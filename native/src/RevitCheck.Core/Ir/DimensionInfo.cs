using System.Text.Json.Serialization;

namespace RevitCheck.Core.Ir;

public sealed class DimensionInfo
{
    public required long ElementId { get; init; }
    public required long ViewId { get; init; }

    /// <summary>Spot dimensions (elevation/coordinate/slope) subclass Dimension in the API and are collected alongside ordinary ones. They matter more here, not less - a spot coordinate placed on detail linework looks authoritative and tracks nothing.</summary>
    public bool IsSpot { get; init; }

    public List<ReferenceInfo> References { get; init; } = new();
    public List<DimensionSegmentInfo> Segments { get; init; } = new();
    public Point3D? Origin { get; init; }
    public string? TypeName { get; init; }
    public string? WorksetName { get; init; }
    public string? UniqueId { get; init; }

    /// <summary>
    /// Real horizontal faces found near this dimension's own point by a
    /// category-agnostic solid-geometry search - populated only when a
    /// caller opts in (<c>RevitDimensionSource.Collect</c>'s
    /// <c>populateNearbyShelfFaces</c>, off by default: a real solid-geometry
    /// walk per spot is comparatively expensive, worth paying only for
    /// <see cref="RevitCheck.Core.Checks.AbutmentElevationConsistencyCheck"/>'s own run).
    /// Empty either because nothing was found, or because the search never
    /// ran at all - <see cref="ShelfSearchPerformed"/> is the only way to
    /// tell those apart; an empty list here must never be read as "checked,
    /// clean" on its own. See <see cref="NearbyFaceInfo"/>'s own remarks
    /// for why this doesn't rely on <see cref="References"/> or any
    /// parameter.
    /// </summary>
    public List<NearbyFaceInfo> NearbyHorizontalFaces { get; init; } = new();

    /// <summary>True once the geometry search behind <see cref="NearbyHorizontalFaces"/> has actually run for this dimension - see that field's own remarks for why an empty list alone is ambiguous.</summary>
    public bool ShelfSearchPerformed { get; init; }

    /// <summary>Total measured length across every segment, or null if the model reports no value at all (Revit does this for some spot types).</summary>
    [JsonIgnore]
    public double? ValueMm
    {
        get
        {
            var values = Segments.Where(s => s.ValueMm is not null).Select(s => s.ValueMm!.Value).ToList();
            return values.Count > 0 ? values.Sum() : null;
        }
    }
}
