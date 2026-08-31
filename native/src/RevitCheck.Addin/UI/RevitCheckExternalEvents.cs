using Autodesk.Revit.UI;

namespace RevitCheck.Addin.UI;

/// <summary>
/// Constructs the two <see cref="ExternalEvent"/>/handler pairs the
/// checklist window's own buttons need, once, from
/// <see cref="RevitCheckApplication.OnStartup"/> - a valid API context, and
/// the only place these need to be created (an <see cref="ExternalEvent"/>
/// is meant to be long-lived, not recreated per window instance).
/// </summary>
internal static class RevitCheckExternalEvents
{
    public static OpenViewExternalEventHandler? OpenViewHandler { get; private set; }

    public static ExternalEvent? OpenView { get; private set; }

    public static ExportReconciledBcfExternalEventHandler? ExportBcfHandler { get; private set; }

    public static ExternalEvent? ExportBcf { get; private set; }

    public static void Initialize()
    {
        OpenViewHandler = new OpenViewExternalEventHandler();
        OpenView = ExternalEvent.Create(OpenViewHandler);

        ExportBcfHandler = new ExportReconciledBcfExternalEventHandler();
        ExportBcf = ExternalEvent.Create(ExportBcfHandler);
    }
}
