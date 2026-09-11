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
    // Three panels, not one, and the split is the workflow distinction
    // itself rather than cosmetic tidying (2026-09-07). "Dimension
    // Checking" holds the button that raises triage plus exactly the
    // checks that can resolve it; "Model Checks" holds the ones that
    // report independently and resolve nothing triage raised. That
    // difference cost a whole round of real confusion to surface
    // (PLANNING.md §21) - a reviewer should be able to read it off the
    // ribbon instead.
    private const string CapturePanelName = "Capture";
    private const string DimensionPanelName = "Dimension Checking";
    private const string ModelPanelName = "Model Checks";

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

        var capturePanel = application.CreateRibbonPanel(TabName, CapturePanelName);
        var dimensionPanel = application.CreateRibbonPanel(TabName, DimensionPanelName);
        var modelPanel = application.CreateRibbonPanel(TabName, ModelPanelName);

        var assemblyPath = Assembly.GetExecutingAssembly().Location;

        // Left-to-right still matches the order someone actually runs
        // things in (confirmed 2026-08-24), now grouped: capture, then the
        // triage workflow and its investigation checks, then the standalone
        // checks that report on their own.
        var captureButton = new PushButtonData(
            "RevitCheck.CaptureModel",
            "Capture\nModel",
            assemblyPath,
            typeof(CaptureModelCommand).FullName)
        {
            ToolTip = "Write a full model sweep (every model category, plus sheets/views/dimensions " +
                      "and schedule headers) to a JSON capture file - a point-in-time snapshot, not a " +
                      "live sync - so checks can be developed and tested off this machine. Also writes " +
                      "a starter config for a model that has none, listing the categories this project " +
                      "actually uses. Asks what to cover: the whole document, the active view, or a " +
                      "mapping file's scope view.",
        };

        SetIcons(captureButton, "CaptureModel");

        capturePanel.AddItem(captureButton);

        var ruleConfigButton = new PushButtonData(
            "RevitCheck.RuleConfig",
            "Rule\nConfig",
            assemblyPath,
            typeof(RuleConfigCommand).FullName)
        {
            ToolTip = "Show this model's config - where it is, when it was written, and every setting it " +
                      "holds away from the built-in defaults - and export it for Forma, import one back, or " +
                      "reset to defaults. The config belongs alongside the model's capture rather than only " +
                      "on this machine, and a config written before 2026-09-09 pins every setting as it stood " +
                      "then, including tolerances recalibrated since.",
        };

        SetIcons(ruleConfigButton, "CaptureModel");

        capturePanel.AddItem(ruleConfigButton);

        var checkDimensionsButton = new PushButtonData(
            "RevitCheck.CheckDimensions",
            "Check\nDimensions",
            assemblyPath,
            typeof(CheckDimensionsCommand).FullName)
        {
            ToolTip = "Open a view, run this, settle it, move on. Runs every dimension check that applies to " +
                      "the active view - drafted dimensions against the model geometry the view shows, spot " +
                      "elevations, pile dimensions against the real pile spacing, pile chain bearings - and " +
                      "reconciles them against that view's triage in one " +
                      "pass. Says which dimension types the view actually contains, so you don't need to know " +
                      "which check applies before running it.",
        };

        SetIcons(checkDimensionsButton, "DimensionProvenance");

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

        dimensionPanel.AddItem(dimensionTriageButton);
        // Straight after triage: triage raises a view, this settles it.
        dimensionPanel.AddItem(checkDimensionsButton);

        var pileModelScheduleButton = new PushButtonData(
            "RevitCheck.PileModelScheduleConsistency",
            "Pile Model/\nSchedule",
            assemblyPath,
            typeof(PileModelScheduleConsistencyCommand).FullName)
        {
            ToolTip = "For each pile visible in the active view, compares its own live position " +
                      "(a fresh GetProjectPosition call) against the pile schedule's row for it - " +
                      "catches a pile moved in the model without the schedule's Dynamo script being " +
                      "rerun. Open the pile layout view before running this. Reports " +
                      "independently - it does not resolve Dimension Triage items.",
        };

        SetIcons(pileModelScheduleButton, "PileModelSchedule");

        modelPanel.AddItem(pileModelScheduleButton);

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

        dimensionPanel.AddItem(pileChainBearingButton);

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

        dimensionPanel.AddItem(spotElevationButton);

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

        modelPanel.AddItem(metadataButton);

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
