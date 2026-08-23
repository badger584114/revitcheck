using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Mapping;

/// <summary>
/// How one canonical field is resolved and compared. <see cref="CsvColumn"/>
/// defaults to the field's own key in <see cref="ParameterMapping.Fields"/> if
/// unset - a canonical field name doesn't have to textually match the
/// spreadsheet header either.
/// </summary>
public sealed class FieldMapping
{
    public required ComparisonType Comparison { get; init; }

    /// <summary>Required when Comparison is Numeric - validated at load time, not at first comparison.</summary>
    public double? ToleranceMm { get; init; }

    public bool CaseInsensitive { get; init; } = true;

    /// <summary>
    /// When true, a model value that is genuinely blank is always a
    /// mismatch, even when the CSV cell is blank too - for a field whose
    /// convention is "always an explicit value, even if that value is just
    /// 'N/A' for a not-applicable case", never a truly unset parameter.
    /// Confirmed by the user for Asset Classification.csv 2026-08-23:
    /// different fields are conditionally required depending on the
    /// element's Managed status, most non-required cells read literally
    /// "N/A" rather than being empty, and a genuinely blank CSV cell (a
    /// data gap in the reference table) does not excuse the model from
    /// still needing its own explicit value. Off by default - this is a
    /// domain rule for this table, not assumed to hold for every mapping.
    /// </summary>
    public bool RequireModelValue { get; init; }

    public string? CsvColumn { get; init; }

    public string? DefaultParameter { get; init; }

    /// <summary>Ordered, first match wins.</summary>
    public List<FieldOverride> Overrides { get; init; } = new();

    /// <summary>
    /// Resolves which Revit parameter name holds this canonical field's
    /// value for a given element - an override match if one applies, else
    /// the default. Null means unresolved (no override matched and no
    /// default is set) - the reconciliation check turns that into a
    /// coverage Issue, never a silent skip.
    /// </summary>
    public string? ResolveParameterName(ElementMetadata element) =>
        Overrides.FirstOrDefault(o => o.Match.Matches(element))?.Parameter ?? DefaultParameter;

    public void Validate(string fieldName)
    {
        if (Comparison == ComparisonType.Numeric && ToleranceMm is null)
        {
            throw new InvalidOperationException(
                $"Field '{fieldName}' uses numeric comparison but has no tolerance_mm set.");
        }
    }
}
