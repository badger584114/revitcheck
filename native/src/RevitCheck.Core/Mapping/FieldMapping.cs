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
