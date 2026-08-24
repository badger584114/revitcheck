using System.IO;
using Autodesk.Revit.DB;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Resolves a safe local filename/directory from a <see cref="Document"/> -
/// tolerating <see cref="Document.PathName"/> not being a real filesystem
/// path at all.
/// </summary>
/// <remarks>
/// Found running Metadata Reconciliation against a real Revit Cloud
/// Worksharing model (BIM 360 / Autodesk Construction Cloud / Autodesk
/// Docs), 2026-08-25 - not a hypothetical. For a cloud-hosted model,
/// <c>Document.PathName</c> is a URN-shaped string
/// (<c>"BIM 360://ProjectName/Model.rvt"</c>, or similarly for Autodesk
/// Docs), not a local path. <see cref="Path.GetDirectoryName(string)"/>/
/// <see cref="Path.GetFileNameWithoutExtension(string)"/> both throw
/// <see cref="NotSupportedException"/> ("The given path's format is not
/// supported") on the <c>"://"</c> - .NET treats any colon outside the
/// drive-letter position as invalid. The previous code only guarded against
/// <c>PathName</c> being empty (a doc that was never saved locally), which
/// is a different case from this one: a cloud model's <c>PathName</c> is
/// non-empty and genuinely unparseable as a path.
/// </remarks>
internal static class DocumentPaths
{
    /// <summary>A safe (directory, baseName) pair for writing output files next to the model. Falls back to the temp folder plus a sanitized <see cref="Document.Title"/> whenever <see cref="Document.PathName"/> isn't a real local path - a never-saved-locally doc and a cloud-worksharing doc both land here, for the same reason: neither has a usable local directory.</summary>
    public static (string Directory, string BaseName) Resolve(Document doc)
    {
        if (TryLocalPath(doc.PathName) is { } local)
        {
            return local;
        }

        return (Path.GetTempPath(), SafeBaseName(doc));
    }

    /// <summary>A safe suggested filename base for a save dialog - never throws, even against a cloud-worksharing model's URN-shaped PathName.</summary>
    public static string SafeBaseName(Document doc)
    {
        var docPath = doc.PathName;
        if (!string.IsNullOrEmpty(docPath))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(docPath);
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
            catch (Exception)
            {
                // Not a local filesystem path - fall through to doc.Title.
            }
        }

        return Sanitize(doc.Title) ?? "model";
    }

    private static (string Directory, string BaseName)? TryLocalPath(string? docPath)
    {
        if (string.IsNullOrEmpty(docPath))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(docPath);
            var baseName = Path.GetFileNameWithoutExtension(docPath);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(baseName))
            {
                return null;
            }

            return (directory, baseName);
        }
        catch (Exception)
        {
            // NotSupportedException ("The given path's format is not
            // supported") is the one actually seen against a real cloud
            // model - caught broadly anyway, matching this project's own
            // "fail open" convention at extraction/path boundaries
            // (e.g. RevitDimensionSource.WorksetName): a bad path here is
            // exactly as recoverable as a bad workset lookup, and worth the
            // same treatment.
            return null;
        }
    }

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
