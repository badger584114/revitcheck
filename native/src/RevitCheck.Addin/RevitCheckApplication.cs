using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Commands;

namespace RevitCheck.Addin;

/// <summary>
/// Ribbon wiring - the "no IExternalCommands, no ribbon, no .addin manifest"
/// gap native/README.md named as the real precondition for archiving
/// pyRevit. Started narrow (Metadata Reconciliation plus its Capture Model
/// dev-loop companion, proving the wiring itself on the simplest case
/// first); the two dimension-check buttons were added once the dimension
/// adapter existed to back them (PLANNING.md §14).
/// </summary>
public class RevitCheckApplication : IExternalApplication
{
    private const string TabName = "RevitCheck";
    private const string PanelName = "Checks";

    public Result OnStartup(UIControlledApplication application)
    {
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
        // (the dev-loop snapshot), then the dimension checks, then
        // metadata/data reconciliation last.
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

        var dimensionProvenanceButton = new PushButtonData(
            "RevitCheck.DimensionProvenance",
            "Dimension\nProvenance",
            assemblyPath,
            typeof(DimensionProvenanceCommand).FullName)
        {
            ToolTip = "For each dimension, do its references resolve to model geometry, a datum, " +
                      "or view-specific linework? Reports triage, not verdicts - which dimensions " +
                      "can't be trusted to track the model, not whether any specific one is wrong.",
        };

        SetIcons(dimensionProvenanceButton, "DimensionProvenance");

        panel.AddItem(dimensionProvenanceButton);

        var dimensionOverridesButton = new PushButtonData(
            "RevitCheck.DimensionOverrideConsistency",
            "Dimension\nOverrides",
            assemblyPath,
            typeof(DimensionOverrideConsistencyCommand).FullName)
        {
            ToolTip = "Where a drafter typed over the measured value, is the difference explainable " +
                      "as rounding to a sensible grid? Always reports how much was checkable.",
        };

        SetIcons(dimensionOverridesButton, "DimensionOverrides");

        panel.AddItem(dimensionOverridesButton);

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
