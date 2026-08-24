using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Wires <c>revit.dimension_provenance</c> up to a real Revit document -
/// the dimension-side counterpart of <see cref="MetadataReconciliationCommand"/>,
/// same shape: adapter -&gt; <c>RevitModel</c> -&gt; check -&gt; JSON/CSV -&gt;
/// <c>TaskDialog</c> summary. No mapping file or CSV to pick here, unlike
/// metadata reconciliation - this check only ever needs the live document
/// and a <see cref="RuleConfig"/>, so there's nothing to prompt for.
/// </summary>
/// <remarks>
/// No <see cref="IssueGrouping"/> here - that's specifically shaped around
/// metadata's (family, type, field, values) key, and dimension issues don't
/// carry family/type. <c>DimensionProvenanceCheck</c>'s own view rollup
/// (<c>RollUpFullyDraftedViews</c>) is the equivalent "don't flood the
/// output" mechanism for this rule, already built into the check itself.
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public class DimensionProvenanceCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            message = "No active document.";
            return Result.Failed;
        }

        DimensionCollectionResult collected;
        try
        {
            collected = RevitDimensionSource.Collect(doc);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Dimension Provenance", $"Could not collect the model:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var model = new RevitModel
        {
            DocTitle = doc.Title,
            RevitVersion = commandData.Application.Application.VersionNumber,
            CapturedAt = DateTime.UtcNow.ToString("O"),
            Sheets = collected.Sheets,
            Views = collected.Views,
            Dimensions = collected.Dimensions,
            ExtractionErrors = collected.ExtractionErrors,
            ExcludedWorksets = collected.ExcludedWorksets,
        };

        var config = new RuleConfig();
        var issues = DimensionProvenanceCheck.Run(model, config);

        string? outputPath = null;
        try
        {
            outputPath = WriteIssuesNextToModel(doc, issues);
        }
        catch (Exception ex)
        {
            // Not being able to write the file is not a reason to hide the
            // result from the user - report the count either way.
            TaskDialog.Show("RevitCheck - Dimension Provenance",
                $"{issues.Count} issue(s) found, but the results file could not be written:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Succeeded;
        }

        TaskDialog.Show("RevitCheck - Dimension Provenance",
            $"{issues.Count} issue(s) found ({collected.Dimensions.Count} dimension(s) across " +
            $"{collected.Views.Count} view(s) checked)" +
            (collected.ExtractionErrors.Count > 0 ? $", {collected.ExtractionErrors.Count} extraction error(s)" : "") +
            $".\n\nWritten to (JSON and CSV, same folder):\n{outputPath}");

        return Result.Succeeded;
    }

    /// <summary>See MetadataReconciliationCommand.WriteIssuesNextToModel - same shape, different rule id in the filename.</summary>
    private static string WriteIssuesNextToModel(Document doc, List<Core.Issues.Issue> issues)
    {
        var docPath = doc.PathName;
        var directory = string.IsNullOrEmpty(docPath) ? Path.GetTempPath() : Path.GetDirectoryName(docPath) ?? Path.GetTempPath();
        var baseName = string.IsNullOrEmpty(docPath) ? (doc.Title ?? "model") : Path.GetFileNameWithoutExtension(docPath);
        var jsonPath = Path.Combine(directory, $"{baseName}.dimension_provenance.json");
        var csvPath = Path.Combine(directory, $"{baseName}.dimension_provenance.csv");
        IssueCsvWriter.Write(issues, csvPath);
        return IssueJsonWriter.Write(issues, jsonPath);
    }
}
