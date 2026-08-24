using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Commands;

namespace RevitCheck.Addin;

/// <summary>
/// Ribbon wiring - the "no IExternalCommands, no ribbon, no .addin manifest"
/// gap native/README.md named as the real precondition for archiving
/// pyRevit. Deliberately one button today (Metadata Reconciliation): the
/// dimension-checks adapter is a separate, later phase (native/README.md's
/// "What's not done"), and this file's job is proving the wiring itself on
/// the simpler case first, not shipping every check at once.
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
