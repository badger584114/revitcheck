namespace RevitCheck.Core.Ir;

/// <summary>
/// One roughly-horizontal real face found near a Spot Elevation's own
/// point - the raw geometric fact
/// <see cref="RevitCheck.Core.Checks.AbutmentElevationConsistencyCheck"/> judges against.
/// Populated by the adapter's own solid-geometry walk
/// (<c>RevitDimensionSource.NearbyHorizontalFaces</c>): a small,
/// category-agnostic bounding-box search around the spot's point, then
/// <c>Face.Project</c> onto every roughly-horizontal <c>PlanarFace</c>
/// found - never filtered or judged there, matching this codebase's own
/// adapter/check split (CLAUDE.md's layering rule). The check decides
/// which candidate, if any, is close enough to explain the drafted value.
/// </summary>
/// <remarks>
/// Built from real, validated diagnostic work (PLANNING.md §18,
/// 2026-09-02): a Spot Elevation's own <c>Reference</c> and any named
/// parameter on nearby model elements both proved unreliable for finding
/// a bearing shelf - reference resolution is mixed (a real model element
/// once, a view-specific annotation twice on the same real sample), and
/// even a plausible-looking parameter (a profile's own "Start/End Level
/// Offset") turned out to describe a different real feature (the crest)
/// than the shelf a girder actually sits on. Real solid geometry, read
/// directly, is the one thing that can't misrepresent where a horizontal
/// surface actually is - confirmed against 3 real Spot Elevations, all
/// matching within a few millimetres once the search was made
/// category-agnostic (a category name, even within one client's own
/// project history, is not a stable thing to search by - confirmed by
/// the user directly).
/// </remarks>
public sealed class NearbyFaceInfo
{
    /// <summary>The face's own real elevation (mm) at the point nearest the spot - via <c>Face.Project</c> where that succeeded, a coarser fallback otherwise (see the adapter's own remarks).</summary>
    public required double ZMm { get; init; }

    /// <summary>Plan (X/Y-only) distance from the spot's own point to the nearest real point on this face, mm - null if no representative point could be computed at all.</summary>
    public double? Distance2DMm { get; init; }

    /// <summary>The element this face's geometry actually came from - never a category, since none is stable enough to search by (see this class's own remarks).</summary>
    public long? SourceElementId { get; init; }

    /// <summary>True when <see cref="ZMm"/> was read via <c>Face.Project(nearXyz)</c> at the real point nearest the spot, rather than a coarser fallback (a face's own untrimmed-plane origin, wrong for anything but a perfectly flat face - PLANNING.md §18's third real bug in this exact area).</summary>
    public bool ZReadAtProjectedPoint { get; init; }
}
