using System.Collections.Generic;
using System.Linq;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// What Check Dimensions records for one view. As first built (2026-09-11)
/// the command ran Pile Chain Bearing and recorded its investigated scope,
/// but never ran the pile dimension check - so a pile run with the right
/// bearing reconciled every dimension on it as clean, including one stating
/// the wrong distance. PLANNING.md §27's trap, reintroduced one layer up and
/// invisible to every test, because it lived in a command.
/// </summary>
public class DimensionInvestigationTests
{
    private const long ViewId = 10;

    /// <summary>
    /// Three piles due north, 1000mm apart, dimensioned tag to tag, with a
    /// bearing call beside them - the clean chain Pile Chain Bearing's own
    /// tests use. Only the stated values and the call vary.
    /// </summary>
    private static RevitModel PileRun(
        double secondStatedMm = 1000.0,
        string? secondOverride = null,
        string bearingCall = "0° 00' 00\"",
        IEnumerable<DimensionInfo>? extraDimensions = null)
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var pileC = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);

        var dimensions = new List<DimensionInfo>
        {
            TagToTag(100, pileA, pileB, 1000.0),
            TagToTag(101, pileB, pileC, secondStatedMm, secondOverride),
        };
        dimensions.AddRange(extraDimensions ?? Enumerable.Empty<DimensionInfo>());

        return RevitCheckTestBuilders.Model(
            views: new[]
            {
                RevitCheckTestBuilders.View(ViewId, name: "PILE LAYOUT", viewType: "EngineeringPlan", sheetNo: "S201"),
            },
            elements: new[] { pileA, pileB, pileC },
            dimensions: dimensions,
            textNotes: new[]
            {
                RevitCheckTestBuilders.TextNote(900, ViewId, bearingCall, RevitCheckTestBuilders.Pt(50, 500)),
            });
    }

    private static DimensionInfo TagToTag(
        long id, ElementMetadata a, ElementMetadata b, double statedMm, string? overrideText = null) =>
        RevitCheckTestBuilders.Dimension(
            id, ViewId,
            new[]
            {
                RevitCheckTestBuilders.TagRef(id * 10, a.LocalPoint!),
                RevitCheckTestBuilders.TagRef((id * 10) + 1, b.LocalPoint!),
            },
            valueMm: statedMm,
            overrideText: overrideText);

    private static (List<Issue> Issues, List<long> Investigated) Run(RevitModel model) =>
        DimensionInvestigation.Run(model, new RuleConfig(), ViewId, "PILE LAYOUT", "S201");

    /// <summary>
    /// The tests below mean nothing unless bearing really is satisfied by
    /// this run while still claiming its dimensions - which is the trap.
    /// </summary>
    [Fact]
    public void The_fixture_satisfies_the_bearing_check_while_it_claims_every_dimension()
    {
        var bearing = PileChainBearingConsistencyCheck.RunWithScope(PileRun(secondStatedMm: 1050.0), new RuleConfig());

        Assert.Empty(bearing.Issues);
        Assert.Contains(101L, bearing.InvestigatedDimensionElementIds);
    }

    /// <summary>
    /// The regression. The run's bearing is right, so Pile Chain Bearing is
    /// satisfied - but dimension 101 states 1050 where the piles are 1000
    /// apart. As first built, Check Dimensions reconciled it as clean.
    /// </summary>
    [Fact]
    public void A_pile_dimension_stating_the_wrong_distance_is_a_confirmed_problem_even_when_the_bearing_is_right()
    {
        var model = PileRun(secondStatedMm: 1050.0);
        var (issues, investigated) = Run(model);

        var finding = Assert.Single(
            issues, i => i.RuleId == PileDimensionConsistencyCheck.RuleId && i.Category == "geometry");
        Assert.Equal(101, finding.ElementId);
        Assert.Contains("50mm out", finding.Description);
        Assert.Equal("PILE LAYOUT", finding.ViewName);
        Assert.Equal("S201", finding.SheetNo);

        var result = InvestigationReconciliation.Reconcile(
            DimensionProvenanceCheck.Run(model, new RuleConfig()), investigated, issues);
        Assert.Contains(result.ConfirmedProblems, i => i.ElementId == 101);
    }

    /// <summary>
    /// "Investigated" has to mean the dimension's own value was compared.
    /// Bearing claims every dimension in the run; one whose override is not
    /// a number was compared by nothing, so it must not be counted.
    /// </summary>
    [Fact]
    public void A_dimension_whose_value_nothing_compared_is_not_counted_as_investigated()
    {
        var (_, investigated) = Run(PileRun(secondOverride: "TYP."));

        Assert.Contains(100L, investigated);
        Assert.DoesNotContain(101L, investigated);
    }

    /// <summary>
    /// Bearing still reports - its findings land on every dimension in the
    /// run, in this view, and are listed so a re-run replaces them.
    /// </summary>
    [Fact]
    public void A_bearing_finding_still_lands_on_every_dimension_in_the_run()
    {
        var (issues, investigated) = Run(PileRun(bearingCall: "90° 00' 00\""));

        var bearing = issues
            .Where(i => i.RuleId == PileChainBearingConsistencyCheck.RuleId && i.Category == "geometry")
            .ToList();
        Assert.Equal(new long?[] { 100, 101 }, bearing.Select(i => i.ElementId).OrderBy(id => id));
        Assert.All(bearing, i =>
        {
            Assert.Equal("PILE LAYOUT", i.ViewName);
            Assert.Equal("S201", i.SheetNo);
        });
        Assert.Contains(101L, investigated);
    }

    /// <summary>
    /// A contradiction between two dimensions of one pile pair names both,
    /// and nothing can say which is wrong - so neither may reconcile as
    /// clean. Unexpanded, only the first would carry the finding.
    /// </summary>
    [Fact]
    public void A_contradiction_between_two_dimensions_lands_on_both()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var (issues, _) = Run(PileRun(extraDimensions: new[] { TagToTag(102, pileA, pileB, 1100.0) }));

        var contradiction = issues.Where(i => i.Description.Contains("disagree by")).ToList();
        Assert.Equal(new long?[] { 100, 102 }, contradiction.Select(i => i.ElementId).OrderBy(id => id));
    }
}
