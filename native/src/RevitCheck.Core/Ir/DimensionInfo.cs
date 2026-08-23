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
