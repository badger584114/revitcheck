using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Commands;
using RevitCheck.Addin.UI;

namespace RevitCheck.Addin;

/// <summary>
/// Ribbon wiring - the "no IExternalCommands, no ribbon, no .addin manifest"
/// gap native/README.md named as the real precondition for archiving
/// pyRevit. Started narrow (Metadata Reconciliation plus its Capture Model
/// dev-loop companion, proving the wiring itself on the simplest case
/// first); the two dimension-check buttons were added once the dimension
/// adapter existed to back them (PLANNING.md §14); the two pile-check
/// buttons were added once their own adapter work (live project position,
/// per-reference LocalPoint, TextNotes, schedule reading) was built
/// (PLANNING.md §16 Stage 2). The two dimension buttons were replaced by
/// one combined Dimension Triage button, and the pile buttons gained
/// dual-mode session integration, in PLANNING.md §16 Stage 3. Spot
/// Elevation - the first check verifying against real solid geometry
/// rather than a schedule or parameter (PLANNING.md §18) - went dual-mode
/// from the start, real machine confirmation already in hand before the
/// button existed. Built and proven against abutments, renamed from
/// "Abutment Elevation" the same day once real use showed nothing about it
/// is actually abutment-specific.
/// </summary>
public class RevitCheckApplication : IExternalApplication
{
    private const string TabName = "RevitCheck";
    private const string PanelName = "Checks";

    public Result OnStartup(UIControlledApplication application)
    {
        // Constructs the checklist window's two ExternalEvent/handler
        // pairs once, here - a valid API context, and the only place they
        // need to be created (see RevitCheckExternalEvents' own remarks).
        RevitCheckExternalEvents.Initialize();

        try
        {
            application.CreateRibbonTab(TabName);
        }
        catch (Exception)
        {
            // CreateRibbonTab throws if the tab already exists (e.g. a
            // second add-in reload in the same session) - not a startup
            // failure, just skip creating it again.
        }

        var panel = application.CreateRibbonPanel(TabName, PanelName);

        var assemblyPath = Assembly.GetExecutingAssembly().Location;

        // Left-to-right order matches the order someone actually runs
        // things in, confirmed with the user 2026-08-24: capture first
        // (the dev-loop snapshot), then triage, then the two pile
        // investigation checks, then metadata/data reconciliation last -
        // the final order PLANNING.md §16 Stage 3 named, now that Dimension
        // Provenance/Overrides are one combined Dimension Triage button.
        var captureButton = new PushButtonData(
            "RevitCheck.CaptureModel",
            "Capture\nModel",
            assemblyPath,
            typeof(CaptureModelCommand).FullName)
        {
            ToolTip = "Write a full model sweep (metadata, sheets/views/dimensions) to a JSON " +
                      "capture file - a point-in-time snapshot, not a live sync - so checks can be " +
                      "developed and tested off this machine. Prompts for a mapping file only to " +
                      "read its scope view for the metadata half; its fields and any CSV are not " +
                      "used here.",
        };

        SetIcons(captureButton, "CaptureModel");

        panel.AddItem(captureButton);

        var dimensionTriageButton = new PushButtonData(
            "RevitCheck.DimensionTriage",
            "Dimension\nTriage",
            assemblyPath,
            typeof(DimensionTriageCommand).FullName)
        {
            ToolTip = "Runs Dimension Provenance and Dimension Overrides together and opens a " +
                      "checklist you cycle through view by view: open a flagged view, run the " +
                      "relevant pile check while it's active, and its dimensions get marked " +
                      "resolved/flagged automatically. Reports triage, not verdicts, until an " +
                      "investigation check has actually looked at a given dimension.",
        };

        // Reuses the existing DimensionProvenance icon - no dedicated
        // DimensionTriage icon exists yet (cosmetic, not blocking).
        SetIcons(dimensionTriageButton, "DimensionProvenance");

        panel.AddItem(dimensionTriageButton);

        var pileModelScheduleButton = new PushButtonData(
            "RevitCheck.PileModelScheduleConsistency",
            "Pile Model/\nSchedule",
            assemblyPath,
            typeof(PileModelScheduleConsistencyCommand).FullName)
        {
            ToolTip = "For each pile visible in the active view, compares its own live position " +
                      "(a fresh GetProjectPosition call) against the pile schedule's row for it - " +
                      "catches a pile moved in the model without the schedule's Dynamo script being " +
                      "rerun. Open the pile layout view before running this.",
        };

        SetIcons(pileModelScheduleButton, "PileModelSchedule");

        panel.AddItem(pileModelScheduleButton);

        var pileChainBearingButton = new PushButtonData(
            "RevitCheck.PileChainBearingConsistency",
            "Pile Chain\nBearing",
            assemblyPath,
            typeof(PileChainBearingConsistencyCommand).FullName)
        {
            ToolTip = "Reconstructs each real pile chain's own bearing from live model geometry " +
                      "in the active view (tag-to-pile proximity matching) and compares it against " +
                      "the drafted bearing call nearest to it. Open the pile layout view before " +
                      "running this.",
        };

        SetIcons(pileChainBearingButton, "PileChainBearing");

        panel.AddItem(pileChainBearingButton);

        var spotElevationButton = new PushButtonData(
            "RevitCheck.SpotElevationConsistency",
            "Spot\nElevation",
            assemblyPath,
            typeof(SpotElevationConsistencyCommand).FullName)
        {
            ToolTip = "For each Spot Elevation visible in the active view, searches nearby real solid " +
                      "geometry (any category - not filtered, since no single category is stable enough " +
                      "across this project's own history, let alone across clients) and compares the " +
                      "drafted value against the nearest real horizontal face. Open the view you want " +
                      "to check before running this - works on any Spot Elevation, not tied to any " +
                      "particular structure type.",
        };

        // Reuses the Pile Chain Bearing icon - no dedicated Spot Elevation
        // icon exists yet (cosmetic, not blocking), same precedent
        // Dimension Triage set reusing Dimension Provenance's.
        SetIcons(spotElevationButton, "PileChainBearing");

        panel.AddItem(spotElevationButton);

        var metadataButton = new PushButtonData(
            "RevitCheck.MetadataReconciliation",
            "Metadata\nReconciliation",
            assemblyPath,
            typeof(MetadataReconciliationCommand).FullName)
        {
            ToolTip = "Join captured model elements to an external reference CSV via a mapping " +
                      "file, and flag missing or mismatched fields. Mapping file and CSV are both " +
                      "chosen per run.",
        };

        SetIcons(metadataButton, "MetadataReconciliation");

        panel.AddItem(metadataButton);

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static void SetIcons(PushButtonData button, string baseName)
    {
        var iconsDir = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
            "Resources", "Icons");

        var large = Path.Combine(iconsDir, $"{baseName}32.png");
        var small = Path.Combine(iconsDir, $"{baseName}16.png");

        if (File.Exists(large))
        {
            button.LargeImage = new BitmapImage(new Uri(large));
        }

        if (File.Exists(small))
        {
            button.Image = new BitmapImage(new Uri(small));
        }
    }
}
