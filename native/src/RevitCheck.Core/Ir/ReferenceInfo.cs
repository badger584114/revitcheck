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

    /// <summary>
    /// The resolved element's own Location.Point, in local project
    /// coordinates (mm) - added 2026-08-26 for pile-chain reconstruction
    /// (PileChainReconstruction). Deliberately local, not survey-adjusted:
    /// this only ever feeds a nearest-neighbour proximity search
    /// (PileChainReconstruction.ResolvePileMatch), which is invariant to
    /// any consistent coordinate frame - local coordinates are what a real
    /// diagnostic run (InspectDimensionGeometry.pushbutton) already
    /// validated this works with, and avoid a GetProjectPosition call per
    /// reference (this can be thousands on a real model). Null for any
    /// reference whose resolved element has no simple Location.Point (most
    /// model geometry references, e.g. CUT_EDGE/Face) - not a gap this
    /// field needs to fill, since chain reconstruction only ever needs a
    /// tag's own point, not arbitrary model geometry's.
    /// </summary>
    public Point3D? LocalPoint { get; init; }
}
