namespace RevitCheck.Core.Mapping;

/// <summary>How a canonical field's model value and CSV value should be compared.</summary>
public enum ComparisonType
{
    /// <summary>Compare as numbers within a tolerance (e.g. millimetres) - for dimension-like fields.</summary>
    Numeric,

    /// <summary>Compare as text, optionally case-insensitive - for everything else.</summary>
    ExactString,

    /// <summary>
    /// The model value is a delimited list (semicolon-separated - an
    /// element can genuinely belong to more than one group), the CSV value
    /// is a single entry, and the field is fine as long as the CSV's value
    /// appears somewhere in the model's list - not a mismatch just because
    /// the model also lists others the CSV doesn't track. Confirmed by the
    /// user 2026-08-24 for Location Referencing's LocationHeirarchyKey: the
    /// model's value is copied from Asset Classification, which can
    /// legitimately record more than one role for one element (e.g. a pile
    /// shared between a pier and an abutment - "3BPI; 3BAB" - while
    /// Location Referencing only ever settles on the one it actually is,
    /// "3BAB").
    /// </summary>
    /// <remarks>
    /// Only ~28% of the mismatches ExactString flagged on the first real run
    /// were actually this shape (114 of 409) - the rest turned out to be
    /// genuine data drift this check correctly caught (a stale
    /// classification copied from another discipline, confirmed by the user
    /// 2026-08-24) on a model that had already passed a manual audit. Worth
    /// remembering before assuming a big real-run mismatch count is always a
    /// comparison-logic bug - sometimes the count is real.
    /// </remarks>
    ContainsCsvValue,
}
