using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Mapping;

/// <summary>
/// Matches an element by category and/or family name (AND'd together when
/// both are present, null means "don't care about this axis"). Deliberately
/// not a general rules DSL - just enough to express "this family exposes a
/// field under a different parameter name", the specific case this mapping
/// file exists to solve.
/// </summary>
public sealed class MatchRule
{
    public string? Category { get; init; }
    public string? FamilyName { get; init; }

    public bool Matches(ElementMetadata element) =>
        (Category is null || string.Equals(Category, element.Category, StringComparison.Ordinal)) &&
        (FamilyName is null || string.Equals(FamilyName, element.FamilyName, StringComparison.Ordinal));
}
