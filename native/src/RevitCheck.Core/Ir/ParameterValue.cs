namespace RevitCheck.Core.Ir;

/// <summary>
/// The raw storage-type variants a Revit <c>Parameter</c> can hold. Recorded
/// as-is by the adapter, not judged - a caller decides how to interpret it,
/// the same "extract facts, judge nothing" split <c>ir.py</c>'s
/// <c>ReferenceInfo</c> uses for its own Revit-API-derived fields.
/// </summary>
public enum ParameterStorageType
{
    None,
    String,
    Double,
    Integer,
    ElementId,
}

/// <summary>
/// One parameter's value on one captured element. Revit parameters are
/// string/double/int/ElementId-valued, and a double may or may not be a
/// length (angles, ratios, counts are doubles too) - so both a
/// human-formatted display string (<c>Parameter.AsValueString()</c>, unit-
/// and locale-aware) and the raw comparable value are captured, and it is
/// left to the reconciliation check to decide which one a given canonical
/// field's comparison type needs. <see cref="IsLength"/> is recorded by the
/// adapter (which knows the parameter's spec/unit type) rather than guessed
/// downstream from the storage type alone.
/// </summary>
/// <remarks>
/// When <see cref="IsLength"/> is true, <see cref="NumericValue"/> has
/// already been converted to millimetres by the adapter, mirroring
/// <c>revit_source.py</c>'s <c>_mm()</c> helper - Revit's internal unit is
/// always decimal feet regardless of project display settings.
/// </remarks>
public sealed class ParameterValue
{
    public ParameterStorageType StorageType { get; init; } = ParameterStorageType.None;

    /// <summary>Parameter.AsValueString() - formatted for display, unit-aware. Not safe for numeric comparison.</summary>
    public string? DisplayString { get; init; }

    /// <summary>Parameter.AsString() - only meaningful when StorageType is String.</summary>
    public string? RawString { get; init; }

    /// <summary>Parameter.AsDouble() - already converted to mm when IsLength is true.</summary>
    public double? NumericValue { get; init; }

    /// <summary>True iff the parameter's spec/unit type is Length (decided by the adapter, not inferred here).</summary>
    public bool IsLength { get; init; }

    public long? ElementIdValue { get; init; }

    public int? IntegerValue { get; init; }
}
