using RevitCheck.Core.Checks;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// Real-data-shaped scenarios for PileChainBearingConsistencyCheck. The
/// "real 2-pile chain" test uses the actual PIL232115/PIL232116 figures
/// from InspectDimensionGeometry.pushbutton's real 2026-08-26 run
/// (PLANNING.md §14), not invented ones - a passing test here reflects
/// the real precision the check needs to handle.
/// </summary>
public class PileChainBearingConsistencyCheckTests
{
    [Fact]
    public void Real_two_pile_chain_matches_its_real_printed_bearing_call()
    {
        // PIL232116 (element 7926092) and PIL232115 (element 7926091),
        // real local/project positions - dimension 8174725's own two
        // AnnotationSymbol references (7941127/7941112) sit 0.08mm and
        // 0.22mm from their own pile, real tag-on-pile placement. The
        // matched note is the real "165° 13' 08"" TextNote (element
        // 8802924), positioned 7724mm from the chain - well inside the
        // default 10m cap.
        var pileA = RevitCheckTestBuilders.Pile(7926092, "PIL232116", 206708.88855549053, 1201898.9253552181);
        var pileB = RevitCheckTestBuilders.Pile(7926091, "PIL232115", 206198.63146068316, 1203832.7396424038);

        var dim = RevitCheckTestBuilders.PileChainDimension(
            8174725, 1,
            RevitCheckTestBuilders.TagRef(7941127, RevitCheckTestBuilders.Pt(206708.90860901462, 1201898.8493549456)),
            RevitCheckTestBuilders.TagRef(7941112, RevitCheckTestBuilders.Pt(206198.6870528184, 1203832.5289547609)));

        var note = RevitCheckTestBuilders.TextNote(
            8802924, 1, "165° 13' 08\"\r", RevitCheckTestBuilders.Pt(205982.15142232634, 1194208.5224849312));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB },
            dimensions: new[] { dim },
            textNotes: new[] { note });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void Synthetic_clean_chain_with_a_matching_note_reports_nothing()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 1000)));
        // Due north (0deg) or due south (180deg, the reciprocal) - use a
        // note reading exactly "0° 00' 00"".
        var note = RevitCheckTestBuilders.TextNote(300, 1, "0° 00' 00\"", RevitCheckTestBuilders.Pt(50, 500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB }, dimensions: new[] { dim }, textNotes: new[] { note });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void Chain_disagreeing_with_its_matched_note_is_flagged_high()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000); // due north, 0deg
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 1000)));
        // Printed as 90 deg (due east) - a real 90 degree drafting/model
        // disagreement, not invented noise near the tolerance boundary.
        var note = RevitCheckTestBuilders.TextNote(300, 1, "90° 00' 00\"", RevitCheckTestBuilders.Pt(50, 500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB }, dimensions: new[] { dim }, textNotes: new[] { note });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal(PileChainBearingConsistencyCheck.RuleId, issue.RuleId);
        Assert.Equal("geometry", issue.Category);
        Assert.Equal("high", issue.Severity);
        Assert.Equal(1, issue.ElementId);
        Assert.NotNull(issue.SuggestedFix);
        Assert.Contains("90° 00' 00\"", issue.Description);
    }

    [Fact]
    public void No_note_within_range_reports_coverage_not_a_confirmed_problem()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 1000)));
        // Real confirmed case, PLANNING.md §14: a real printed bearing
        // note that belongs to a chain outside this run's scope stayed
        // unmatched rather than being force-matched - this mirrors that,
        // a note far outside the default 10m cap.
        var farNote = RevitCheckTestBuilders.TextNote(300, 1, "0° 00' 00\"", RevitCheckTestBuilders.Pt(50_000, 500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB }, dimensions: new[] { dim }, textNotes: new[] { farNote });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("no bearing call was found", issue.Description);
    }

    [Fact]
    public void No_pile_category_elements_reports_low_severity_coverage()
    {
        var model = RevitCheckTestBuilders.Model(
            elements: new[] { RevitCheckTestBuilders.Element(1, category: "Structural Framing") });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("low", issue.Severity);
        Assert.Equal("coverage", issue.Category);
    }

    [Fact]
    public void Branched_network_is_reported_as_coverage_not_evaluated_as_a_chain()
    {
        var hub = RevitCheckTestBuilders.Pile(1, "HUB", 0, 0);
        var a = RevitCheckTestBuilders.Pile(2, "A", 0, 1000);
        var b = RevitCheckTestBuilders.Pile(3, "B", 1000, 0);
        var c = RevitCheckTestBuilders.Pile(4, "C", 0, -1000);

        var dims = new[]
        {
            RevitCheckTestBuilders.PileChainDimension(101, 1,
                RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 0)),
                RevitCheckTestBuilders.TagRef(202, RevitCheckTestBuilders.Pt(0, 1000))),
            RevitCheckTestBuilders.PileChainDimension(102, 1,
                RevitCheckTestBuilders.TagRef(203, RevitCheckTestBuilders.Pt(0, 0)),
                RevitCheckTestBuilders.TagRef(204, RevitCheckTestBuilders.Pt(1000, 0))),
            RevitCheckTestBuilders.PileChainDimension(103, 1,
                RevitCheckTestBuilders.TagRef(205, RevitCheckTestBuilders.Pt(0, 0)),
                RevitCheckTestBuilders.TagRef(206, RevitCheckTestBuilders.Pt(0, -1000))),
        };

        var model = RevitCheckTestBuilders.Model(elements: new[] { hub, a, b, c }, dimensions: dims);

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("branched or cyclic", issue.Description);
    }

    [Fact]
    public void Chain_shorter_than_the_configured_minimum_is_skipped_entirely()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 1000)));
        // No note at all - if the 2-pile chain were evaluated, this would
        // produce a coverage issue. Raising the minimum to 3 should skip
        // it entirely instead.
        var model = RevitCheckTestBuilders.Model(elements: new[] { pileA, pileB }, dimensions: new[] { dim });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig { PileChainMinimumPiles = 3 });

        Assert.Empty(issues);
    }
}
