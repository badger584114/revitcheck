using System.Text.Json.Serialization;

namespace RevitCheck.Core.Ir;

/// <summary>
/// One segment of a dimension. A Revit dimension chain is a single
/// <c>Dimension</c> element with many segments, not many dimensions - they
/// arrive pre-assembled here, unlike the DXF pipeline which had to
/// reassemble chains from shared witness points.
/// </summary>
public sealed class DimensionSegmentInfo
{
    /// <summary>What the model measures.</summary>
    public double? ValueMm { get; init; }

    /// <summary>The text a drafter typed to replace ValueMm, or null.</summary>
    public string? ValueOverride { get; init; }

    public string? Prefix { get; init; }
    public string? Suffix { get; init; }

    [JsonIgnore]
    public bool IsOverridden => !string.IsNullOrEmpty(ValueOverride);
}
