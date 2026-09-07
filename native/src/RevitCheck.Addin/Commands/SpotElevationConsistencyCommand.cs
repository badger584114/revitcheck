using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Wires <c>revitcheck.spot_elevation_consistency</c> up to a real
/// Revit document - the first check to verify against raw solid geometry
/// rather than a schedule or a live parameter (PLANNING.md §16/§18).
/// </summary>
/// <remarks>
/// <para>
/// <b>Renamed from "Abutment Elevation", 2026-09-02, per the user's own
/// direction.</b> Built and proven against abutments, but nothing about it
/// is abutment-specific - it checks any Spot Elevation in the active view
/// against any real nearby geometry, no category filter anywhere (see
/// <see cref="SpotElevationConsistencyCheck"/>'s own remarks). The real
/// organizing axis for this project's checking tools isn't element type
/// (piles vs. abutments vs. deck) or view type (plan vs. section) - it's
/// dimension type plus how its provenance resolves (Spot vs. linear;
/// direct-to-geometry vs. witness-line), which is what actually determines
/// the verification technique. Naming this "Abutment Elevation" implied a
/// scope restriction that was never really there.
/// </para>
/// <para>
/// <b>Dual-mode from the start</b> - unlike both pile commands, which
/// started standalone-only and gained session integration later (PLANNING.md
/// §16 Stage 3), this one goes straight in: real machine confirmation
/// (2026-09-02) already showed the standalone path working cleanly (3 of 3
/// Spot Elevations confirmed, 0 issues, first real run), and this check's
/// findings are dimension-ElementId-keyed from the start (unlike
/// <see cref="PileModelScheduleConsistencyCommand"/>'s pile-keyed findings,
/// which "stand alone" - see that class's own remarks), so no
/// <c>ExpandByElementIdList</c>/rollup-unrolling step or view-context
/// patching is needed the way <see cref="PileChainBearingConsistencyCommand"/>
/// needs both: <see cref="SpotElevationConsistencyCheck.RunWithScope"/>'s
/// issues already carry each Spot Elevation's own ElementId plus its real
/// ViewId/ViewName/SheetNo (resolved via <c>RevitModel.ViewById</c> inside
/// the check itself, since it operates per-dimension rather than
/// whole-model the way chain reconstruction does).
/// </para>
/// <para>
/// If <see cref="CheckingSessionHost.Session"/> is null, this command
/// writes results directly - the original, standalone behaviour, same
/// shape as <see cref="MetadataReconciliationCommand"/>, <c>writeBcf: true</c>
/// (a confirmed mismatch here is already a verdict, not triage). If a
/// session is active, results are routed into it via
/// <see cref="CheckingSession.RecordInvestigation"/> instead - a genuine
/// "couldn't determine" outcome (no nearby geometry, no drafted value)
/// carries <see cref="InvestigationReconciliation.ManualReviewCategory"/>,
/// not a plain coverage/geometry category, specifically so
/// <c>InvestigationReconciliation.Reconcile</c> routes it to
/// <c>NeedsManualReview</c> rather than wrongly auto-exporting it as a
/// confirmed problem - see the check's own remarks on this.
/// </para>
/// <para>
/// <b>Scoped to the active view, not the whole document</b> - same
/// reasoning both pile commands already give: a real solid-geometry walk
/// per Spot Elevation is comparatively expensive, and checking the
/// view someone has open is the point, not sweeping the whole document.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public class SpotElevationConsistencyCommand : IExternalCommand
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
            message = "No active view - open a view showing the Spot Elevations you want to check before running this.";
            return Result.Failed;
        }

        // Per-model config if this project has one, compiled defaults
        // otherwise - either way the run's own output says which
        // (RuleConfigSource's remarks).
        var (config, configDescription) = RuleConfigSource.Resolve(doc);

        DimensionCollectionResult collected;
        try
        {
            // populateNearbyShelfFaces: true is the real, comparatively
            // expensive opt-in (a solid-geometry walk per Spot Elevation in
            // view) - see RevitDimensionSource.NearbyHorizontalFaces's own
            // remarks. scopeView also narrows dimension collection itself
            // to the active view, same as both pile commands.
            collected = RevitDimensionSource.Collect(doc, scopeView: activeView, populateNearbyShelfFaces: true);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Spot Elevation", $"Could not collect the model:\n\n{ExceptionMessage.Full(ex)}");
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
        };

        var (issues, investigatedElementIds) = SpotElevationConsistencyCheck.RunWithScope(model, config);
        var spotCount = collected.Dimensions.Count(d => d.IsSpot);

        var summary = $"{issues.Count} issue(s) found ({spotCount} Spot Elevation(s) in view '{activeView.Name}')" +
            (model.ExtractionErrors.Count > 0 ? $", {model.ExtractionErrors.Count} extraction error(s)" : "") +
            "." +
            ExtractionErrorSample.Format(model.ExtractionErrors);
        summary += "\n\n" + configDescription;

        if (CheckingSessionHost.Session is { } session)
        {
            var viewId = activeView.Id.Value;
            session.RecordInvestigation(viewId, investigatedElementIds, issues);

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

            TaskDialog.Show("RevitCheck - Spot Elevation", summary + sessionNote);
            return Result.Succeeded;
        }

        string? outputPath;
        try
        {
            outputPath = IssueOutput.WriteNextToModel(doc, issues, "spot_elevation_consistency", "RevitCheck - Spot Elevation");
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Spot Elevation",
                $"{summary}\n\nBut the results file could not be written:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Succeeded;
        }

        if (outputPath is null)
        {
            TaskDialog.Show("RevitCheck - Spot Elevation", $"{summary}\n\nSave cancelled - nothing written.");
            return Result.Succeeded;
        }

        TaskDialog.Show("RevitCheck - Spot Elevation",
            $"{summary}\n\nWritten to (JSON, CSV and BCF, same folder):\n{outputPath}");

        return Result.Succeeded;
    }
}
