using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Reporting;

/// <summary>
/// Writes Issues as BCF 2.1 (<c>.bcf</c>) files - a line-for-line port of
/// the Python engine's <c>bcf.py</c>, including its hard-won Forma-import
/// fixes (<c>project.bcfp</c>, a default camera on every Viewpoint,
/// <c>&lt;Viewpoint&gt;</c> as a child element rather than an attribute)
/// rather than a fresh implementation against the bare spec. See
/// <c>bcf.py</c>'s own module docstring for the full "why BCF" and "what
/// Forma actually needed" history (PLANNING.md §5d, §12) - not repeated
/// here to avoid the two copies drifting apart.
/// </summary>
/// <remarks>
/// Placed in <c>Reporting/</c> alongside <see cref="IssueJsonWriter"/>/
/// <see cref="IssueCsvWriter"/>/<see cref="IssueGrouping"/>, not as its own
/// top-level module the way Python's <c>bcf.py</c> sits next to
/// <c>report.py</c> - the C# side already collapsed "how issues get handed
/// to a user" into one folder, and BCF is one more output format in that
/// set, not a separately-scoped concern here.
/// </remarks>
public static class IssueBcfWriter
{
    public const string BcfVersion = "2.1";

    /// <summary>Forma's BCF import rejects a file over this many issues (stated by the user, 2026-08-19) - splitting at this boundary keeps every exported file importable on its own, rather than discovering the cap on whichever upload happens to cross it.</summary>
    public const int DefaultMaxIssuesPerFile = 100;

    private const string TopicStatus = "Open";
    private const string TopicType = "Issue";

    // Some real BCF readers truncate or reflow a very long Title. The full
    // text always survives in Description regardless, so Title only needs
    // to be recognisable in a list, not complete.
    private const int MaxTitleLen = 200;

    private static readonly Dictionary<string, string> SeverityPriority = new()
    {
        ["high"] = "High",
        ["medium"] = "Normal",
        ["low"] = "Low",
    };

    // A fixed namespace for deriving deterministic Topic/Viewpoint GUIDs
    // from an Issue's own IssueId - generated once for this project and
    // never regenerated, since regenerating it would silently re-mint
    // every Topic Guid this project has ever exported. Same value bcf.py
    // uses, deliberately - a C#-side export and a Python-side export of
    // the same finding must land on the same Topic Guid.
    private static readonly Guid GuidNamespace = new("6f6e4b9a-2b1c-4b7a-9b3a-9f6a8f0c9a4e");

    /// <summary>
    /// Every issue, as one or more <c>.bcf</c> files of at most
    /// <paramref name="maxIssuesPerFile"/> topics each.
    /// </summary>
    /// <remarks>
    /// Returns (filename, bytes) pairs rather than writing to disk - same
    /// reasoning as <see cref="IssueJsonWriter"/>/<see cref="IssueCsvWriter"/>
    /// staying pure where they can: a command decides where files go, and
    /// this stays testable without touching a filesystem.
    /// <see cref="IssueSorting.SortIssues"/>'s sheet-major ordering is kept,
    /// so which chunk a finding lands in is stable and predictable rather
    /// than dependent on rule-run order.
    /// </remarks>
    public static List<(string FileName, byte[] Bytes)> ToBcfFiles(
        IEnumerable<Issue> issues,
        string modelTitle = "",
        int maxIssuesPerFile = DefaultMaxIssuesPerFile,
        string author = "RevitCheck",
        DateTimeOffset? createdAt = null)
    {
        var ordered = IssueSorting.SortIssues(issues);
        if (ordered.Count == 0)
        {
            return new List<(string, byte[])>();
        }

        var when = (createdAt ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        var baseName = Slugify(modelTitle);

        var chunks = new List<List<Issue>>();
        for (var i = 0; i < ordered.Count; i += maxIssuesPerFile)
        {
            chunks.Add(ordered.Skip(i).Take(maxIssuesPerFile).ToList());
        }

        var files = new List<(string, byte[])>();
        for (var index = 0; index < chunks.Count; index++)
        {
            var filename = chunks.Count == 1
                ? $"{baseName}.bcf"
                : $"{baseName}-{index + 1:000}-of-{chunks.Count:000}.bcf";
            files.Add((filename, WriteBcfContainer(chunks[index], modelTitle, when, author)));
        }

        return files;
    }

    private static byte[] WriteBcfContainer(List<Issue> issues, string modelTitle, string createdAt, string author)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "bcf.version", BcfVersionXml());

            // Same deterministic-GUID reasoning as a Topic's - the same
            // model should get the same project id on every export, not a
            // fresh one each run.
            var projectGuid = DeterministicGuid("project", modelTitle);
            WriteEntry(zip, "project.bcfp", ProjectBcfpXml(projectGuid, modelTitle));

            foreach (var issue in issues)
            {
                var topicGuid = DeterministicGuid(issue.IssueId);
                // Every Topic gets a Viewpoint file, no exceptions - a real
                // Forma import rejected a file with "no viewpoint file
                // found for one or more BCF topics" when this only
                // happened for issues with an element to pin (see bcf.py's
                // module docstring). ViewpointXml still only writes the
                // pin itself when there's a real target.
                var vpGuid = DeterministicGuid(issue.IssueId, "viewpoint");
                WriteEntry(zip, $"{topicGuid}/markup.bcf", MarkupXml(issue, topicGuid, vpGuid, createdAt, author));
                WriteEntry(zip, $"{topicGuid}/viewpoint.bcfv", ViewpointXml(issue, vpGuid));
            }
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string BcfVersionXml() =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Version VersionId=\"{BcfVersion}\"/>\n";

    private static string MarkupXml(Issue issue, string topicGuid, string? vpGuid, string createdAt, string author)
    {
        var priority = SeverityPriority.TryGetValue(issue.Severity, out var mapped) ? mapped : "Normal";
        var lines = new List<string>
        {
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<Markup>",
            $"  <Topic Guid=\"{topicGuid}\" TopicType=\"{TopicType}\" TopicStatus=\"{TopicStatus}\">",
            $"    <Title>{XmlEscape(TopicTitle(issue))}</Title>",
            $"    <Priority>{priority}</Priority>",
            $"    <Description>{XmlEscape(issue.Description)}</Description>",
            $"    <CreationDate>{createdAt}</CreationDate>",
            $"    <CreationAuthor>{XmlEscape(author)}</CreationAuthor>",
            "  </Topic>",
        };

        if (vpGuid is not null)
        {
            // <Viewpoint> is a child element, not an attribute on
            // <Viewpoints> - the self-closing attribute form was this
            // module's original, wrong guess at the shape on the Python
            // side; this is the buildingSMART example shape a real Forma
            // import confirmed. See bcf.py's own note on _markup_xml.
            lines.Add($"  <Viewpoints Guid=\"{vpGuid}\">");
            lines.Add("    <Viewpoint>viewpoint.bcfv</Viewpoint>");
            lines.Add("  </Viewpoints>");
        }

        lines.Add("</Markup>");
        return string.Join("\n", lines) + "\n";
    }

    // A placeholder camera, not a real one - this project doesn't carry a
    // dimension's or view's camera direction/position anywhere in the IR
    // yet, only witness-point origins. Looking at nothing meaningful (the
    // world origin, -Z) rather than the element is the honest trade for
    // now - see bcf.py's own note on _DEFAULT_CAMERA_XML.
    private const string DefaultCameraXml =
        "  <OrthogonalCamera>\n" +
        "    <CameraViewPoint><X>0</X><Y>0</Y><Z>0</Z></CameraViewPoint>\n" +
        "    <CameraDirection><X>0</X><Y>0</Y><Z>-1</Z></CameraDirection>\n" +
        "    <CameraUpVector><X>0</X><Y>1</Y><Z>0</Z></CameraUpVector>\n" +
        "    <ViewToWorldScale>1</ViewToWorldScale>\n" +
        "  </OrthogonalCamera>\n";

    private static string ViewpointXml(Issue issue, string vpGuid)
    {
        // IfcGuid is deliberately omitted rather than emitted empty - this
        // project has no real IFC GlobalId for the element, and a
        // fabricated one risks colliding with (or simply not matching) an
        // unrelated element in whatever IFC export a viewer cross-references
        // against. AuthoringToolId is the honest field for a Revit-only
        // identifier.
        var hasTarget = issue.UniqueId is not null || issue.ElementId is not null;
        var componentsXml = "";
        if (hasTarget)
        {
            var attrs = new List<string> { "OriginatingSystem=\"Revit\"" };
            if (!string.IsNullOrEmpty(issue.UniqueId))
            {
                attrs.Add($"AuthoringToolId=\"{XmlEscape(issue.UniqueId)}\"");
            }

            componentsXml =
                "  <Components>\n" +
                "    <Selection>\n" +
                $"      <Component {string.Join(" ", attrs)}/>\n" +
                "    </Selection>\n" +
                "  </Components>\n";
        }

        return
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            $"<VisualizationInfo Guid=\"{vpGuid}\">\n" +
            DefaultCameraXml +
            componentsXml +
            "</VisualizationInfo>\n";
    }

    private static string ProjectBcfpXml(string projectGuid, string projectName) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<ProjectExtension>\n" +
        $"  <Project ProjectId=\"{projectGuid}\">\n" +
        $"    <Name>{XmlEscape(projectName)}</Name>\n" +
        "  </Project>\n" +
        "</ProjectExtension>\n";

    /// <summary>A short, recognisable label for the topic list - the full finding text is Issue.Description, not this.</summary>
    private static string TopicTitle(Issue issue)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(issue.SheetNo))
        {
            parts.Add($"Sheet {issue.SheetNo}");
        }

        if (!string.IsNullOrEmpty(issue.ViewName))
        {
            parts.Add(issue.ViewName!);
        }

        var title = parts.Count > 0 ? string.Join(" — ", parts) : issue.RuleId;
        if (title.Length > MaxTitleLen)
        {
            title = title.Substring(0, MaxTitleLen - 1) + "…";
        }

        return title;
    }

    /// <summary>Same three-entity default as Python's xml.sax.saxutils.escape - '&amp;' first, so escaping '&lt;'/'&gt;' afterwards doesn't double-escape the ampersands those entities introduce. Quotes are left alone, matching the Python original - every use here is element text, never an XML attribute value.</summary>
    private static string XmlEscape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        // IsNullOrEmpty doesn't narrow `text` on this compiler's
        // nullable-analysis surface - same situation
        // RevitMetadataElementSource.ReadParameters already notes.
        return text!.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string Slugify(string? text)
    {
        var slug = Regex.Replace(text ?? "", "[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
        return slug.Length > 0 ? slug : "revitcheck";
    }

    /// <summary>
    /// RFC 4122 version-5 (SHA-1, namespace-based) UUID - .NET's
    /// <see cref="Guid"/> has no built-in equivalent to Python's
    /// <c>uuid.uuid5</c>. Verified byte-for-byte against real Python
    /// output, not just against the RFC - see
    /// <c>IssueBcfWriterTests</c>' <c>DeterministicGuid</c> fixture cases,
    /// each computed once via a real <c>python3 -c "import uuid; ..."</c>
    /// call against this exact namespace.
    /// </summary>
    private static string DeterministicGuid(params string[] parts)
    {
        var name = string.Join("\x1f", parts);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var namespaceBytes = GuidNamespace.ToByteArray();
        SwapByteOrder(namespaceBytes);

        using var sha1 = SHA1.Create();
        var toHash = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, toHash, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, toHash, namespaceBytes.Length, nameBytes.Length);
        var hash = sha1.ComputeHash(toHash);

        var newGuidBytes = new byte[16];
        Array.Copy(hash, 0, newGuidBytes, 0, 16);
        newGuidBytes[6] = (byte)((newGuidBytes[6] & 0x0F) | (5 << 4)); // version 5
        newGuidBytes[8] = (byte)((newGuidBytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        SwapByteOrder(newGuidBytes);

        return new Guid(newGuidBytes).ToString();
    }

    // Guid's internal byte layout is little-endian for the first three
    // fields (time_low, time_mid, time_hi_and_version); RFC 4122 treats a
    // UUID as one big-endian byte sequence for hashing/formatting purposes.
    // This swap converts between the two, both going in (before hashing
    // the namespace) and coming out (after building the result from the
    // hash) - the standard technique for a UUIDv5 implementation on top of
    // System.Guid.
    private static void SwapByteOrder(byte[] guid)
    {
        SwapBytes(guid, 0, 3);
        SwapBytes(guid, 1, 2);
        SwapBytes(guid, 4, 5);
        SwapBytes(guid, 6, 7);
    }

    private static void SwapBytes(byte[] guid, int left, int right) => (guid[left], guid[right]) = (guid[right], guid[left]);
}
