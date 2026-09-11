namespace RevitCheck.Core.Ir;

/// <summary>
/// One real point on model geometry found near a dimension's witness -
/// a candidate for what that end of the dimension actually measures to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added 2026-09-11 (PLANNING.md §28).</b> Produced by projecting a
/// witness anchor onto the faces of model elements the view shows. Several
/// are returned per witness and <b>none is chosen here</b>: picking the one
/// the dimension means is a judgement, and judgement belongs in
/// <c>Checks/</c>, not the adapter. The same split
/// <see cref="NearbyFaceInfo"/> already draws for Spot Elevation.
/// </para>
/// <para>
/// Raw facts only - the point, and which element it came off. No distance
/// is recorded, because the distance that matters is measured <i>in the
/// view plane</i> and that needs the view's own direction, which a check
/// has and a single reference does not.
/// </para>
/// </remarks>
public sealed class WitnessPointInfo
{
    /// <summary>The point on the model face, in local project coordinates (mm).</summary>
    public required Point3D Point { get; init; }

    /// <summary>The element this face belongs to - what a finding names so a reviewer can select it.</summary>
    public long? SourceElementId { get; init; }

    /// <summary>The element's category, for a finding's own audit trail.</summary>
    public string? SourceCategory { get; init; }

    /// <summary>
    /// The face's normal. A face seen edge-on in the view (normal
    /// perpendicular to the view direction) draws as a line, and that line
    /// is what a drafter dimensions to; one whose normal points along the
    /// view direction is the background behind the cut.
    /// </summary>
    public Point3D? FaceNormal { get; init; }
}
