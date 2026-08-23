namespace RevitCheck.Core.Ir;

/// <summary>
/// One endpoint of a dimension - what it is attached to. Raw facts only,
/// matching <c>ir.py</c>'s rule 2: <see cref="ViewSpecific"/> is the
/// load-bearing field. Revit sets it on every element that belongs to a
/// single view (detail lines, detail components, filled/masking regions) -
/// exactly the population that cannot track the model. It is a property of
/// the API rather than of a client's drafting standard, which is why it
/// survived contact with a second client (Flinders) where a CAD-layer-name
/// proxy for the same idea didn't.
/// </summary>
public sealed class ReferenceInfo
{
    public required long ElementId { get; init; }

    public bool Resolved { get; init; } = true;

    public string? ClassName { get; init; }

    public string? Category { get; init; }

    /// <summary>Negative int for a Revit built-in category - language-independent and version-stable, unlike Category's localized display name. Classify on this one.</summary>
    public long? BuiltinCategory { get; init; }

    public bool? ViewSpecific { get; init; }

    /// <summary>True when this reference resolves through a linked model - the adapter follows Reference.LinkedElementId to describe the real element rather than the RevitLinkInstance wrapping it.</summary>
    public bool Linked { get; init; }

    public long? LinkInstanceId { get; init; }
}
