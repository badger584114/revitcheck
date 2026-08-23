namespace RevitCheck.Core.Mapping;

/// <summary>One family/category-specific parameter-name override for a canonical field.</summary>
public sealed class FieldOverride
{
    public MatchRule Match { get; init; } = new();
    public required string Parameter { get; init; }
}
