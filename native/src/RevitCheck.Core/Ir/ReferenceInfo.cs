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

    /// <summary>
    /// The resolved element's own curve endpoints, in local project
    /// coordinates (mm), when it has one - a detail line, a model line, a
    /// wall. Null for anything with no simple <c>LocationCurve</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-09-10, and it reopens a question PLANNING.md §14 had
    /// closed.</b> That diagnostic concluded a linear dimension's witness
    /// points were unrecoverable, having tried <c>Reference.GlobalPoint</c>
    /// (null on all 17 real references) and <c>Location</c> (returning
    /// <c>(0,0,0)</c> for real model geometry). But the thing a drafted
    /// dimension actually references is usually a <c>DetailLine</c>, and a
    /// detail line's own <c>GeometryCurve</c> is directly readable - the
    /// same "resolve the element, read its own geometry" move that already
    /// works for a tag's <see cref="LocalPoint"/> and for a pile.
    /// </para>
    /// <para>
    /// So a dimension between two detail lines does have recoverable
    /// witness geometry after all: not the dimension's witness points, but
    /// the lines it measures between, which is what a person reading the
    /// drawing sees anyway. That is the anchor the §14 approach lacked -
    /// its projection attempts were anchored on the dimension's own text
    /// position, which real drafting practice drags arbitrarily far (527m,
    /// on a real case).
    /// </para>
    /// <para>
    /// Endpoints rather than the whole curve: two points is what a distance
    /// comparison needs, it round-trips through the capture as plain data,
    /// and an arc's endpoints are still the right anchor for finding what
    /// model geometry sits near it.
    /// </para>
    /// </remarks>
    public Point3D? CurveStart { get; init; }

    /// <summary>The other end of <see cref="CurveStart"/>'s curve - see its remarks.</summary>
    public Point3D? CurveEnd { get; init; }
}
