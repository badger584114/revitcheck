using System.IO;
using Autodesk.Revit.DB;
using Microsoft.Win32;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Prompts for a save location and writes an issue list as JSON (the
/// complete, lossless record), CSV (for reviewing/filtering in a
/// spreadsheet), and BCF (for Forma - the proven Revit → BCF → Forma →
/// Revit round trip, PLANNING.md §12) side by side, all sharing the base
/// filename the user picked.
/// </summary>
/// <remarks>
/// Factored out of <c>MetadataReconciliationCommand</c>/<c>DimensionProvenanceCommand</c>/
/// <c>DimensionOverrideConsistencyCommand</c>, which each had their own
/// near-identical writer - same "worth factoring out once a second/third
/// caller needs it" reasoning <see cref="ExceptionMessage"/> already gives.
///
/// Originally derived a save location automatically from
/// <c>Document.PathName</c> - changed 2026-08-25 after that failed for two
/// real reasons found on the actual Revit machine: (1) a genuine bug (see
/// <see cref="DocumentPaths"/>'s remarks), and (2) the user's own
/// preference, having seen <c>CaptureModelCommand</c>'s save dialog,
/// asked for the same control here rather than a silently-chosen folder.
/// Prompting sidesteps the whole "is <c>doc.PathName</c> a real path"
/// question entirely, which is the more robust fix regardless of (1).
/// </remarks>
internal static class IssueOutput
{
    /// <summary>
    /// Prompts for a JSON save location (suggested name:
    /// <c>{model}.{kind}.json</c>), then writes the matching
    /// <c>.csv</c> and one or more BCF files alongside it, all sharing
    /// whatever base name the user actually chose - so renaming the
    /// suggested filename in the dialog renames every sibling file too,
    /// not just the JSON. Returns the JSON path, or null if the user
    /// cancelled the dialog (results were computed but nothing was written).
    /// </summary>
    public static string? WriteNextToModel(Document doc, List<Issue> issues, string kind, string dialogTitle)
    {
        var suggestedName = DocumentPaths.SafeBaseName(doc);
        var dialog = new SaveFileDialog
        {
            Title = $"{dialogTitle} - save results as",
            FileName = $"{suggestedName}.{kind}.json",
            DefaultExt = ".json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var jsonPath = dialog.FileName;
        var directory = Path.GetDirectoryName(jsonPath) ?? Path.GetTempPath();
        // The stem the user actually picked (dialog suggests "{model}.{kind}",
        // but they're free to rename it) - every sibling file is named off
        // this, not off the model/kind again, so a rename in the dialog is
        // honoured consistently across json/csv/bcf.
        var stem = Path.GetFileNameWithoutExtension(jsonPath);

        var csvPath = Path.Combine(directory, $"{stem}.csv");
        IssueCsvWriter.Write(issues, csvPath);
        var written = IssueJsonWriter.Write(issues, jsonPath);

        // doc.Title (not stem) is what a Forma reviewer sees as the BCF
        // project name - the on-disk filename is disambiguated by the stem
        // prefix below instead, so this stays the honest, readable model
        // name rather than the filesystem-safe stem. Safe read - see
        // DocumentPaths' remarks on why doc.Title/doc.PathName can throw.
        var modelTitle = TryReadTitle(doc) ?? stem;
        foreach (var (fileName, bytes) in IssueBcfWriter.ToBcfFiles(issues, modelTitle))
        {
            File.WriteAllBytes(Path.Combine(directory, $"{stem}.{fileName}"), bytes);
        }

        return written;
    }

    private static string? TryReadTitle(Document doc)
    {
        try
        {
            return doc.Title;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
