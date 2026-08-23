using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// Port of test_dimension_overrides.py (27 test defs; more individual
/// cases once [Theory]/[InlineData] are counted, same as the provenance
/// port). Synthetic IR throughout, same as the Python original: the three
/// tolerance figures are inherited placeholders, so nothing here asserts a
/// particular millimetre threshold is right - only that the rounding-grid
/// comparison behaves as designed and that skip-rather-than-guess cases are
/// counted instead of dropped.
/// </summary>
public class DimensionOverrideConsistencyCheckTests
{
    /// <summary>The real findings - the coverage Issue is always present and is asserted separately.</summary>
    private static List<Issue> Findings(List<Issue> issues) => issues.Where(i => i.Category != "coverage").ToList();

    private static Issue Coverage(List<Issue> issues)
    {
        var matching = issues.Where(i => i.Category == "coverage").ToList();
        Assert.True(matching.Count == 1, "exactly one coverage Issue per run");
        return matching[0];
    }

    private static RevitModel OneDimension(double? valueMm, string? overrideText, string? typeName = null, IEnumerable<ReferenceInfo>? refs = null)
    {
        var view = RevitCheckTestBuilders.View(10);
        var dim = RevitCheckTestBuilders.Dimension(1, 10, refs ?? new[] { RevitCheckTestBuilders.ModelRef() }, valueMm: valueMm, overrideText: overrideText, typeName: typeName);
        return RevitCheckTestBuilders.Model(views: new[] { view }, dimensions: new[] { dim });
    }

    // --- TestParseOverride (3 defs) ---

    [Theory]
    [InlineData("1200", 1200.0)]
    [InlineData(" 1200 ", 1200.0)]
    [InlineData("1200.5", 1200.5)]
    [InlineData("1200mm", 1200.0)]
    [InlineData("1200 MM", 1200.0)]
    [InlineData("1,200", 1200.0)]
    [InlineData("-15", -15.0)]
    public void NumericForms(string text, double expected) =>
        Assert.Equal(expected, DimensionOverrideConsistencyCheck.ParseOverrideMm(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EQ")]
    [InlineData("VARIES")]
    [InlineData("TYP")]
    [InlineData("A")] // a bar-mark letter keying into a schedule table
    [InlineData("500 MIN.")] // a limit, not a value - ParseOverrideBound's job
    [InlineData("1200-1400")]
    [InlineData("1,2")] // decimal comma is a different convention - not guessed at
    public void NonNumericFormsAreNotGuessed(string? text) =>
        Assert.Null(DimensionOverrideConsistencyCheck.ParseOverrideMm(text));

    [Fact]
    public void InvisibleFormatCharactersAreStripped()
    {
        // The DXF export carried a literal trailing U+200E on some override
        // text - invisible in an editor, so a valid override failed to
        // parse for a reason with no visible cause.
        Assert.Equal(1200.0, DimensionOverrideConsistencyCheck.ParseOverrideMm("1200‎"));
    }

    // --- TestToleranceBranches (4 defs) ---

    [Fact]
    public void WithinTheRoundingGridPasses()
    {
        // 1200 typed over a measured 1198.2: 1.8mm, inside 5/2 + 0.5.
        var model = OneDimension(1198.2, "1200");
        Assert.Empty(Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig())));
    }

    [Fact]
    public void BeyondTheRoundingGridIsFlagged()
    {
        var model = OneDimension(1150.0, "1200");
        var issues = Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig()));
        var issue = Assert.Single(issues);
        Assert.Equal("high", issue.Severity);
        Assert.Equal(50.0, issue.SuggestedFix!["delta_mm"]);
        Assert.Equal("default", issue.SuggestedFix!["tier"]);
        // Both values reach the reader - never just "these disagree".
        Assert.Contains("1200", issue.Description);
        Assert.Contains("1150", issue.Description);
    }

    [Fact]
    public void SetoutCriticalTypeGetsTheTighterGrid()
    {
        // 1.8mm passes on the default grid and fails on the tight one. Same
        // dimension, same override - only the type name differs.
        var config = new RuleConfig { SetoutCriticalTypeNames = new List<string> { "Setout - 1mm" } };

        var loose = OneDimension(1198.2, "1200");
        Assert.Empty(Findings(DimensionOverrideConsistencyCheck.Run(loose, config)));

        var tight = OneDimension(1198.2, "1200", typeName: "Setout - 1mm");
        var issues = Findings(DimensionOverrideConsistencyCheck.Run(tight, config));
        var issue = Assert.Single(issues);
        Assert.Equal("setout_critical", issue.SuggestedFix!["tier"]);
    }

    [Fact]
    public void AnUnlistedTypeNameIsNotAssumedCritical()
    {
        var model = OneDimension(1198.2, "1200", typeName: "Some Other Style");
        Assert.Empty(Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig())));
    }

    // --- TestWhatIsSkipped (4 defs) ---

    [Fact]
    public void ADimensionWithNoOverrideIsNotCompared()
    {
        // Wildly wrong, but nobody typed anything - there's no claim to
        // check. This rule covers workaround 1 only.
        var model = OneDimension(5.0, null);
        var issues = DimensionOverrideConsistencyCheck.Run(model, new RuleConfig());
        Assert.Empty(Findings(issues));
        Assert.Equal(0, Coverage(issues).SuggestedFix!["overridden"]);
    }

    [Fact]
    public void ANonNumericOverrideIsCountedNotGuessed()
    {
        var model = OneDimension(1150.0, "VARIES");
        var issues = DimensionOverrideConsistencyCheck.Run(model, new RuleConfig());
        Assert.Empty(Findings(issues));
        var summary = Coverage(issues).SuggestedFix!;
        Assert.Equal(1, summary["overridden"]);
        Assert.Equal(0, summary["checked"]);
        Assert.Equal(1, summary["unparsed"]);
        Assert.Contains("'VARIES'", Coverage(issues).Description);
    }

    [Fact]
    public void NoMeasuredValueIsSkipped()
    {
        // Revit reports no value for some spot dimension types. Nothing to
        // compare against, so it's not a finding and not "checked".
        var model = OneDimension(null, "1200");
        var issues = DimensionOverrideConsistencyCheck.Run(model, new RuleConfig());
        Assert.Empty(Findings(issues));
        Assert.Equal(0, Coverage(issues).SuggestedFix!["checked"]);
    }

    [Fact]
    public void UnsheetedViewsAreOutOfScopeByDefault()
    {
        var view = RevitCheckTestBuilders.View(10, sheetNo: null);
        var dim = RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.ModelRef() }, valueMm: 1150.0, overrideText: "1200");
        var model = RevitCheckTestBuilders.Model(views: new[] { view }, dimensions: new[] { dim });

        Assert.Empty(Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig())));

        var swept = DimensionOverrideConsistencyCheck.Run(model, new RuleConfig { SheetedViewsOnly = false });
        Assert.Single(Findings(swept));
    }

    // --- TestChains (2 defs) ---

    [Fact]
    public void OnlyTheOverriddenSegmentIsCompared()
    {
        // A chain is one element with many segments. Two segments here are
        // wrong but untyped; only the third makes a claim.
        var view = RevitCheckTestBuilders.View(10);
        var dim = RevitCheckTestBuilders.Chain(1, 10, new[] { RevitCheckTestBuilders.ModelRef() },
            new (double?, string?)[] { (500.0, null), (600.0, null), (1150.0, "1200") });
        var model = RevitCheckTestBuilders.Model(views: new[] { view }, dimensions: new[] { dim });

        var issues = Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig()));
        var issue = Assert.Single(issues);
        Assert.Equal(3, issue.SuggestedFix!["segment"]);
        Assert.Equal(3, issue.SuggestedFix!["segments"]);
        // The element id selects the chain; the description says which
        // number inside it to look at.
        Assert.Equal(1, issue.ElementId);
        Assert.Contains("Segment 3 of 3", issue.Description);
    }

    [Fact]
    public void EverySegmentCountsTowardsCoverage()
    {
        var view = RevitCheckTestBuilders.View(10);
        var dim = RevitCheckTestBuilders.Chain(1, 10, new[] { RevitCheckTestBuilders.ModelRef() },
            new (double?, string?)[] { (500.0, null), (1200.0, "1200") });
        var model = RevitCheckTestBuilders.Model(views: new[] { view }, dimensions: new[] { dim });

        var summary = Coverage(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig())).SuggestedFix!;
        Assert.Equal(2, summary["segments"]);
        Assert.Equal(1, summary["overridden"]);
        Assert.Equal(1, summary["checked"]);
    }

    // --- TestProvenanceTravelsWithTheFinding (2 defs) ---

    [Theory]
    [InlineData("model")]
    [InlineData("drafted")]
    [InlineData("datum")]
    public void VerdictIsCarried(string refKind)
    {
        ReferenceInfo reference = refKind switch
        {
            "model" => RevitCheckTestBuilders.ModelRef(),
            "drafted" => RevitCheckTestBuilders.DraftedRef(),
            "datum" => RevitCheckTestBuilders.DatumRef(),
            _ => throw new ArgumentOutOfRangeException(nameof(refKind)),
        };
        var model = OneDimension(1150.0, "1200", refs: new[] { reference });
        var issues = Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig()));
        Assert.Equal(refKind, issues[0].SuggestedFix!["provenance"]);
    }

    [Fact]
    public void ADraftedDimensionIsStillReported()
    {
        // Not filtered out. Per the standing position: assume nothing is
        // trustworthy. A drafter disagreeing with their own linework by
        // 50mm is worth knowing about.
        var model = OneDimension(1150.0, "1200", refs: new[] { RevitCheckTestBuilders.DraftedRef() });
        Assert.Single(Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig())));
    }

    // --- TestCoverageIsAlwaysReported (4 defs) ---

    [Fact]
    public void ACleanRunStillSaysHowMuchItChecked()
    {
        var model = OneDimension(1200.0, "1200");
        var issues = DimensionOverrideConsistencyCheck.Run(model, new RuleConfig());
        Assert.Empty(Findings(issues));
        var summary = Coverage(issues);
        Assert.Equal("low", summary.Severity);
        Assert.Equal(1, summary.SuggestedFix!["checked"]);
    }

    [Fact]
    public void NothingCheckableSaysSoExplicitly()
    {
        var view = RevitCheckTestBuilders.View(10);
        var dims = Enumerable.Range(1, 3)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.ModelRef() }, valueMm: 1000.0, overrideText: "EQ"))
            .ToArray();
        var model = RevitCheckTestBuilders.Model(views: new[] { view }, dimensions: dims);

        var summary = Coverage(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig()));
        Assert.Equal(0, summary.SuggestedFix!["checked"]);
        Assert.Contains("nothing to check", summary.Description);
        Assert.Contains("'EQ' x3", summary.Description);
    }

    [Fact]
    public void AnEmptyModelIsNotSilence()
    {
        var summary = Coverage(DimensionOverrideConsistencyCheck.Run(RevitCheckTestBuilders.Model(), new RuleConfig()));
        Assert.Contains("No dimensions were found", summary.Description);
    }

    [Fact]
    public void DistinctUnparsedFormsAreListedForRecognition()
    {
        // A new client's override convention should surface as data, not
        // as an absence of findings.
        var view = RevitCheckTestBuilders.View(10);
        var dims = new[]
        {
            RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.ModelRef() }, overrideText: "EQ"),
            // A real Flinders override. Not a number, not a limit - it's
            // AutoCAD field syntax that survived the round trip.
            RevitCheckTestBuilders.Dimension(2, 10, new[] { RevitCheckTestBuilders.ModelRef() }, overrideText: "<>\\XMIN"),
            RevitCheckTestBuilders.Dimension(3, 10, new[] { RevitCheckTestBuilders.ModelRef() }, overrideText: "VARIES"),
        };
        var model = RevitCheckTestBuilders.Model(views: new[] { view }, dimensions: dims);

        var description = Coverage(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig())).Description;
        foreach (var form in new[] { "'EQ'", "XMIN", "'VARIES'" })
        {
            Assert.Contains(form, description);
        }
    }

    // --- TestLimitOverrides (8 defs) ---

    [Theory]
    [InlineData("500 MIN.", 500.0, ">=")]
    [InlineData("500 MIN", 500.0, ">=")]
    [InlineData("500MIN", 500.0, ">=")]
    [InlineData("MIN 500", 500.0, ">=")]
    [InlineData("MIN. 500", 500.0, ">=")]
    [InlineData("min 500", 500.0, ">=")]
    [InlineData("1200 MAX", 1200.0, "<=")]
    [InlineData("1200 MAX.", 1200.0, "<=")]
    [InlineData("500 MIN. mm", 500.0, ">=")]
    public void RecognisedForms(string text, double expectedValue, string expectedComparator)
    {
        var result = DimensionOverrideConsistencyCheck.ParseOverrideBound(text);
        Assert.NotNull(result);
        Assert.Equal(expectedValue, result!.Value.Value);
        Assert.Equal(expectedComparator, result.Value.Comparator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1200")]
    [InlineData("EQ")]
    [InlineData("MIN")]
    [InlineData("500 MINIMUM")]
    [InlineData("500 MIN 600")]
    public void EverythingElseIsStillNotGuessed(string? text) =>
        Assert.Null(DimensionOverrideConsistencyCheck.ParseOverrideBound(text));

    [Fact]
    public void ASatisfiedMinimumIsNotAFinding()
    {
        var model = OneDimension(620.0, "500 MIN.");
        Assert.Empty(Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig())));
    }

    [Fact]
    public void AViolatedMinimumIsFlagged()
    {
        var model = OneDimension(480.0, "500 MIN.");
        var issues = Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig()));
        var issue = Assert.Single(issues);
        Assert.Equal(">=", issue.SuggestedFix!["comparator"]);
        Assert.Equal(500.0, issue.SuggestedFix!["stated_limit_mm"]);
        Assert.Equal(480.0, issue.SuggestedFix!["measured_mm"]);
        Assert.Contains("at least 500mm", issue.Description);
    }

    [Fact]
    public void AViolatedMaximumIsFlagged()
    {
        var model = OneDimension(1250.0, "1200 MAX");
        var issues = Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig()));
        var issue = Assert.Single(issues);
        Assert.Equal("<=", issue.SuggestedFix!["comparator"]);
        Assert.Contains("at most 1200mm", issue.Description);
    }

    [Fact]
    public void TheRoundingGridDoesNotApplyToALimit()
    {
        // 2mm below a stated minimum is a violation, even though the same
        // 2mm on an exact override would be inside the default grid. A
        // limit isn't a rounded restatement of anything, so allowing grid
        // slack below it would invent tolerance the drawing doesn't offer.
        // Only measurement noise is allowed.
        var exact = OneDimension(498.0, "500");
        Assert.Empty(Findings(DimensionOverrideConsistencyCheck.Run(exact, new RuleConfig())));

        var limit = OneDimension(498.0, "500 MIN.");
        Assert.Single(Findings(DimensionOverrideConsistencyCheck.Run(limit, new RuleConfig())));
    }

    [Fact]
    public void MeasurementNoiseIsStillAllowed()
    {
        // 0.2mm under, inside MeasurementEpsilonMm (0.5).
        var model = OneDimension(499.8, "500 MIN.");
        Assert.Empty(Findings(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig())));
    }

    [Fact]
    public void LimitsAreCountedSeparatelyInCoverage()
    {
        var view = RevitCheckTestBuilders.View(10);
        var dims = new[]
        {
            RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.ModelRef() }, valueMm: 1200.0, overrideText: "1200"),
            RevitCheckTestBuilders.Dimension(2, 10, new[] { RevitCheckTestBuilders.ModelRef() }, valueMm: 620.0, overrideText: "500 MIN."),
        };
        var model = RevitCheckTestBuilders.Model(views: new[] { view }, dimensions: dims);

        var summary = Coverage(DimensionOverrideConsistencyCheck.Run(model, new RuleConfig()));
        Assert.Equal(2, summary.SuggestedFix!["checked"]);
        Assert.Equal(1, summary.SuggestedFix!["bounds"]);
        Assert.Contains("MIN/MAX limit", summary.Description);
    }
}
