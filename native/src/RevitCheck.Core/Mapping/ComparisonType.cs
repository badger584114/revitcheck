namespace RevitCheck.Core.Mapping;

/// <summary>How a canonical field's model value and CSV value should be compared.</summary>
public enum ComparisonType
{
    /// <summary>Compare as numbers within a tolerance (e.g. millimetres) - for dimension-like fields.</summary>
    Numeric,

    /// <summary>Compare as text, optionally case-insensitive - for everything else.</summary>
    ExactString,
}
