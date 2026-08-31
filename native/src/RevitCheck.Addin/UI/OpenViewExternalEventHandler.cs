using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Commands;

namespace RevitCheck.Addin.UI;

/// <summary>
/// Opens (activates) one view from the checklist window's Open View
/// button. Revit API calls triggered from a modeless WPF window's own
/// Click handler need the standard <see cref="IExternalEventHandler"/>
/// pattern even though the window runs on Revit's own UI thread - a
/// direct API call from inside a WPF event handler is not a valid Revit
/// API context, only <see cref="IExternalCommand.Execute"/> and an
/// <see cref="IExternalEventHandler.Execute"/> callback are. New to this
/// codebase (every command before Stage 3 runs entirely inside its own
/// <c>Execute</c>, with no modeless window to call back from) but not new
/// to Revit add-ins generally.
/// </summary>
internal sealed class OpenViewExternalEventHandler : IExternalEventHandler
{
    /// <summary>Set by <see cref="ChecklistWindow"/> immediately before <see cref="ExternalEvent.Raise"/> - read once, here, then left as-is until the next request.</summary>
    public long? RequestedViewId { get; set; }

    public void Execute(UIApplication app)
    {
        if (RequestedViewId is not { } viewId)
        {
            return;
        }

        var uiDoc = app.ActiveUIDocument;
        var doc = uiDoc?.Document;
        if (doc is null)
        {
            return;
        }

        View? view;
        try
        {
            view = doc.GetElement(new ElementId(viewId)) as View;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Dimension Triage", $"Could not resolve that view:\n\n{ExceptionMessage.Full(ex)}");
            return;
        }

        if (view is null)
        {
            TaskDialog.Show("RevitCheck - Dimension Triage", "That view no longer exists in this document.");
            return;
        }

        try
        {
            uiDoc!.ActiveView = view;
        }
        catch (Exception ex)
        {
            // A view that can't be made active (e.g. a schedule/legend
            // with no graphical activation) is a real, expected outcome
            // here, not a crash-worthy one - report it and let the
            // reviewer pick a different row.
            TaskDialog.Show("RevitCheck - Dimension Triage", $"Could not open that view:\n\n{ExceptionMessage.Full(ex)}");
        }
    }

    public string GetName() => "RevitCheck - Open Checklist View";
}
