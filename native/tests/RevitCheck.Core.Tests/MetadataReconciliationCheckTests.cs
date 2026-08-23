using RevitCheck.Core.Checks;
using RevitCheck.Core.Csv;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Mapping;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

public class MetadataReconciliationCheckTests
{
    private static ParameterMapping NumericMapping(double toleranceMm = 5.0) => new()
    {
        KeyParameterName = "Asset_ID",
        KeyCsvColumn = "Asset ID",
        Fields = new Dictionary<string, FieldMapping>
        {
            ["girder_depth_mm"] = new()
            {
                Comparison = ComparisonType.Numeric,
                ToleranceMm = toleranceMm,
                CsvColumn = "Girder Depth (mm)",
                DefaultParameter = "Depth",
            },
        },
    };

    private static ParameterMapping StringMapping(bool caseInsensitive = true) => new()
    {
        KeyParameterName = "Asset_ID",
        KeyCsvColumn = "Asset ID",
        Fields = new Dictionary<string, FieldMapping>
        {
            ["owner"] = new() { Comparison = ComparisonType.ExactString, CaseInsensitive = caseInsensitive, CsvColumn = "Owner", DefaultParameter = "Owner" },
        },
    };

    private static CsvTable Csv(params (string AssetId, string Depth)[] rows) => new()
    {
        Headers = new[] { "Asset ID", "Girder Depth (mm)" },
        Rows = rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["Asset ID"] = r.AssetId,
            ["Girder Depth (mm)"] = r.Depth,
        }).ToList(),
    };

    private static CsvTable OwnerCsv(params (string AssetId, string Owner)[] rows) => new()
    {
        Headers = new[] { "Asset ID", "Owner" },
        Rows = rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["Asset ID"] = r.AssetId,
            ["Owner"] = r.Owner,
        }).ToList(),
    };

    private static ElementMetadata ElementWithDepth(long id, string assetId, double? depthMm, string? uniqueId = null) =>
        RevitCheckTestBuilders.Element(id, keyValue: assetId, uniqueId: uniqueId, parameters: new Dictionary<string, ParameterValue>
        {
            ["Asset_ID"] = new() { StorageType = ParameterStorageType.String, RawString = assetId, DisplayString = assetId },
            ["Depth"] = depthMm is { } d
                ? new() { StorageType = ParameterStorageType.Double, NumericValue = d, IsLength = true, DisplayString = $"{d} mm" }
                : new(),
        });

    private static ElementMetadata ElementWithOwner(long id, string assetId, string? owner, string? familyName = "PC_I_Beam") =>
        RevitCheckTestBuilders.Element(id, familyName: familyName, keyValue: assetId, parameters: new Dictionary<string, ParameterValue>
        {
            ["Asset_ID"] = new() { StorageType = ParameterStorageType.String, RawString = assetId, DisplayString = assetId },
            ["Owner"] = new() { StorageType = ParameterStorageType.String, RawString = owner, DisplayString = owner },
        });

    [Fact]
    public void NumericWithinTolerance_NoIssue()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithDepth(1, "A1", 448.0) });
        var issues = MetadataReconciliationCheck.Run(model, NumericMapping(toleranceMm: 5.0), Csv(("A1", "450")), new ReconciliationConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void NumericOutsideTolerance_OneMismatchIssue()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithDepth(1, "A1", 440.0) });
        var issues = MetadataReconciliationCheck.Run(model, NumericMapping(toleranceMm: 5.0), Csv(("A1", "450")), new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("metadata", issue.Category);
        Assert.Contains("model says '440'", issue.Description);
        Assert.Contains("spreadsheet says '450'", issue.Description);
    }

    [Fact]
    public void ExactString_CaseInsensitiveMatch_NoIssue()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", "roads authority") });
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), OwnerCsv(("A1", "Roads Authority")), new ReconciliationConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void ExactString_CaseSensitiveMismatch_WhenCaseInsensitiveIsOff()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", "roads authority") });
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(caseInsensitive: false), OwnerCsv(("A1", "Roads Authority")), new ReconciliationConfig());

        Assert.Single(issues);
    }

    [Fact]
    public void BlankKeyValue_ProducesOneCoverageIssue_DistinctFromAMismatch()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "", null) });
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), OwnerCsv(("A1", "Roads")), new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("no value for the key parameter", issue.Description);
    }

    [Fact]
    public void KeySetNoCsvRow_DefaultConfig_ExactlyOneMediumSeverityIssue()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "MISSING", "Roads") });
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), OwnerCsv(("A1", "Roads")), new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("metadata", issue.Category);
        Assert.Equal("medium", issue.Severity);
        Assert.Contains("no matching row", issue.Description);
    }

    [Fact]
    public void KeySetNoCsvRow_ReportingDisabled_NoIssue()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "MISSING", "Roads") });
        var config = new ReconciliationConfig { ReportUnmatchedModelElements = false };
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), OwnerCsv(("A1", "Roads")), config);

        Assert.Empty(issues);
    }

    [Fact]
    public void BlankModelValue_CsvHasData_IsAMismatchIssue_NotCoverage()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", null) });
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), OwnerCsv(("A1", "Roads Authority")), new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("metadata", issue.Category);
        Assert.Contains("model value is blank", issue.Description);
        Assert.Contains("Roads Authority", issue.Description);
    }

    [Fact]
    public void ModelHasValue_CsvCellBlank_NoIssue()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", "Roads Authority") });
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), OwnerCsv(("A1", "")), new ReconciliationConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void BothBlank_DefaultField_NoIssue()
    {
        // The default: a CSV data gap paired with an unset model value is
        // not this tool's job to guess about.
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", null) });
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), OwnerCsv(("A1", "")), new ReconciliationConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void BothBlank_RequireModelValueField_IsAMismatch()
    {
        // Confirmed by the user (Asset Classification.csv, 2026-08-23): a
        // CSV data gap does not excuse the model from still needing an
        // explicit value ("N/A" when not applicable) - never a truly unset
        // parameter, regardless of what the reference table says.
        var mapping = new ParameterMapping
        {
            KeyParameterName = "Asset_ID",
            KeyCsvColumn = "Asset ID",
            Fields = new Dictionary<string, FieldMapping>
            {
                ["owner"] = new() { Comparison = ComparisonType.ExactString, CsvColumn = "Owner", DefaultParameter = "Owner", RequireModelValue = true },
            },
        };
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", null) });
        var issues = MetadataReconciliationCheck.Run(model, mapping, OwnerCsv(("A1", "")), new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("metadata", issue.Category);
        Assert.Contains("model value is blank", issue.Description);
        Assert.Contains("always have an explicit value", issue.Description);
    }

    [Fact]
    public void UnmatchedCsvRowsAmongManyUnrelatedOnes_ProduceZeroNoise()
    {
        // The regression test named directly in the plan: a whole-of-project
        // CSV (here, 50 unrelated rows standing in for 1000+) against a
        // handful of model elements must never report on the CSV's excess.
        var unrelatedRows = Enumerable.Range(1, 50).Select(i => ($"OTHER-{i}", "Someone Else")).ToArray();
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", "Roads Authority") });
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), OwnerCsv(unrelatedRows.Append(("A1", "Roads Authority")).ToArray()), new ReconciliationConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void DuplicateCsvKeyValues_ProduceOneCoverageIssueNamingTheCount()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", "Roads Authority") });
        var csv = OwnerCsv(("A1", "Roads Authority"), ("A1", "Someone Else"), ("A2", "X"), ("A2", "Y"));

        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), csv, new ReconciliationConfig());

        var duplicateIssue = issues.Single(i => i.Category == "coverage" && i.Description.Contains("more than one row"));
        Assert.Contains("2 key value(s)", duplicateIssue.Description);
    }

    [Fact]
    public void DuplicateCsvKey_UsesFirstRowDeterministically_ForComparison()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", "Roads Authority") });
        var csv = OwnerCsv(("A1", "Roads Authority"), ("A1", "Someone Else"));

        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), csv, new ReconciliationConfig());

        // First row matches the model value, so no mismatch issue - only the duplicate-key coverage issue.
        Assert.DoesNotContain(issues, i => i.Category == "metadata");
    }

    [Fact]
    public void DifferentFamilies_ResolveTheSameCanonicalFieldToDifferentParameters()
    {
        var mapping = new ParameterMapping
        {
            KeyParameterName = "Asset_ID",
            KeyCsvColumn = "Asset ID",
            Fields = new Dictionary<string, FieldMapping>
            {
                ["girder_depth_mm"] = new()
                {
                    Comparison = ComparisonType.Numeric,
                    ToleranceMm = 1.0,
                    CsvColumn = "Girder Depth (mm)",
                    DefaultParameter = "Depth",
                    Overrides = new List<FieldOverride>
                    {
                        new() { Match = new MatchRule { FamilyName = "PC_Super_T_Girder" }, Parameter = "Girder_Depth" },
                    },
                },
            },
        };

        var iBeam = RevitCheckTestBuilders.Element(1, familyName: "PC_I_Beam", keyValue: "A1", parameters: new Dictionary<string, ParameterValue>
        {
            ["Asset_ID"] = new() { StorageType = ParameterStorageType.String, RawString = "A1" },
            ["Depth"] = new() { StorageType = ParameterStorageType.Double, NumericValue = 450.0, IsLength = true },
        });
        var tGirder = RevitCheckTestBuilders.Element(2, familyName: "PC_Super_T_Girder", keyValue: "A2", parameters: new Dictionary<string, ParameterValue>
        {
            ["Asset_ID"] = new() { StorageType = ParameterStorageType.String, RawString = "A2" },
            ["Girder_Depth"] = new() { StorageType = ParameterStorageType.Double, NumericValue = 600.0, IsLength = true },
        });

        var model = RevitCheckTestBuilders.Model(new[] { iBeam, tGirder });
        var csv = Csv(("A1", "450"), ("A2", "600"));

        var issues = MetadataReconciliationCheck.Run(model, mapping, csv, new ReconciliationConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void NestedComponent_ReconciledIndependentlyOfItsHost()
    {
        var panel = ElementWithOwner(1, "PANEL-01", "Roads Authority", familyName: "Concrete_Panel");
        var bracket = RevitCheckTestBuilders.Element(2, category: "Structural Connections", familyName: "Fixing_Bracket",
            keyValue: "BRK-01", hostElementId: panel.ElementId, parameters: new Dictionary<string, ParameterValue>
            {
                ["Asset_ID"] = new() { StorageType = ParameterStorageType.String, RawString = "BRK-01" },
                ["Owner"] = new() { StorageType = ParameterStorageType.String, RawString = "Wrong Owner" },
            });

        var model = RevitCheckTestBuilders.Model(new[] { panel, bracket });
        var csv = OwnerCsv(("PANEL-01", "Roads Authority"), ("BRK-01", "Bracket Supplier Co"));

        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), csv, new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal(bracket.ElementId, issue.ElementId);
        Assert.Contains("Bracket Supplier Co", issue.Description);
    }

    [Fact]
    public void UnmappedFieldForAnUnlistedFamily_ProducesCoverageIssue_RunContinues()
    {
        var mapping = new ParameterMapping
        {
            KeyParameterName = "Asset_ID",
            KeyCsvColumn = "Asset ID",
            Fields = new Dictionary<string, FieldMapping>
            {
                ["owner"] = new() { Comparison = ComparisonType.ExactString, CsvColumn = "Owner" }, // no DefaultParameter, no overrides
            },
        };
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", "Roads Authority") });

        var issues = MetadataReconciliationCheck.Run(model, mapping, OwnerCsv(("A1", "Roads Authority")), new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("no resolvable Revit parameter", issue.Description);
    }

    [Fact]
    public void UnparseableNonBlankNumericValue_ProducesCoverageIssue_NotACrashOrFalseMismatch()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithDepth(1, "A1", 450.0) });
        var issues = MetadataReconciliationCheck.Run(model, NumericMapping(), Csv(("A1", "not-a-number")), new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("could not be read as a number", issue.Description);
    }

    [Fact]
    public void MappedCsvColumnMissingFromCsv_ProducesOneCoverageIssue_NotOnePerElement()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithDepth(1, "A1", 450.0), ElementWithDepth(2, "A2", 500.0) });
        var badCsv = new CsvTable
        {
            Headers = new[] { "Asset ID" },
            Rows = new List<IReadOnlyDictionary<string, string>>
            {
                new Dictionary<string, string> { ["Asset ID"] = "A1" },
                new Dictionary<string, string> { ["Asset ID"] = "A2" },
            },
        };

        var issues = MetadataReconciliationCheck.Run(model, NumericMapping(), badCsv, new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("Girder Depth (mm)", issue.Description);
    }
}
