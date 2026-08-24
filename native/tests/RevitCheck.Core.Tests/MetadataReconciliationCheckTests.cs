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

    private static ParameterMapping LocationMapping() => new()
    {
        KeyParameterName = "Asset_ID",
        KeyCsvColumn = "Asset ID",
        Fields = new Dictionary<string, FieldMapping>
        {
            ["location"] = new() { Comparison = ComparisonType.ContainsCsvValue, CsvColumn = "Location", DefaultParameter = "Location" },
        },
    };

    private static CsvTable LocationCsv(params (string AssetId, string Location)[] rows) => new()
    {
        Headers = new[] { "Asset ID", "Location" },
        Rows = rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["Asset ID"] = r.AssetId,
            ["Location"] = r.Location,
        }).ToList(),
    };

    private static ElementMetadata ElementWithLocation(long id, string assetId, string? location) =>
        RevitCheckTestBuilders.Element(id, keyValue: assetId, parameters: new Dictionary<string, ParameterValue>
        {
            ["Asset_ID"] = new() { StorageType = ParameterStorageType.String, RawString = assetId, DisplayString = assetId },
            ["Location"] = new() { StorageType = ParameterStorageType.String, RawString = location, DisplayString = location },
        });

    [Fact]
    public void ContainsCsvValue_CsvValueIsOneEntryInModelList_NoIssue()
    {
        // Real shape found 2026-08-24: the model holds every hierarchy
        // group an element belongs to, the CSV only tracks one.
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithLocation(1, "A1", "3BAP; 3BDE; 3BAB") });
        var issues = MetadataReconciliationCheck.Run(model, LocationMapping(), LocationCsv(("A1", "3BDE")), new ReconciliationConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void ContainsCsvValue_CsvValueNotInModelList_OneMismatchIssue()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithLocation(1, "A1", "3FC") });
        var issues = MetadataReconciliationCheck.Run(model, LocationMapping(), LocationCsv(("A1", "3BDE")), new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("metadata", issue.Category);
        Assert.Contains("does not contain", issue.Description);
    }

    [Fact]
    public void ContainsCsvValue_IgnoresInconsistentSemicolonSpacing()
    {
        // Real example: "3BDE; 3CD ;3BAP" - a space before one semicolon,
        // none after another.
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithLocation(1, "A1", "3BDE; 3CD ;3BAP") });
        var issues = MetadataReconciliationCheck.Run(model, LocationMapping(), LocationCsv(("A1", "3CD")), new ReconciliationConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void KeyValueIsBlankKeySentinel_TreatedAsNoKey_NotReportedAsUnmatched()
    {
        // Real bug found 2026-08-24: a key parameter literally holding "N/A"
        // (the same not-applicable convention RequireModelValue already
        // knows) was being looked up as a real key, producing a false
        // "no matching row" issue for every such element. It should be
        // folded into the same low-severity "no key value at all" coverage
        // note a genuinely blank key already gets - not a distinct,
        // per-element "missing item" mismatch.
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "N/A", "Someone") });
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), OwnerCsv(("A1", "Someone")), new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.DoesNotContain("no matching row", issue.Description);
    }

    [Fact]
    public void KeyValueIsBlankKeySentinel_CaseAndWhitespaceInsensitive()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "  n/a  ", "Someone") });
        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), OwnerCsv(("A1", "Someone")), new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
    }

    // --- DisambiguationField: real shape found 2026-08-24 - Asset
    // Classification can carry multiple rows for the same asset identifier,
    // one per discipline package.

    private static ParameterMapping DisciplineMapping() => new()
    {
        KeyParameterName = "Asset_ID",
        KeyCsvColumn = "Asset ID",
        DisambiguationField = "discipline",
        Fields = new Dictionary<string, FieldMapping>
        {
            ["discipline"] = new() { Comparison = ComparisonType.ExactString, CsvColumn = "Discipline", DefaultParameter = "Discipline" },
            ["owner"] = new() { Comparison = ComparisonType.ExactString, CsvColumn = "Owner", DefaultParameter = "Owner" },
        },
    };

    private static CsvTable DisciplineCsv(params (string AssetId, string Discipline, string Owner)[] rows) => new()
    {
        Headers = new[] { "Asset ID", "Discipline", "Owner" },
        Rows = rows.Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["Asset ID"] = r.AssetId,
            ["Discipline"] = r.Discipline,
            ["Owner"] = r.Owner,
        }).ToList(),
    };

    private static ElementMetadata ElementWithDiscipline(long id, string assetId, string discipline, string owner = "Bridge Team") =>
        RevitCheckTestBuilders.Element(id, keyValue: assetId, parameters: new Dictionary<string, ParameterValue>
        {
            ["Asset_ID"] = new() { StorageType = ParameterStorageType.String, RawString = assetId, DisplayString = assetId },
            ["Discipline"] = new() { StorageType = ParameterStorageType.String, RawString = discipline, DisplayString = discipline },
            ["Owner"] = new() { StorageType = ParameterStorageType.String, RawString = owner, DisplayString = owner },
        });

    [Fact]
    public void Disambiguation_PicksTheRowMatchingTheModelsOwnDisciplineValue()
    {
        // Two rows share the key, differing on Owner - a naive first-match
        // would compare against whichever happened to come first, right or
        // wrong.
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithDiscipline(1, "Sign Face Plate", "BR") });
        var csv = DisciplineCsv(
            ("Sign Face Plate", "GD", "Roads Team"),
            ("Sign Face Plate", "BR", "Bridge Team"));

        var issues = MetadataReconciliationCheck.Run(model, DisciplineMapping(), csv, new ReconciliationConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void Disambiguation_OnlyOneCandidateRow_NeverSecondGuessed_ComparedNormallyEvenIfDisciplineDiffers()
    {
        // Real shape, confirmed by the user 2026-08-24: a key with exactly
        // one CSV row is never "ambiguous" - there's nothing to pick
        // between. If the model's own discipline value differs from that
        // one row's, that's a real finding (a genuine wrong-identifier
        // error, in the real case this mirrors) to report normally via the
        // usual field comparison, not a "no row for this discipline"
        // special case - disambiguation only ever applies with 2+
        // candidates.
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithDiscipline(1, "Sign Face Plate", "BR") });
        var csv = DisciplineCsv(("Sign Face Plate", "GD", "Bridge Team"));

        var issues = MetadataReconciliationCheck.Run(model, DisciplineMapping(), csv, new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("metadata", issue.Category);
        Assert.Contains("discipline", issue.Description);
        Assert.Contains("'BR'", issue.Description);
        Assert.Contains("'GD'", issue.Description);
    }

    [Fact]
    public void Disambiguation_MultipleRowsButNoneMatchModelsDiscipline_ReportsUnmatched_NotFalseFieldMismatches()
    {
        // With 2+ genuinely ambiguous candidates, picking one to compare
        // against (right or wrong) would be guessing - this is the case
        // DisambiguationField actually exists for, distinct from the
        // single-row case above.
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithDiscipline(1, "Sign Face Plate", "BR") });
        var csv = DisciplineCsv(
            ("Sign Face Plate", "GD", "Roads Team"),
            ("Sign Face Plate", "MD", "Mechanical Team"));

        var issues = MetadataReconciliationCheck.Run(model, DisciplineMapping(), csv, new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("metadata", issue.Category);
        Assert.Contains("no row for this key", issue.Description);
        Assert.Contains("GD", issue.Description);
        Assert.Contains("MD", issue.Description);
    }

    [Fact]
    public void Disambiguation_StillAmbiguousAfterFiltering_ReportsCoverageNote_UsesFirstMatch()
    {
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithDiscipline(1, "Sign Face Plate", "BR", owner: "Bridge Team A") });
        var csv = DisciplineCsv(
            ("Sign Face Plate", "BR", "Bridge Team A"),
            ("Sign Face Plate", "BR", "Bridge Team B"));

        var issues = MetadataReconciliationCheck.Run(model, DisciplineMapping(), csv, new ReconciliationConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("matching 2 rows", issue.Description);
    }

    [Fact]
    public void Disambiguation_NotConfigured_KeepsOriginalFirstMatchBehaviour()
    {
        // Regression guard: mappings that never set DisambiguationField
        // (e.g. Location Referencing, whose key is confirmed unique) must
        // behave exactly as before this feature existed.
        var model = RevitCheckTestBuilders.Model(new[] { ElementWithOwner(1, "A1", "Someone") });
        var csv = OwnerCsv(("A1", "Someone"), ("A1", "Someone Else"));

        var issues = MetadataReconciliationCheck.Run(model, StringMapping(), csv, new ReconciliationConfig());

        // First row ("Someone") matches; the duplicate-key coverage note
        // still fires exactly as it did before DisambiguationField existed.
        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("more than one row", issue.Description);
    }
}
