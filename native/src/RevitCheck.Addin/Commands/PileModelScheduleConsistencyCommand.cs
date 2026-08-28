using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Wires <c>revitcheck.pile_model_schedule_consistency</c> up to a real
/// Revit document - the first real ribbon button for either pile check
/// (PLANNING.md §16 Stage 2; the Core-side check itself was built and
/// tested 2026-08-26, PLANNING.md §14). Standalone: unlike the interactive
/// checking workflow's dual-mode session integration (PLANNING.md §16
/// Stage 3, not yet built), this always writes results directly - same
/// shape as <see cref="MetadataReconciliationCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>writeBcf: true</c> - this check's findings are already verdicts, not
/// triage (a pile's live position either agrees with its schedule row or it
/// doesn't), the same reasoning <see cref="MetadataReconciliationCommand"/>
/// and <c>InvestigationReconciliation</c>'s own remarks already establish
/// for why this check "stands alone" and should never be routed through
/// dimension-triage reconciliation.
/// </para>
/// <para>
/// <b>Pile collection is scoped to the active view, not the whole
/// document - a real bug fixed 2026-08-28, the day after this command was
/// first built.</b> The first version swept the whole document by category
/// alone and pulled in 281 "piles" on the real model this project develops
/// against - the exact same over-collection number
/// <c>InspectDimensionGeometry.pushbutton</c> already found and fixed the
/// same way (CLAUDE.md's "Notes worth not rediscovering": real count is
/// ~43-47, the difference being piles/foundations belonging to unrelated
/// structures elsewhere in a large model). This command's job is checking
/// the pile layout someone has open, not every foundation element in the
/// document - <see cref="RevitMetadataElementSource.Collect"/>'s
/// <c>scopeView</c> parameter now does that. Schedule collection stays
/// whole-document either way (see below) - a schedule isn't "in" a plan
/// view the way a pile element is, and the id-based join already narrows
/// to the one row that matters per pile regardless of how many schedules
/// exist.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public class PileModelScheduleConsistencyCommand : IExternalCommand
{
    private const int MaxListedErrors = 5;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc?.Document;
        if (doc is null)
        {
            message = "No active document.";
            return Result.Failed;
        }

        var activeView = uiDoc!.ActiveView;
        if (activeView is null)
        {
            message = "No active view - open the pile layout view to check before running this.";
            return Result.Failed;
        }

        var config = new RuleConfig();

        MetadataCollectionResult piles;
        try
        {
            // Scoped to the active view (see class remarks - a real
            // over-collection bug this fixes) and the pile category alone,
            // not RevitMetadataElementSource's full DefaultCategories set -
            // populateLivePosition costs a real GetProjectPosition call per
            // element, worth paying only for the category this check
            // actually reads.
            piles = RevitMetadataElementSource.Collect(
                doc,
                categories: new[] { BuiltInCategory.OST_StructuralFoundation },
                populateLivePosition: true,
                scopeView: activeView);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Pile Model/Schedule", $"Could not collect piles:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var scheduleErrors = new List<string>();
        List<ScheduleInfo> schedules;
        try
        {
            schedules = RevitScheduleSource.Collect(doc, scheduleErrors);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Pile Model/Schedule", $"Could not collect schedules:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var model = new RevitModel
        {
            DocTitle = doc.Title,
            Elements = piles.Elements,
            Schedules = schedules,
            ExtractionErrors = piles.ExtractionErrors.Concat(scheduleErrors).ToList(),
        };

        var issues = PileModelScheduleConsistencyCheck.Run(model, config);

        var summary = $"{issues.Count} issue(s) found ({piles.Elements.Count} pile(s) in view '{activeView.Name}', " +
            $"{schedules.Count} captured schedule(s) checked)" +
            (model.ExtractionErrors.Count > 0 ? $", {model.ExtractionErrors.Count} extraction error(s)" : "") +
            "." +
            ExtractionErrorSample.Format(model.ExtractionErrors);

        string? outputPath;
        try
        {
            outputPath = IssueOutput.WriteNextToModel(doc, issues, "pile_model_schedule_consistency", "RevitCheck - Pile Model/Schedule");
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Pile Model/Schedule",
                $"{summary}\n\nBut the results file could not be written:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Succeeded;
        }

        if (outputPath is null)
        {
            TaskDialog.Show("RevitCheck - Pile Model/Schedule", $"{summary}\n\nSave cancelled - nothing written.");
            return Result.Succeeded;
        }

        TaskDialog.Show("RevitCheck - Pile Model/Schedule",
            $"{summary}\n\nWritten to (JSON, CSV and BCF, same folder):\n{outputPath}");

        return Result.Succeeded;
    }
}
