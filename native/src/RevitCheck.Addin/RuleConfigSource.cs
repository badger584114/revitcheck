using System.IO;
using Autodesk.Revit.DB;
using RevitCheck.Addin.Commands;
using RevitCheck.Core.Checks;

namespace RevitCheck.Addin;

/// <summary>
/// Finds and loads the per-document <see cref="RuleConfig"/>, so a
/// project's category names, schedule column headings and tolerances are a
/// file a person can edit rather than a rebuild of the add-in.
/// </summary>
/// <remarks>
/// <para>
/// Stored under LocalApplicationData keyed by
/// <see cref="DocumentPaths.SafeBaseName"/>, exactly as
/// <see cref="CheckingSessionHost.SessionFilePathFor"/> already stores
/// per-document session state, and for the same reason: a cloud-worksharing
/// model has no usable <c>PathName</c> to sit beside (PLANNING.md §15).
/// </para>
/// <para>
/// A missing file is normal and silent-by-default in behaviour terms - the
/// compiled defaults are used, which is what every command did
/// unconditionally before 2026-09-07 - but never silent in reporting:
/// <see cref="Resolve"/> hands back a description saying which was used, so
/// a run's own output can state whether it was configured for this model or
/// running on figures calibrated against a different one.
/// </para>
/// </remarks>
internal static class RuleConfigSource
{
    /// <summary>
    /// The conventional name for this model's config as a portable file -
    /// what it is called when it sits beside the capture, or in Forma.
    /// </summary>
    public static string FileNameFor(Document doc) =>
        DocumentPaths.SafeBaseName(doc) + RuleConfigSerializer.FileSuffix;

    /// <summary>
    /// Replaces this model's working copy with <paramref name="json"/>,
    /// after checking it parses.
    /// </summary>
    /// <remarks>
    /// Added 2026-09-09, per the user: the config must be able to live off
    /// the machine, in Forma alongside the model, so that nothing depends on
    /// a file on one person's C: drive. The local copy stays - it is what
    /// makes a run need no prompts, and what works offline - but it is now a
    /// cache of a portable artefact rather than the only copy in existence.
    /// This is also the only way to clear a stale config without hunting
    /// through LocalApplicationData, which blocked two consecutive real runs
    /// (PLANNING.md §23).
    /// </remarks>
    public static void Install(Document doc, string json)
    {
        // Parse first: a file that won't load must not replace one that
        // will, and Loads refuses a newer schema rather than misreading it.
        RuleConfigSerializer.Loads(json);
        File.WriteAllText(PathFor(doc), json);
    }

    /// <summary>The raw JSON of this model's working copy, or null if it has none.</summary>
    public static string? ReadRaw(Document doc)
    {
        try
        {
            var path = PathFor(doc);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deletes this model's working copy, so the next run uses the compiled defaults.</summary>
    public static bool Reset(Document doc)
    {
        var path = PathFor(doc);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public static string PathFor(Document doc)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitCheck");
        Directory.CreateDirectory(root);
        return Path.Combine(root, DocumentPaths.SafeBaseName(doc) + RuleConfigSerializer.FileSuffix);
    }

    /// <summary>
    /// The config for this document, plus a one-line description of where
    /// it came from for the run's own output. A file that exists but won't
    /// parse is reported as such and the defaults are used - a broken file
    /// must not look like a configured one.
    /// </summary>
    public static (RuleConfig Config, string Description) Resolve(Document doc)
    {
        string path;
        try
        {
            path = PathFor(doc);
        }
        catch (Exception ex)
        {
            return (new RuleConfig(), $"Using built-in defaults (could not resolve a config path: {ex.Message}).");
        }

        if (!File.Exists(path))
        {
            return (new RuleConfig(),
                "Using built-in defaults - no per-model config found. These figures were calibrated against a " +
                $"different model; run Capture Model to write a starter config to:\n{path}");
        }

        try
        {
            // What the file pins is reported, not just that a file was
            // used. A config written before 2026-09-09 records every
            // setting as it stood when the model was first captured, so it
            // silently overrides every later recalibration - which is
            // exactly what happened to model 100302 between 09-07 and
            // 09-09. Only the run can say so; nothing else can see it.
            var (config, overrides) = RuleConfigSerializer.LoadWithOverrides(path);
            var description = $"Using per-model config:\n{path}";

            var provenance = RuleConfigSerializer.DescribeProvenance(File.ReadAllText(path));
            description += provenance is null
                ? "\n\nIt records no date - written before 2026-09-09, so it pins every setting as it stood " +
                  "then, including any recalibrated since. Re-import or reset it if that is not deliberate."
                : $"\n{provenance}";
            if (overrides.Count > 0)
            {
                description +=
                    $"\n\nIt overrides {overrides.Count} built-in default(s) - check these are still what this " +
                    "project wants, since a value recorded here does not track later recalibration:\n  " +
                    string.Join("\n  ", overrides);
            }

            return (config, description);
        }
        catch (Exception ex)
        {
            return (new RuleConfig(),
                $"Using built-in defaults - the per-model config at\n{path}\ncould not be read: {ex.Message}");
        }
    }
}
