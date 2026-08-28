using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Wires <c>revitcheck.pile_chain_bearing_consistency</c> up to a real
/// Revit document - the second real ribbon button for either pile check
/// (PLANNING.md §16 Stage 2; the Core-side check itself, including the
/// end-to-end real-data validation against a real pile-layout view, was
/// built and tested 2026-08-26, PLANNING.md §14). Whole-model, standalone -
/// see <see cref="PileModelScheduleConsistencyCommand"/>'s own remarks on
/// what that means and what Stage 3 will later add.
/// </summary>
/// <remarks>
/// <c>writeBcf: true</c> - same reasoning as
/// <see cref="PileModelScheduleConsistencyCommand"/>: reconstructing a
/// chain's real bearing from live geometry and comparing it to the drafted
/// call is already a verdict, not a triage candidate.
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public class PileChainBearingConsistencyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            message = "No active document.";
            return Result.Failed;
        }

        var config = new RuleConfig();

        MetadataCollectionResult piles;
        try
        {
            // Same category-scoped, live-position collection as
            // PileModelScheduleConsistencyCommand - see its own remarks.
            piles = RevitMetadataElementSource.Collect(
                doc,
                categories: new[] { BuiltInCategory.OST_StructuralFoundation },
                populateLivePosition: true);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Pile Chain Bearing", $"Could not collect piles:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        DimensionCollectionResult dims;
        try
        {
            // Reuses the dimension/text-note adapter wholesale - chain
            // reconstruction needs the same tag-to-tag dimensions the
            // dimension checks already collect, plus the TextNotes it now
            // also collects (see RevitDimensionSource.CollectTextNotes).
            dims = RevitDimensionSource.Collect(doc);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Pile Chain Bearing", $"Could not collect dimensions/text notes:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var model = new RevitModel
        {
            DocTitle = doc.Title,
            Elements = piles.Elements,
            Sheets = dims.Sheets,
            Views = dims.Views,
            Dimensions = dims.Dimensions,
            TextNotes = dims.TextNotes,
            ExtractionErrors = piles.ExtractionErrors.Concat(dims.ExtractionErrors).ToList(),
            ExcludedWorksets = dims.ExcludedWorksets,
        };

        var issues = PileChainBearingConsistencyCheck.Run(model, config);

        var summary = $"{issues.Count} issue(s) found ({piles.Elements.Count} pile(s), {dims.Dimensions.Count} dimension(s), " +
            $"{dims.TextNotes.Count} text note(s) checked)" +
            (model.ExtractionErrors.Count > 0 ? $", {model.ExtractionErrors.Count} extraction error(s)" : "") +
            ".";

        string? outputPath;
        try
        {
            outputPath = IssueOutput.WriteNextToModel(doc, issues, "pile_chain_bearing_consistency", "RevitCheck - Pile Chain Bearing");
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Pile Chain Bearing",
                $"{summary}\n\nBut the results file could not be written:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Succeeded;
        }

        if (outputPath is null)
        {
            TaskDialog.Show("RevitCheck - Pile Chain Bearing", $"{summary}\n\nSave cancelled - nothing written.");
            return Result.Succeeded;
        }

        TaskDialog.Show("RevitCheck - Pile Chain Bearing",
            $"{summary}\n\nWritten to (JSON, CSV and BCF, same folder):\n{outputPath}");

        return Result.Succeeded;
    }
}
