using System.IO;
using Autodesk.Revit.DB;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// A safe suggested filename base for a save dialog, derived from a
/// <see cref="Document"/> without ever throwing.
/// </summary>
/// <remarks>
/// Found running Metadata Reconciliation against a real Revit Cloud
/// Worksharing model (BIM 360 / Autodesk Construction Cloud / Autodesk
/// Docs), 2026-08-25 - not a hypothetical, and a real bug in this class's
/// first version. That version guarded <c>Path.GetDirectoryName</c>/
/// <c>GetFileNameWithoutExtension</c> against throwing on a bad
/// <c>PathName</c> string, but read <c>doc.PathName</c> itself as a bare
/// argument expression, outside any try/catch - if the *property getter*
/// is what throws for a cloud model (plausible: Revit's own
/// <c>NotSupportedException</c> message text matches .NET Framework's
/// internal path-normalization error, which a getter could easily trigger
/// internally before ever handing a string back), no amount of guarding
/// the calls made *with* that string helps. Confirmed the fix didn't
/// clear the real error on a second real run - this version guards the
/// property reads themselves, not just what's done with their result, and
/// (the more robust fix) the three check commands now prompt for a save
/// location via dialog instead of deriving one from <c>doc.PathName</c> at
/// all - see <see cref="IssueOutput"/>. This class only still exists to
/// suggest a sensible default filename for that dialog.
/// </remarks>
internal static class DocumentPaths
{
    public static string SafeBaseName(Document doc)
    {
        var docPath = SafeRead(() => doc.PathName);
        if (!string.IsNullOrEmpty(docPath))
        {
            var name = SafeRead(() => Path.GetFileNameWithoutExtension(docPath));
            if (!string.IsNullOrEmpty(name))
            {
                // IsNullOrEmpty doesn't narrow `name` on this compiler's
                // nullable-analysis surface - same situation
                // RevitMetadataElementSource.ReadParameters already notes.
                return name!;
            }
        }

        return Sanitize(SafeRead(() => doc.Title)) ?? "model";
    }

    private static string? SafeRead(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            // Whatever just failed - reading the property, or parsing what
            // it returned - "couldn't determine a name" is exactly as
            // recoverable as "there wasn't one", so this folds into the
            // same fallback rather than needing its own case.
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
