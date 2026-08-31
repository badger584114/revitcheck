using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Microsoft.Win32;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Json;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Prompts for a save location and writes an issue list as JSON (the
/// complete, lossless record), CSV (for reviewing/filtering in a
/// spreadsheet), and - by default - BCF (for Forma, the proven Revit →
/// BCF → Forma → Revit round trip, PLANNING.md §12) side by side, all
/// sharing the base filename the user picked.
/// </summary>
/// <remarks>
/// <para>
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
/// </para>
/// <para>
/// <paramref name="writeBcf"/> defaulted to true from when this was
/// factored out - correct for <c>MetadataReconciliationCommand</c>, whose
/// findings are already verdicts. Changed 2026-08-26: the dimension
/// checks (<c>revit.dimension_provenance</c>/<c>revit.
/// dimension_override_consistency</c>) report *triage* (a real run found
/// ~250 issues, most of them "this needs investigating," not "this is
/// wrong" - see CLAUDE.md/PLANNING.md §14), and the BCF wiring on those
/// two commands was only ever there to prove the Revit → BCF → Forma
/// round trip (PLANNING.md §12) - not to be the thing that actually ships
/// to Forma. Confirmed by the user directly: shipping 250 triage findings
/// to Forma is not the intended outcome. BCF export for dimension
/// findings now belongs to a later reconciliation stage (still to be
/// built) that prunes triage against investigation-check verdicts first -
/// see PLANNING.md §14. Both dimension commands now pass
/// <c>writeBcf: false</c>; JSON/CSV still write unconditionally, since
/// they remain useful for review and are what that later stage will
/// consume.
/// </para>
/// </remarks>
internal static class IssueOutput
{
    /// <summary>
    /// Prompts for a JSON save location (suggested name:
    /// <c>{model}.{kind}.json</c>), then writes the matching
    /// <c>.csv</c> alongside it and, when <paramref name="writeBcf"/> is
    /// true (the default), one or more BCF files too - all sharing
    /// whatever base name the user actually chose, so renaming the
    /// suggested filename in the dialog renames every sibling file too,
    /// not just the JSON. Returns the JSON path, or null if the user
    /// cancelled the dialog (results were computed but nothing was written).
    /// </summary>
    public static string? WriteNextToModel(Document doc, List<Issue> issues, string kind, string dialogTitle, bool writeBcf = true)
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

        if (writeBcf)
        {
            // doc.Title (not stem) is what a Forma reviewer sees as the BCF
            // project name - the on-disk filename is disambiguated by the
            // stem prefix below instead, so this stays the honest, readable
            // model name rather than the filesystem-safe stem. Safe read -
            // see DocumentPaths' remarks on why doc.Title/doc.PathName can
            // throw.
            var modelTitle = TryReadTitle(doc) ?? stem;
            foreach (var (fileName, bytes) in IssueBcfWriter.ToBcfFiles(issues, modelTitle))
            {
                File.WriteAllBytes(Path.Combine(directory, $"{stem}.{fileName}"), bytes);
            }
        }

        return written;
    }

    private static readonly JsonSerializerOptions ManualResolutionOptions = new()
    {
        PropertyNamingPolicy = SnakeCaseLowerNamingPolicy.Instance,
        WriteIndented = true,
    };

    /// <summary>
    /// Writes JSON+CSV for a second issue list alongside whatever
    /// <see cref="WriteNextToModel"/>'s dialog already wrote for the
    /// primary list - added for the checklist window's Export Reconciled
    /// BCF button (PLANNING.md §16 Stage 3), which needs to write
    /// confirmed problems (BCF+JSON+CSV, via <see cref="WriteNextToModel"/>)
    /// plus manual-review and still-open-triage items (JSON+CSV only, per
    /// this class's own <c>writeBcf</c> remarks) without a second/third
    /// save dialog interrupting the export. Names the sibling
    /// <c>{stem}.{kind}.json</c>/<c>.csv</c>, same convention
    /// <see cref="WriteNextToModel"/> already uses for its own BCF
    /// siblings.
    /// </summary>
    public static void WriteSibling(string primaryJsonPath, List<Issue> issues, string kind)
    {
        var directory = Path.GetDirectoryName(primaryJsonPath) ?? Path.GetTempPath();
        var stem = Path.GetFileNameWithoutExtension(primaryJsonPath);
        IssueJsonWriter.Write(issues, Path.Combine(directory, $"{stem}.{kind}.json"));
        IssueCsvWriter.Write(issues, Path.Combine(directory, $"{stem}.{kind}.csv"));
    }

    /// <summary>
    /// The manual-dismissal audit trail (<c>CheckingSession.ExportableManualResolutions</c>)
    /// alongside the primary export - CLAUDE.md's "a rule must say how it
    /// reached its conclusion" applies just as much to a human's own
    /// dismissal as to an automated check's finding. Never BCF (nothing
    /// was found, a human judged the view out of scope) and not an
    /// <see cref="Issue"/> list, so this writes its own small JSON rather
    /// than reusing <see cref="WriteSibling"/>. A no-op if there's nothing
    /// to write - an empty audit file next to a real export would read as
    /// "checked, found nothing" rather than "nobody used this feature".
    /// </summary>
    public static void WriteManualResolutionsSibling(string primaryJsonPath, List<ManualResolutionRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(primaryJsonPath) ?? Path.GetTempPath();
        var stem = Path.GetFileNameWithoutExtension(primaryJsonPath);
        var json = JsonSerializer.Serialize(new { count = records.Count, resolutions = records }, ManualResolutionOptions);
        File.WriteAllText(Path.Combine(directory, $"{stem}.manual_resolutions.json"), json);
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
