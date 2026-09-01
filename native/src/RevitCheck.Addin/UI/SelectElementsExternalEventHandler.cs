using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Commands;

namespace RevitCheck.Addin.UI;

/// <summary>
/// Selects and zooms to one or more elements from the checklist window's
/// "Select in Revit" button - real user feedback, 2026-09-02: reading an
/// Element Id off the details pane and retyping it into Revit's own
/// Select-by-ID search box by hand was the actual friction, not the id
/// being hard to read. Same <see cref="IExternalEventHandler"/> pattern as
/// <see cref="OpenViewExternalEventHandler"/> and for the same reason: a
/// direct <c>Selection</c>/<c>ShowElements</c> call from inside a WPF
/// Click handler is not a valid Revit API context.
/// </summary>
/// <remarks>
/// <c>UIDocument.ShowElements(ICollection&lt;ElementId&gt;)</c> and
/// <c>Selection.SetElementIds(ICollection&lt;ElementId&gt;)</c> were both
/// verified against the real <c>RevitAPI(UI).dll</c> (via
/// <c>System.Reflection.MetadataLoadContext</c>, no Revit machine needed)
/// before writing this, per this codebase's own standing practice.
/// <c>ShowElements</c> switches to whichever view can show a view-specific
/// element (a dimension included) on its own - no separate "open the right
/// view first" step is needed here the way <see cref="OpenViewExternalEventHandler"/>
/// needs one for the checklist's own per-view row.
/// </remarks>
internal sealed class SelectElementsExternalEventHandler : IExternalEventHandler
{
    /// <summary>Set by <see cref="ChecklistWindow"/> immediately before <see cref="ExternalEvent.Raise"/> - read once, here, then left as-is until the next request.</summary>
    public IReadOnlyList<long> RequestedElementIds { get; set; } = Array.Empty<long>();

    public void Execute(UIApplication app)
    {
        if (RequestedElementIds.Count == 0)
        {
            return;
        }

        var uiDoc = app.ActiveUIDocument;
        var doc = uiDoc?.Document;
        if (doc is null)
        {
            return;
        }

        var resolved = new List<ElementId>();
        var missing = 0;
        foreach (var id in RequestedElementIds)
        {
            var elementId = new ElementId(id);
            if (doc.GetElement(elementId) is not null)
            {
                resolved.Add(elementId);
            }
            else
            {
                missing++;
            }
        }

        if (resolved.Count == 0)
        {
            TaskDialog.Show("RevitCheck - Dimension Triage", "None of the selected element(s) exist in this document any more.");
            return;
        }

        try
        {
            uiDoc!.Selection.SetElementIds(resolved);
            uiDoc.ShowElements(resolved);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Dimension Triage", $"Could not select/show those element(s):\n\n{ExceptionMessage.Full(ex)}");
            return;
        }

        if (missing > 0)
        {
            TaskDialog.Show("RevitCheck - Dimension Triage",
                $"Selected {resolved.Count} element(s); {missing} no longer exist in this document.");
        }
    }

    public string GetName() => "RevitCheck - Select Checklist Element(s)";
}
