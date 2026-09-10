using System.Collections.Generic;
using System.Linq;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// The half the bearing check never did. It marks every dimension in an
/// evaluated run as investigated - which reconciles the triage flag on it -
/// having used those dimensions only as chain topology, never reading a
/// single one's value. So a drafted "3755" was reconciled away without
/// anything ever comparing it to anything.
/// </summary>
public class PileDimensionConsistencyCheckTests
{
    /// <summary>Two piles a known distance apart, dimensioned tag to tag.</summary>
    private static RevitModel Model(
        double actualSpacingMm,
        double? statedMm,
        string? overrideText = null,
        long dimensionId = 100,
        long viewId = 1)
    {
        // Tags are matched to piles by local point, so the piles carry both
        // frames - survey for the distance, local for the tag join.
        var pileA = RevitCheckTestBuilders.Pile(
            1, "P1", 278000000.0, 6130000000.0, localPoint: RevitCheckTestBuilders.Pt(0, 0));
        var pileB = RevitCheckTestBuilders.Pile(
            2, "P2", 278000000.0, 6130000000.0 + actualSpacingMm,
            localPoint: RevitCheckTestBuilders.Pt(0, actualSpacingMm));

        var dimension = RevitCheckTestBuilders.Dimension(
            dimensionId, viewId,
            new[]
            {
                RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 0)),
                RevitCheckTestBuilders.TagRef(202, RevitCheckTestBuilders.Pt(0, actualSpacingMm)),
            },
            valueMm: statedMm,
            overrideText: overrideText);

        return RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB }, dimensions: new[] { dimension });
    }

    [Fact]
    public void A_dimension_agreeing_with_the_model_reports_nothing()
    {
        var issues = PileDimensionConsistencyCheck.Run(Model(3755.0, 3755.0), new RuleConfig());

        Assert.Empty(issues.Where(i => i.Category == "geometry"));
    }

    /// <summary>
    /// The real staleness case, and the one triage exists to raise: the
    /// drawing still says 3755 while the piles have moved 50mm apart.
    /// </summary>
    [Fact]
    public void A_dimension_the_model_disagrees_with_is_flagged_at_its_real_magnitude()
    {
        var issues = PileDimensionConsistencyCheck.Run(Model(3805.0, 3755.0), new RuleConfig());

        var issue = Assert.Single(issues, i => i.Category == "geometry");
        Assert.Equal("high", issue.Severity);
        Assert.Equal(100, issue.ElementId);
        Assert.Contains("50mm out", issue.Description);
    }

    /// <summary>
    /// A typed override is what a reader of the sheet acts on, so it is
    /// what gets checked - a drafter who types 3755 over a measurement of
    /// 3805 has stated something the model does not.
    /// </summary>
    [Fact]
    public void A_typed_override_is_what_gets_checked_not_the_measurement()
    {
        var issues = PileDimensionConsistencyCheck.Run(
            Model(3805.0, 3805.0, overrideText: "3755"), new RuleConfig());

        var issue = Assert.Single(issues, i => i.Category == "geometry");
        Assert.Contains("states 3755mm", issue.Description);
    }

    /// <summary>
    /// An override that states something this comparison cannot use is
    /// skipped and counted, never guessed at - the real shape from the
    /// archived pipeline, where one client's overrides were 4.5% numeric
    /// and included '500 MIN.' and bar marks.
    /// </summary>
    [Fact]
    public void A_non_numeric_override_is_skipped_and_reported_rather_than_guessed()
    {
        var issues = PileDimensionConsistencyCheck.Run(
            Model(3805.0, 3805.0, overrideText: "500 MIN."), new RuleConfig());

        Assert.Empty(issues.Where(i => i.Category == "geometry"));
        // Named, not just counted - it is a dimension a reviewer still has
        // to settle by hand.
        Assert.Contains(issues, i => i.Category == "coverage" && i.Description.Contains("Not compared")
            && i.Description.Contains("100"));
    }

    /// <summary>
    /// Dimension against dimension: the same two piles measured on two
    /// sheets, stating different distances. Neither needs the model to be
    /// wrong - two drawings simply cannot both be right - and this is the
    /// case the model comparison alone cannot catch when each is
    /// individually plausible.
    /// </summary>
    [Fact]
    public void The_same_pile_pair_dimensioned_twice_with_different_values_is_a_contradiction()
    {
        var pileA = RevitCheckTestBuilders.Pile(
            1, "P1", 278000000.0, 6130000000.0, localPoint: RevitCheckTestBuilders.Pt(0, 0));
        var pileB = RevitCheckTestBuilders.Pile(
            2, "P2", 278000000.0, 6130003755.0, localPoint: RevitCheckTestBuilders.Pt(0, 3755));

        DimensionInfo Dim(long id, long viewId, double stated) =>
            RevitCheckTestBuilders.Dimension(
                id, viewId,
                new[]
                {
                    RevitCheckTestBuilders.TagRef(200 + id, RevitCheckTestBuilders.Pt(0, 0)),
                    RevitCheckTestBuilders.TagRef(300 + id, RevitCheckTestBuilders.Pt(0, 3755)),
                },
                valueMm: stated);

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB },
            dimensions: new[] { Dim(100, 1, 3755.0), Dim(101, 2, 3855.0) });

        var issues = PileDimensionConsistencyCheck.Run(model, new RuleConfig());

        var contradiction = Assert.Single(issues, i => i.Description.Contains("disagree by"));
        Assert.Equal("high", contradiction.Severity);
        Assert.Contains("100mm", contradiction.Description);
        var ids = Assert.IsType<List<long>>(
            Assert.Contains("dimension_element_ids", contradiction.SuggestedFix!));
        Assert.Equal(new[] { 100L, 101L }, ids);
    }

    /// <summary>The same answer stated twice is worth nothing on its own, and is not a finding.</summary>
    [Fact]
    public void The_same_pile_pair_dimensioned_twice_in_agreement_is_not_a_finding()
    {
        var pileA = RevitCheckTestBuilders.Pile(
            1, "P1", 278000000.0, 6130000000.0, localPoint: RevitCheckTestBuilders.Pt(0, 0));
        var pileB = RevitCheckTestBuilders.Pile(
            2, "P2", 278000000.0, 6130003755.0, localPoint: RevitCheckTestBuilders.Pt(0, 3755));

        DimensionInfo Dim(long id, long viewId) =>
            RevitCheckTestBuilders.Dimension(
                id, viewId,
                new[]
                {
                    RevitCheckTestBuilders.TagRef(200 + id, RevitCheckTestBuilders.Pt(0, 0)),
                    RevitCheckTestBuilders.TagRef(300 + id, RevitCheckTestBuilders.Pt(0, 3755)),
                },
                valueMm: 3755.0);

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB },
            dimensions: new[] { Dim(100, 1), Dim(101, 2) });

        Assert.Empty(PileDimensionConsistencyCheck.Run(model, new RuleConfig())
            .Where(i => i.Category == "geometry"));
    }

    /// <summary>
    /// A dimension it actually compared counts as investigated, so the
    /// triage flag on it reconciles - and one it could not compare does
    /// not, which is the distinction the bearing check got wrong.
    /// </summary>
    [Fact]
    public void Only_a_dimension_actually_compared_counts_as_investigated()
    {
        var compared = PileDimensionConsistencyCheck.RunWithScope(
            Model(3755.0, 3755.0), new RuleConfig());
        Assert.Equal(new[] { 100L }, compared.InvestigatedElementIds);

        var skipped = PileDimensionConsistencyCheck.RunWithScope(
            Model(3805.0, 3805.0, overrideText: "BAR A"), new RuleConfig());
        Assert.Empty(skipped.InvestigatedElementIds);
    }

    [Fact]
    public void No_piles_in_scope_reports_coverage_rather_than_silence()
    {
        var issues = PileDimensionConsistencyCheck.Run(
            RevitCheckTestBuilders.Model(elements: new[] { RevitCheckTestBuilders.Element(1, category: "Walls") }),
            new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
    }
}
