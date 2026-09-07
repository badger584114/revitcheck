using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Wires <c>revitcheck.pile_chain_bearing_consistency</c> up to a real
/// Revit document - the second real ribbon button for either pile check
/// (PLANNING.md §16 Stage 2; the Core-side check itself, including the
/// end-to-end real-data validation against a real pile-layout view, was
/// built and tested 2026-08-26, PLANNING.md §14).
/// </summary>
/// <remarks>
/// <para>
/// <b>Dual-mode, added PLANNING.md §16 Stage 3.</b> If
/// <see cref="CheckingSessionHost.Session"/> is null, this command writes
/// results directly - the original, standalone behaviour, same shape as
/// <see cref="MetadataReconciliationCommand"/>, <c>writeBcf: true</c>
/// (this check's findings are already verdicts, not triage - a pile
/// chain's reconstructed bearing either agrees with the drafted call or it
/// doesn't). If a session is active, results are routed into it instead
/// via <see cref="CheckingSession.RecordInvestigation"/> - see that
/// method's own remarks for the dimension-linked shape this check uses,
/// and <see cref="InvestigationReconciliation.ExpandByElementIdList"/>'s
/// remarks for why expansion has to happen first (a real correctness bug
/// found and designed around before any code was written).
/// </para>
/// <para>
/// <b>Scoped to the active view, not the whole document - a real bug fixed
/// 2026-08-28, the day after this command was first built.</b> The first
/// version swept every pile, every sheeted view's dimensions, and every
/// sheeted view's text notes document-wide - 281 piles, 1297 dimensions,
/// 3790 text notes on the real model this project develops against. This
/// check's whole design is "verify the chain in the view someone has open,"
/// not "process the entire drawing set at once" - see
/// <see cref="PileModelScheduleConsistencyCommand"/>'s own remarks for the
/// pile half of this (the same 281-vs-~47 over-collection
/// <c>InspectDimensionGeometry.pushbutton</c> already found once).
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public class PileChainBearingConsistencyCommand : IExternalCommand
{
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

        // Per-model config if this project has one, compiled defaults
        // otherwise - either way the run's own output says which
        // (RuleConfigSource's remarks).
        var (config, configDescription) = RuleConfigSource.Resolve(doc);

        MetadataCollectionResult piles;
        try
        {
            // Same active-view-scoped, category-scoped, live-position
            // collection as PileModelScheduleConsistencyCommand - see its
            // own remarks.
            piles = RevitMetadataElementSource.Collect(
                doc,
                categories: new[] { BuiltInCategory.OST_StructuralFoundation },
                populateLivePosition: true,
                scopeView: activeView);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Pile Chain Bearing", $"Could not collect piles:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        DimensionCollectionResult dims;
        try
        {
            // Reuses the dimension/text-note adapter wholesale, scoped to
            // the same active view - chain reconstruction needs the same
            // tag-to-tag dimensions the dimension checks already collect,
            // plus the TextNotes it now also collects (see
            // RevitDimensionSource.CollectTextNotes), but only ever within
            // the one view actually being checked (see class remarks).
            dims = RevitDimensionSource.Collect(doc, scopeView: activeView);
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

        var (issues, investigatedDimensionIds) = PileChainBearingConsistencyCheck.RunWithScope(model, config);

        var summary = $"{issues.Count} issue(s) found ({piles.Elements.Count} pile(s), {dims.Dimensions.Count} dimension(s), " +
            $"{dims.TextNotes.Count} text note(s) in view '{activeView.Name}' checked)" +
            (model.ExtractionErrors.Count > 0 ? $", {model.ExtractionErrors.Count} extraction error(s)" : "") +
            "." +
            ExtractionErrorSample.Format(model.ExtractionErrors);
        summary += "\n\n" + configDescription;

        if (CheckingSessionHost.Session is { } session)
        {
            var viewId = activeView.Id.Value;
            var viewInfo = dims.Views.FirstOrDefault(v => v.ElementId == viewId);
            // Expand each chain-keyed issue into one copy per dimension id
            // first - the whole reason ExpandByElementIdList exists (see
            // its own remarks): a flagged chain's issue carries
            // ElementId = <a pile>, not a dimension, so feeding it to
            // RecordInvestigation unexpanded would silently reconcile the
            // affected dimensions as clean. Each expanded copy's
            // ViewId/ViewName/SheetNo are patched in from the view this
            // command already has in hand - ExpandByElementIdList's own
            // remarks flag this as the known gap a per-view caller should
            // close, since PileChainBearingConsistencyCheck never sets
            // them itself (a whole-model check has no view of its own).
            var expanded = InvestigationReconciliation.ExpandByElementIdList(issues, "dimension_element_ids")
                .Select(i => PatchViewContext(i, viewId, activeView.Name, viewInfo?.SheetNo))
                .ToList();

            session.RecordInvestigation(viewId, investigatedDimensionIds, expanded);

            var sessionNote = session.FindView(viewId) is not null
                ? "\n\nRecorded against the active checking session - see the checklist window."
                : "\n\nNo checklist row exists yet for this view (Dimension Triage found nothing to flag " +
                  "here), so these results were not recorded in the session - informational only.";

            try
            {
                CheckingSessionHost.Autosave();
            }
            catch (Exception ex)
            {
                sessionNote += $"\n\nThe session could not be saved to disk:\n\n{ExceptionMessage.Full(ex)}";
            }

            CheckingSessionHost.Window?.Refresh();

            TaskDialog.Show("RevitCheck - Pile Chain Bearing", summary + sessionNote);
            return Result.Succeeded;
        }

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

    /// <summary>
    /// Fills in <see cref="Issue.ViewId"/>/<see cref="Issue.ViewName"/>/
    /// <see cref="Issue.SheetNo"/> from the active view this command
    /// already knows, only if the issue doesn't already carry one -
    /// <see cref="InvestigationReconciliation.ExpandByElementIdList"/>'s
    /// own remarks name exactly this gap and exactly this fix.
    /// </summary>
    private static Issue PatchViewContext(Issue issue, long viewId, string? viewName, string? sheetNo)
    {
        if (issue.ViewId is not null)
        {
            return issue;
        }

        return new Issue
        {
            RuleId = issue.RuleId,
            Category = issue.Category,
            Description = issue.Description,
            Severity = issue.Severity,
            ElementId = issue.ElementId,
            ViewId = viewId,
            ViewName = viewName,
            SheetNo = sheetNo,
            SuggestedFix = issue.SuggestedFix,
            UniqueId = issue.UniqueId,
        };
    }
}
