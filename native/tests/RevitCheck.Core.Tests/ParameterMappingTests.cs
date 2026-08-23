using RevitCheck.Core.Mapping;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

public class ParameterMappingTests
{
    private static ParameterMapping Sample() => new()
    {
        KeyParameterName = "Asset_ID",
        KeyCsvColumn = "Asset ID",
        Fields = new Dictionary<string, FieldMapping>
        {
            ["girder_depth_mm"] = new()
            {
                Comparison = ComparisonType.Numeric,
                ToleranceMm = 5.0,
                CsvColumn = "Girder Depth (mm)",
                DefaultParameter = "Depth",
                Overrides = new List<FieldOverride>
                {
                    new() { Match = new MatchRule { FamilyName = "PC_Super_T_Girder" }, Parameter = "Girder_Depth" },
                    new() { Match = new MatchRule { Category = "Structural Framing", FamilyName = "PC_I_Beam" }, Parameter = "Overall_Depth" },
                },
            },
            ["asset_owner"] = new() { Comparison = ComparisonType.ExactString, DefaultParameter = "Owner" },
        },
    };

    [Fact]
    public void RoundTrip_PreservesFieldsAndOverrides()
    {
        var loaded = ParameterMappingSerializer.Loads(ParameterMappingSerializer.Dumps(Sample()));

        Assert.Equal("Asset_ID", loaded.KeyParameterName);
        Assert.Equal("Asset ID", loaded.KeyCsvColumn);
        Assert.Equal(2, loaded.Fields["girder_depth_mm"].Overrides.Count);
        Assert.Equal(5.0, loaded.Fields["girder_depth_mm"].ToleranceMm);
    }

    [Fact]
    public void OverridePrecedence_FamilyAndCategoryBeatsFamilyOnlyBeatsDefault()
    {
        var field = Sample().Fields["girder_depth_mm"];

        // Matches the family+category override exactly.
        var iBeam = RevitCheckTestBuilders.Element(1, category: "Structural Framing", familyName: "PC_I_Beam");
        Assert.Equal("Overall_Depth", field.ResolveParameterName(iBeam));

        // Matches only the family-only override (different category).
        var tGirder = RevitCheckTestBuilders.Element(2, category: "Structural Framing", familyName: "PC_Super_T_Girder");
        Assert.Equal("Girder_Depth", field.ResolveParameterName(tGirder));

        // Matches no override - falls through to the default.
        var other = RevitCheckTestBuilders.Element(3, category: "Structural Framing", familyName: "Some_Other_Family");
        Assert.Equal("Depth", field.ResolveParameterName(other));
    }

    [Fact]
    public void OverrideOrder_FirstMatchWins()
    {
        var field = new FieldMapping
        {
            Comparison = ComparisonType.ExactString,
            DefaultParameter = "Fallback",
            Overrides = new List<FieldOverride>
            {
                new() { Match = new MatchRule { Category = "Structural Framing" }, Parameter = "First" },
                new() { Match = new MatchRule { FamilyName = "PC_I_Beam" }, Parameter = "Second" },
            },
        };
        var element = RevitCheckTestBuilders.Element(1, category: "Structural Framing", familyName: "PC_I_Beam");

        Assert.Equal("First", field.ResolveParameterName(element));
    }

    [Fact]
    public void NumericFieldMissingTolerance_FailsAtLoadTime()
    {
        var mapping = new ParameterMapping
        {
            KeyParameterName = "Asset_ID",
            Fields = new Dictionary<string, FieldMapping>
            {
                ["bad"] = new() { Comparison = ComparisonType.Numeric },
            },
        };

        Assert.Throws<InvalidOperationException>(() => ParameterMappingSerializer.Loads(ParameterMappingSerializer.Dumps(mapping)));
    }

    [Fact]
    public void ANewerMapping_IsRefusedRatherThanMisread()
    {
        var json = ParameterMappingSerializer.Dumps(Sample())
            .Replace($"\"schema_version\": {ParameterMappingSerializer.SchemaVersion}", $"\"schema_version\": {ParameterMappingSerializer.SchemaVersion + 1}");

        Assert.Throws<InvalidOperationException>(() => ParameterMappingSerializer.Loads(json));
    }

    [Fact]
    public void KeyCsvColumn_DefaultsToKeyParameterName_WhenUnset()
    {
        var mapping = new ParameterMapping { KeyParameterName = "Asset_ID" };

        Assert.Equal("Asset_ID", mapping.ResolvedKeyCsvColumn);
    }
}
