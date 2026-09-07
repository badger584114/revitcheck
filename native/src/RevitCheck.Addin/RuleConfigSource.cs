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
            return (RuleConfigSerializer.Load(path), $"Using per-model config:\n{path}");
        }
        catch (Exception ex)
        {
            return (new RuleConfig(),
                $"Using built-in defaults - the per-model config at\n{path}\ncould not be read: {ex.Message}");
        }
    }
}
