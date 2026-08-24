using System.IO;
using Autodesk.Revit.DB;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Writes an issue list as JSON (the complete, lossless record), CSV (for
/// reviewing/filtering in a spreadsheet), and BCF (for Forma - the proven
/// Revit → BCF → Forma → Revit round trip, PLANNING.md §12) into the same
/// folder as the model, all sharing one base filename derived from it.
/// </summary>
/// <remarks>
/// Factored out of <c>MetadataReconciliationCommand</c>/<c>DimensionProvenanceCommand</c>/
/// <c>DimensionOverrideConsistencyCommand</c>, which each had their own
/// near-identical JSON+CSV-only version of this - the same "worth factoring
/// out once a second/third caller needs it" reasoning
/// <see cref="ExceptionMessage"/> already gives, extended here to wire in
/// the BCF export native/README.md's "Next" section named as still open
/// (2026-08-25).
/// </remarks>
internal static class IssueOutput
{
    /// <summary>
    /// Writes <paramref name="issues"/> as <c>{model}.{kind}.json</c>,
    /// <c>{model}.{kind}.csv</c>, and one or more
    /// <c>{model}.{kind}.&lt;bcf-filename&gt;</c> files (BCF's own naming
    /// already carries the model title and, past Forma's 100-issue cap, a
    /// <c>-NNN-of-MMM</c> suffix - the <c>{kind}</c> prefix here only
    /// exists so three different checks run against the same model don't
    /// overwrite each other's BCF output in the same folder). Returns the
    /// JSON path, the primary one to show the user.
    /// </summary>
    public static string WriteNextToModel(Document doc, List<Issue> issues, string kind)
    {
        var (directory, baseName) = DocumentPaths.Resolve(doc);

        var jsonPath = Path.Combine(directory, $"{baseName}.{kind}.json");
        var csvPath = Path.Combine(directory, $"{baseName}.{kind}.csv");
        IssueCsvWriter.Write(issues, csvPath);
        var written = IssueJsonWriter.Write(issues, jsonPath);

        // doc.Title (not baseName.kind) is what a Forma reviewer sees as
        // the BCF project name - the on-disk filename is disambiguated by
        // the {kind} prefix below instead, so this stays the honest,
        // readable model name rather than a filesystem-safe slug of one.
        foreach (var (fileName, bytes) in IssueBcfWriter.ToBcfFiles(issues, doc.Title ?? ""))
        {
            File.WriteAllBytes(Path.Combine(directory, $"{baseName}.{kind}.{fileName}"), bytes);
        }

        return written;
    }
}
