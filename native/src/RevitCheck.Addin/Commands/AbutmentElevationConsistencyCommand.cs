using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Wires <c>revitcheck.abutment_elevation_consistency</c> up to a real
/// Revit document - the second element-type check in the per-element-type
/// pattern the two pile checks established (PLANNING.md §16/§18), and the
/// first to verify against raw solid geometry rather than a schedule or a
/// live parameter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Standalone only for now, not dual-mode</b> - matches this codebase's
/// own precedent: <see cref="PileModelScheduleConsistencyCommand"/> and
/// <see cref="PileChainBearingConsistencyCommand"/> both started this way
/// (PLANNING.md §16 Stage 2) before checking-session integration was added
/// once real people were using them (Stage 3). Session/dimension-triage
/// reconciliation is a deliberate, named follow-up here, not an oversight -
/// this check's findings are keyed on Spot Elevation ElementIds, which
/// genuinely can appear in a drafted-view rollup's own
/// <c>drafted_dimension_ids</c> (unlike the pile-model-schedule check,
/// which is keyed on pile ElementIds and never overlaps dimension triage at
/// all), so real dual-mode integration here would need its own
/// <c>RunWithScope</c>-style overload the way
/// <see cref="PileChainBearingConsistencyCheck"/> has, not the pile-model-
/// schedule check's simpler "stands alone" path.
/// </para>
/// <para>
/// <b>Collection is scoped to the active view</b>, same reasoning both pile
/// commands already give: a real solid-geometry walk per Spot Elevation is
/// comparatively expensive, and checking the abutment view someone has open
/// is the point, not sweeping the whole document.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public class AbutmentElevationConsistencyCommand : IExternalCommand
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
            message = "No active view - open the abutment elevation/section view to check before running this.";
            return Result.Failed;
        }

        var config = new RuleConfig();

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
            TaskDialog.Show("RevitCheck - Abutment Elevation", $"Could not collect the model:\n\n{ExceptionMessage.Full(ex)}");
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

        var issues = AbutmentElevationConsistencyCheck.Run(model, config);
        var spotCount = collected.Dimensions.Count(d => d.IsSpot);

        var summary = $"{issues.Count} issue(s) found ({spotCount} Spot Elevation(s) in view '{activeView.Name}')" +
            (model.ExtractionErrors.Count > 0 ? $", {model.ExtractionErrors.Count} extraction error(s)" : "") +
            "." +
            ExtractionErrorSample.Format(model.ExtractionErrors);

        string? outputPath;
        try
        {
            outputPath = IssueOutput.WriteNextToModel(doc, issues, "abutment_elevation_consistency", "RevitCheck - Abutment Elevation");
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Abutment Elevation",
                $"{summary}\n\nBut the results file could not be written:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Succeeded;
        }

        if (outputPath is null)
        {
            TaskDialog.Show("RevitCheck - Abutment Elevation", $"{summary}\n\nSave cancelled - nothing written.");
            return Result.Succeeded;
        }

        TaskDialog.Show("RevitCheck - Abutment Elevation",
            $"{summary}\n\nWritten to (JSON, CSV and BCF, same folder):\n{outputPath}");

        return Result.Succeeded;
    }
}
