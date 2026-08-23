using System.Text.Json.Serialization;

namespace RevitCheck.Core.Ir;

public sealed class ViewInfo
{
    public required long ElementId { get; init; }
    public required string Name { get; init; }

    /// <summary>Revit's ViewType as a string, e.g. "FloorPlan", "Section", "Elevation", "Detail", "DraftingView", "ThreeD".</summary>
    public required string ViewType { get; init; }

    public bool IsTemplate { get; init; }
    public int? Scale { get; init; }

    /// <summary>The sheet this view is placed on, via its Viewport, or null if unplaced.</summary>
    public long? SheetId { get; init; }
    public string? SheetNo { get; init; }

    /// <summary>The sheet's own unique_id, denormalized here the same way SheetNo already is - see SheetInfo.UniqueId for why a finding anchors to the sheet, not the view/dimension.</summary>
    public string? SheetUniqueId { get; init; }

    public string? WorksetName { get; init; }

    /// <summary>True when this is a Drafting View displayed via a "Reference other view" callout on a Section/Plan - i.e. standing in for a section cut rather than a free-standing standard detail. Defaults false, the conservative direction.</summary>
    public bool LinkedToModelSection { get; init; }

    public string? UniqueId { get; init; }

    /// <summary>A drafting view has no model behind it at all - every line in one is 2D by construction. Not necessarily an error (a standard detail is legitimately drafted), so rules treat it as a different case.</summary>
    [JsonIgnore]
    public bool IsDraftingView => ViewType is "DraftingView" or "Drafting" or "Legend";
}
