using System.IO.Compression;
using System.Xml.Linq;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// Ported test-by-test from <c>tests/revit/test_bcf.py</c> - same rationale
/// as <c>DimensionProvenanceRuleTests</c>/<c>DimensionOverrideConsistencyCheckTests</c>
/// (PLANNING.md §12): the Python suite is the spec, not just a sanity check.
/// </summary>
/// <remarks>
/// Two of the Python tests (<c>test_same_finding_gets_the_same_topic_guid_across_runs</c>,
/// <c>test_topic_guid_tracks_issue_id_not_incidental_fields</c>) locate the
/// Topic Guid via <c>zf.namelist()[1].split("/")[0]</c>. Verified against a
/// real run of the Python writer (2026-08-25): entry index 1 is
/// <c>project.bcfp</c>, not a topic folder, so that indexing trivially
/// compares <c>"project.bcfp" == "project.bcfp"</c> regardless of the
/// finding - a latent weakness in the reference suite, not a behaviour to
/// reproduce here. Ported using the same "find the entry ending with
/// <c>markup.bcf</c>" pattern the rest of that same Python file already
/// uses correctly (<c>TestMarkup</c>/<c>TestViewpoint</c>), which is
/// self-evidently what both tests intended to check.
/// </remarks>
public class IssueBcfWriterTests
{
    private static Issue MakeIssue(
        string ruleId = "revit.dimension_provenance",
        string category = "geometry",
        string description = "A finding.",
        string severity = "medium",
        long? elementId = null,
        long? viewId = null,
        string? viewName = null,
        string? sheetNo = null,
        string? uniqueId = null) => new()
    {
        RuleId = ruleId,
        Category = category,
        Description = description,
        Severity = severity,
        ElementId = elementId,
        ViewId = viewId,
        ViewName = viewName,
        SheetNo = sheetNo,
        UniqueId = uniqueId,
    };

    private static ZipArchive Unzip(byte[] data) => new(new MemoryStream(data), ZipArchiveMode.Read);

    private static string TopicGuidFor(ZipArchive zip)
    {
        var markup = zip.Entries.First(e => e.FullName.EndsWith("markup.bcf"));
        return markup.FullName.Split('/')[0];
    }

    private static string ReadEntry(ZipArchive zip, string suffix)
    {
        var entry = zip.Entries.First(e => e.FullName.EndsWith(suffix));
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    // ---------------------------------------------------------------- Splitting

    [Fact]
    public void NoIssuesProducesNoFiles()
    {
        Assert.Empty(IssueBcfWriter.ToBcfFiles(new List<Issue>()));
    }

    [Fact]
    public void IssuesUnderTheCapProduceOneFile()
    {
        var issues = Enumerable.Range(0, 5).Select(i => MakeIssue(elementId: i)).ToList();
        var files = IssueBcfWriter.ToBcfFiles(issues, maxIssuesPerFile: 100);
        Assert.Single(files);
        Assert.EndsWith(".bcf", files[0].FileName);
    }

    [Fact]
    public void IssuesOverTheCapSplitIntoMultipleFiles()
    {
        var issues = Enumerable.Range(0, 250).Select(i => MakeIssue(elementId: i)).ToList();
        var files = IssueBcfWriter.ToBcfFiles(issues, maxIssuesPerFile: 100);
        Assert.Equal(3, files.Count);
        var names = files.Select(f => f.FileName).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names); // -001-, -002-, -003- sort in order
        Assert.All(names, n => Assert.Contains("of-003", n));
    }

    [Fact]
    public void ExactlyAtTheCapIsOneFile()
    {
        var issues = Enumerable.Range(0, 100).Select(i => MakeIssue(elementId: i)).ToList();
        var files = IssueBcfWriter.ToBcfFiles(issues, maxIssuesPerFile: 100);
        Assert.Single(files);
    }

    [Fact]
    public void DefaultCapMatchesForma()
    {
        Assert.Equal(100, IssueBcfWriter.DefaultMaxIssuesPerFile);
    }

    [Fact]
    public void FilenamesAreDistinct()
    {
        var issues = Enumerable.Range(0, 150).Select(i => MakeIssue(elementId: i)).ToList();
        var files = IssueBcfWriter.ToBcfFiles(issues, maxIssuesPerFile: 100);
        Assert.Equal(files.Count, files.Select(f => f.FileName).Distinct().Count());
    }

    [Fact]
    public void EveryTopicLandsInExactlyOneFile()
    {
        var issues = Enumerable.Range(0, 120).Select(i => MakeIssue(elementId: i, description: $"finding {i}")).ToList();
        var files = IssueBcfWriter.ToBcfFiles(issues, maxIssuesPerFile: 100);
        var totalTopics = 0;
        foreach (var (_, data) in files)
        {
            using var zip = Unzip(data);
            totalTopics += zip.Entries.Select(e => e.FullName.Split('/')[0]).Where(top => zip.Entries.Any(e => e.FullName == $"{top}/markup.bcf")).Distinct().Count();
        }

        Assert.Equal(120, totalTopics);
    }

    // ---------------------------------------------------------------- Topic identity

    [Fact]
    public void SameFindingGetsTheSameTopicGuidAcrossRuns()
    {
        // The whole point: re-exporting after a model change should let
        // Forma recognise unchanged findings as the same topic, not mint a
        // fresh one every run.
        var issue = MakeIssue(elementId: 5, viewId: 10, sheetNo: "S101");
        var first = IssueBcfWriter.ToBcfFiles(new List<Issue> { issue });
        var second = IssueBcfWriter.ToBcfFiles(new List<Issue> { issue });
        using var zip1 = Unzip(first[0].Bytes);
        using var zip2 = Unzip(second[0].Bytes);
        Assert.Equal(TopicGuidFor(zip1), TopicGuidFor(zip2));
    }

    [Fact]
    public void DifferentFindingsGetDifferentTopicGuids()
    {
        var a = MakeIssue(elementId: 5, description: "finding A");
        var b = MakeIssue(elementId: 6, description: "finding B");
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { a, b });
        using var zip = Unzip(files[0].Bytes);
        var topicDirs = zip.Entries.Select(e => e.FullName.Split('/')[0]).Distinct().Where(d => zip.Entries.Any(e => e.FullName == $"{d}/markup.bcf")).ToList();
        Assert.Equal(2, topicDirs.Count);
    }

    [Fact]
    public void TopicGuidTracksIssueIdNotIncidentalFields()
    {
        // severity isn't part of IssueId's identity (Issue.cs), so
        // re-tiering a rule in config must not re-mint the Topic Guid
        // either - otherwise config changes would look like new findings
        // to Forma the same way they would to a human re-running the tool.
        var a = MakeIssue(elementId: 5, severity: "high");
        var b = MakeIssue(elementId: 5, severity: "low");
        Assert.Equal(a.IssueId, b.IssueId);
        using var zipA = Unzip(IssueBcfWriter.ToBcfFiles(new List<Issue> { a })[0].Bytes);
        using var zipB = Unzip(IssueBcfWriter.ToBcfFiles(new List<Issue> { b })[0].Bytes);
        Assert.Equal(TopicGuidFor(zipA), TopicGuidFor(zipB));
    }

    // ---------------------------------------------------------------- BCF version

    [Fact]
    public void EveryFileDeclares21()
    {
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { MakeIssue() }, maxIssuesPerFile: 100);
        using var zip = Unzip(files[0].Bytes);
        var root = XDocument.Parse(ReadEntry(zip, "bcf.version")).Root!;
        Assert.Equal("2.1", root.Attribute("VersionId")!.Value);
    }

    // ---------------------------------------------------------------- project.bcfp

    [Fact]
    public void EveryFileCarriesAProjectBcfp()
    {
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { MakeIssue() }, modelTitle: "TEST-BRIDGE", maxIssuesPerFile: 100);
        using var zip = Unzip(files[0].Bytes);
        Assert.NotNull(zip.GetEntry("project.bcfp"));
        var root = XDocument.Parse(ReadEntry(zip, "project.bcfp")).Root!;
        Assert.Equal("TEST-BRIDGE", root.Element("Project")!.Element("Name")!.Value);
        Assert.False(string.IsNullOrEmpty(root.Element("Project")!.Attribute("ProjectId")!.Value));
    }

    [Fact]
    public void ProjectGuidIsDeterministicForTheSameModelTitle()
    {
        var filesA = IssueBcfWriter.ToBcfFiles(new List<Issue> { MakeIssue() }, modelTitle: "TEST-BRIDGE");
        var filesB = IssueBcfWriter.ToBcfFiles(new List<Issue> { MakeIssue() }, modelTitle: "TEST-BRIDGE");
        using var zipA = Unzip(filesA[0].Bytes);
        using var zipB = Unzip(filesB[0].Bytes);
        var guidA = XDocument.Parse(ReadEntry(zipA, "project.bcfp")).Root!.Element("Project")!.Attribute("ProjectId")!.Value;
        var guidB = XDocument.Parse(ReadEntry(zipB, "project.bcfp")).Root!.Element("Project")!.Attribute("ProjectId")!.Value;
        Assert.Equal(guidA, guidB);
    }

    [Fact]
    public void DifferentModelTitlesGetDifferentProjectGuids()
    {
        var filesA = IssueBcfWriter.ToBcfFiles(new List<Issue> { MakeIssue() }, modelTitle: "BRIDGE-A");
        var filesB = IssueBcfWriter.ToBcfFiles(new List<Issue> { MakeIssue() }, modelTitle: "BRIDGE-B");
        using var zipA = Unzip(filesA[0].Bytes);
        using var zipB = Unzip(filesB[0].Bytes);
        var guidA = XDocument.Parse(ReadEntry(zipA, "project.bcfp")).Root!.Element("Project")!.Attribute("ProjectId")!.Value;
        var guidB = XDocument.Parse(ReadEntry(zipB, "project.bcfp")).Root!.Element("Project")!.Attribute("ProjectId")!.Value;
        Assert.NotEqual(guidA, guidB);
    }

    // ---------------------------------------------------------------- Markup

    [Fact]
    public void TopicCarriesTitleDescriptionAndStatus()
    {
        var issue = MakeIssue(
            description: "Dimension measures detail linework.",
            sheetNo: "S101",
            viewName: "SECTION A-A",
            severity: "high",
            elementId: 42);
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { issue });
        using var zip = Unzip(files[0].Bytes);
        var topic = XDocument.Parse(ReadEntry(zip, "markup.bcf")).Root!.Element("Topic")!;
        Assert.Equal("Open", topic.Attribute("TopicStatus")!.Value);
        Assert.Contains("S101", topic.Element("Title")!.Value);
        Assert.Contains("SECTION A-A", topic.Element("Title")!.Value);
        Assert.Equal("Dimension measures detail linework.", topic.Element("Description")!.Value);
        Assert.Equal("High", topic.Element("Priority")!.Value);
    }

    [Theory]
    [InlineData("high", "High")]
    [InlineData("medium", "Normal")]
    [InlineData("low", "Low")]
    public void SeverityMapsToPriority(string severity, string expected)
    {
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { MakeIssue(severity: severity) });
        using var zip = Unzip(files[0].Bytes);
        var priority = XDocument.Parse(ReadEntry(zip, "markup.bcf")).Root!.Element("Topic")!.Element("Priority")!.Value;
        Assert.Equal(expected, priority);
    }

    [Fact]
    public void XmlSpecialCharactersAreEscaped()
    {
        var issue = MakeIssue(description: "Typed as <5mm> but measures 6mm & \"drifts\".", elementId: 1);
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { issue });
        using var zip = Unzip(files[0].Bytes);
        // Must parse as valid XML despite the raw text containing '<', '&', '"'.
        var description = XDocument.Parse(ReadEntry(zip, "markup.bcf")).Root!.Element("Topic")!.Element("Description")!.Value;
        Assert.Equal("Typed as <5mm> but measures 6mm & \"drifts\".", description);
    }

    [Fact]
    public void TitleFallsBackToRuleIdWithNoLocation()
    {
        var issue = MakeIssue(ruleId: "revit.capture_coverage", category: "coverage", sheetNo: null, viewName: null);
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { issue });
        using var zip = Unzip(files[0].Bytes);
        var title = XDocument.Parse(ReadEntry(zip, "markup.bcf")).Root!.Element("Topic")!.Element("Title")!.Value;
        Assert.Equal("revit.capture_coverage", title);
    }

    // ---------------------------------------------------------------- Viewpoint

    [Fact]
    public void IssueWithUniqueIdGetsAPinnedViewpoint()
    {
        var issue = MakeIssue(elementId: 5, uniqueId: "d919e769-2a86-4b1c-a9c4-00000000abcd-0002f1e3");
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { issue });
        using var zip = Unzip(files[0].Bytes);
        var vpRoot = XDocument.Parse(ReadEntry(zip, "viewpoint.bcfv")).Root!;
        var component = vpRoot.Element("Components")!.Element("Selection")!.Element("Component")!;
        Assert.Equal("d919e769-2a86-4b1c-a9c4-00000000abcd-0002f1e3", component.Attribute("AuthoringToolId")!.Value);
        Assert.Equal("Revit", component.Attribute("OriginatingSystem")!.Value);
        Assert.Null(component.Attribute("IfcGuid"));

        var markupRoot = XDocument.Parse(ReadEntry(zip, "markup.bcf")).Root!;
        var viewpoints = markupRoot.Element("Viewpoints")!;
        Assert.Equal("viewpoint.bcfv", viewpoints.Element("Viewpoint")!.Value);
    }

    [Fact]
    public void ViewpointCarriesACamera()
    {
        // Added 2026-08-22 (Python side) after a real Forma import reported
        // the export as empty - ruling out "no camera" as the cause.
        var issue = MakeIssue(elementId: 5, uniqueId: "abc-123");
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { issue });
        using var zip = Unzip(files[0].Bytes);
        var root = XDocument.Parse(ReadEntry(zip, "viewpoint.bcfv")).Root!;
        var camera = root.Element("OrthogonalCamera");
        Assert.NotNull(camera);
        Assert.NotNull(camera!.Element("CameraViewPoint"));
        Assert.NotNull(camera.Element("CameraDirection"));
        Assert.NotNull(camera.Element("CameraUpVector"));
    }

    [Fact]
    public void CoverageIssueWithNoElementStillGetsAViewpoint()
    {
        // Changed 2026-08-22 (Python side): a real Forma import rejected a
        // file with "no viewpoint file found for one or more BCF topics",
        // so every Topic gets one now - just without a Component pin,
        // since there's genuinely nothing to select.
        var issue = MakeIssue(ruleId: "revit.capture_coverage", category: "coverage", elementId: null, uniqueId: null);
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { issue });
        using var zip = Unzip(files[0].Bytes);
        Assert.NotNull(zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("viewpoint.bcfv")));
        var markupRoot = XDocument.Parse(ReadEntry(zip, "markup.bcf")).Root!;
        Assert.NotNull(markupRoot.Element("Viewpoints"));

        var vpRoot = XDocument.Parse(ReadEntry(zip, "viewpoint.bcfv")).Root!;
        Assert.NotNull(vpRoot.Element("OrthogonalCamera"));
        Assert.Null(vpRoot.Element("Components"));
    }

    [Fact]
    public void ElementIdWithoutUniqueIdStillGetsAViewpoint()
    {
        // UniqueId is what makes the pin durable, but a capture taken
        // before that field existed should still produce a pinned
        // viewpoint - just without AuthoringToolId to anchor it.
        var issue = MakeIssue(elementId: 5, uniqueId: null);
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { issue });
        using var zip = Unzip(files[0].Bytes);
        Assert.NotNull(zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("viewpoint.bcfv")));
        var root = XDocument.Parse(ReadEntry(zip, "viewpoint.bcfv")).Root!;
        var component = root.Element("Components")!.Element("Selection")!.Element("Component")!;
        Assert.Null(component.Attribute("AuthoringToolId"));
    }

    // ---------------------------------------------------------------- DeterministicGuid parity
    //
    // Cross-checked against real Python output (2026-08-25):
    //   python3 -c "import uuid; ns = uuid.UUID('6f6e4b9a-2b1c-4b7a-9b3a-9f6a8f0c9a4e'); \
    //     print(uuid.uuid5(ns, 'project\x1ftest-bridge'))"
    // and similarly for the other two cases - not just checked against the
    // RFC, matching this project's own translation-risk discipline
    // (PLANNING.md §12).

    [Fact]
    public void DeterministicGuidMatchesPythonUuid5_ProjectTestBridge()
    {
        var files = IssueBcfWriter.ToBcfFiles(new List<Issue> { MakeIssue() }, modelTitle: "test-bridge");
        using var zip = Unzip(files[0].Bytes);
        var guid = XDocument.Parse(ReadEntry(zip, "project.bcfp")).Root!.Element("Project")!.Attribute("ProjectId")!.Value;
        Assert.Equal("0c9c88f3-8ffe-5a57-a599-a166fd584061", guid);
    }
}
