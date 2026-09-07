using RevitCheck.Core.Checks;
using RevitCheck.Core.Reporting;
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

    [Fact]
    public void RunWithScope_reports_an_evaluated_chains_dimension_as_investigated_whether_clean_or_flagged()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000); // due north, 0deg
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 1000)));
        // Printed 90deg - a real disagreement, so this chain gets flagged,
        // not clean. RunWithScope should still count its dimension as
        // investigated - a verdict was reached, whether or not it passed.
        var note = RevitCheckTestBuilders.TextNote(300, 1, "90° 00' 00\"", RevitCheckTestBuilders.Pt(50, 500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB }, dimensions: new[] { dim }, textNotes: new[] { note });

        var (issues, investigated) = PileChainBearingConsistencyCheck.RunWithScope(model, new RuleConfig());

        Assert.Single(issues);
        Assert.Equal(new[] { 100L }, investigated);
    }

    [Fact]
    public void RunWithScope_excludes_a_chain_too_short_to_evaluate_from_the_investigated_scope()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 1000)));
        var model = RevitCheckTestBuilders.Model(elements: new[] { pileA, pileB }, dimensions: new[] { dim });

        var (issues, investigated) = PileChainBearingConsistencyCheck.RunWithScope(model, new RuleConfig { PileChainMinimumPiles = 3 });

        Assert.Empty(issues);
        // This check never reached a verdict on dimension 100 - it must
        // not be claimed as investigated, or a real triage finding on it
        // would silently reconcile as clean.
        Assert.Empty(investigated);
    }

    [Fact]
    public void A_dimension_matching_one_pile_and_missing_the_other_is_flagged_for_manual_review_not_silently_dropped()
    {
        // The real confirmed case (PLANNING.md §14): one reference matches
        // a real pile at ~0mm, the other misses every pile by a real
        // margin (1274.5mm in the real case) - turned out to be dimensioned
        // to a setout-point marker, not a pile. Before this fix,
        // ResolvePileMatch returning null for this dimension meant it
        // produced no Issue of any kind, anywhere - not a problem, not
        // triage staying open, not manual review, nothing.
        var pile = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)), // 0mm from the real pile
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(5000, 5000))); // nowhere near any pile

        var model = RevitCheckTestBuilders.Model(elements: new[] { pile }, dimensions: new[] { dim });

        var (issues, investigated) = PileChainBearingConsistencyCheck.RunWithScope(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal(RevitCheck.Core.Reporting.InvestigationReconciliation.ManualReviewCategory, issue.Category);
        Assert.Equal(100, issue.ElementId);
        // Manual review still counts as examined - the whole point is that
        // a human judgement is an examination too, so a view-rollup finding
        // covering this dimension can still clear once every dimension has
        // some verdict, clean/problem/manual-review alike.
        Assert.Equal(new[] { 100L }, investigated);
    }

    [Fact]
    public void A_dimension_matching_the_same_pile_at_both_ends_is_flagged_for_manual_review()
    {
        var pile = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(2, 2))); // same real pile, not a real pair

        var model = RevitCheckTestBuilders.Model(elements: new[] { pile }, dimensions: new[] { dim });

        var (issues, _) = PileChainBearingConsistencyCheck.RunWithScope(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal(RevitCheck.Core.Reporting.InvestigationReconciliation.ManualReviewCategory, issue.Category);
    }

    [Fact]
    public void A_dimension_nowhere_near_any_pile_is_left_alone_not_flagged_for_manual_review()
    {
        // Most dimensions in a pile-layout view have nothing to do with
        // piles at all (bearing notes, scale bars, unrelated linework) -
        // flagging every one of those for manual review would bury the
        // real signal, exactly the failure mode this check's design
        // already avoids elsewhere ("skip rather than guess").
        var pile = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(50_000, 50_000)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(50_000, 51_000)));

        var model = RevitCheckTestBuilders.Model(elements: new[] { pile }, dimensions: new[] { dim });

        var (issues, investigated) = PileChainBearingConsistencyCheck.RunWithScope(model, new RuleConfig());

        Assert.Empty(issues);
        Assert.Empty(investigated);
    }

    [Fact]
    public void A_dimension_already_part_of_a_real_evaluated_chain_is_not_also_flagged_for_manual_review()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 1000)));
        var note = RevitCheckTestBuilders.TextNote(300, 1, "0° 00' 00\"", RevitCheckTestBuilders.Pt(50, 500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB }, dimensions: new[] { dim }, textNotes: new[] { note });

        var (issues, _) = PileChainBearingConsistencyCheck.RunWithScope(model, new RuleConfig());

        // Clean chain - no issue of any kind, and definitely not a
        // duplicate manual-review flag for the same dimension.
        Assert.Empty(issues);
    }

    [Fact]
    public void RunWithScope_excludes_an_ambiguous_branched_components_dimensions_from_the_investigated_scope()
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

        var (issues, investigated) = PileChainBearingConsistencyCheck.RunWithScope(model, new RuleConfig());

        Assert.Single(issues);
        Assert.Empty(investigated);
    }

    /// <summary>
    /// The real 2026-09-07 false positive, reproduced with this project's
    /// own real bearing figures: two setout lines (165°13'26" and
    /// 165°07'01", both real calls on DRG-2873041 - PILE LAYOUT, only 6'25"
    /// apart) meeting at a shared pile. Tag-to-tag dimensioning connects
    /// them into one topologically simple chain, and the old
    /// endpoint-to-endpoint bearing measured straight across the corner:
    /// 165°10'13", which is 192.5 arcseconds from BOTH real calls - well
    /// beyond the 60-arcsecond tolerance, so a correct drawing got flagged.
    /// Each leg must now be checked against its own call instead, with the
    /// corner itself reported for a human rather than silently averaged.
    /// </summary>
    [Fact]
    public void Two_setout_lines_meeting_at_a_shared_pile_are_not_flagged_as_one_wrong_bearing()
    {
        // Leg A: piles 1→2→3 on 165°13'26". Leg B: piles 3→4→5 on
        // 165°07'01". Pile 3 is the shared corner.
        var p1 = RevitCheckTestBuilders.Pile(1, "P1", 0.0, 0.0);
        var p2 = RevitCheckTestBuilders.Pile(2, "P2", 765.1278858813357, -2900.7894301804736);
        var p3 = RevitCheckTestBuilders.Pile(3, "P3", 1530.2557717626714, -5801.578860360947);
        var p4 = RevitCheckTestBuilders.Pile(4, "P4", 2300.796739915849, -8700.935102080402);
        var p5 = RevitCheckTestBuilders.Pile(5, "P5", 3071.337708069026, -11600.291343799858);

        var dims = new[]
        {
            Edge(101, p1, p2, 201, 202),
            Edge(102, p2, p3, 203, 204),
            Edge(103, p3, p4, 205, 206),
            Edge(104, p4, p5, 207, 208),
        };

        // One real bearing call beside each leg, each nearest to its own.
        var noteA = RevitCheckTestBuilders.TextNote(301, 1, "165° 13' 26\"", RevitCheckTestBuilders.Pt(100.0, 0.0));
        var noteB = RevitCheckTestBuilders.TextNote(302, 1, "165° 07' 01\"", RevitCheckTestBuilders.Pt(3171.337708069026, -11600.291343799858));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { p1, p2, p3, p4, p5 },
            dimensions: dims,
            textNotes: new[] { noteA, noteB });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        // The regression: neither leg disagrees with its own call, so
        // nothing here is a confirmed bearing problem.
        Assert.DoesNotContain(issues, i => i.Category == "geometry");

        // The corner is reported instead - for a human, since only a person
        // can say whether two setout lines legitimately meet here or a pile
        // is off its line.
        var bend = Assert.Single(issues, i => i.Category == InvestigationReconciliation.ManualReviewCategory);
        Assert.Equal(3, bend.ElementId);
        Assert.Contains("changes direction", bend.Description);
    }

    /// <summary>
    /// The other half of the same defect, and the more dangerous one: a
    /// line through exactly two points fits with zero residual, so the old
    /// endpoint-to-endpoint bearing could never detect an interior pile
    /// sitting off the line - it reported such a chain clean. Here the
    /// middle pile is 50mm off a due-north run and the printed call says
    /// due north, which the old measurement matched exactly.
    /// </summary>
    [Fact]
    public void A_pile_sitting_off_its_line_is_no_longer_reported_clean()
    {
        var p1 = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var p2 = RevitCheckTestBuilders.Pile(2, "P2", 50, 1000);   // 50mm off the line
        var p3 = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);

        var dims = new[] { Edge(101, p1, p2, 201, 202), Edge(102, p2, p3, 203, 204) };
        var note = RevitCheckTestBuilders.TextNote(301, 1, "0° 00' 00\"", RevitCheckTestBuilders.Pt(100, 1000));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { p1, p2, p3 }, dimensions: dims, textNotes: new[] { note });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        Assert.NotEmpty(issues);
        var bend = Assert.Single(issues, i => i.Category == InvestigationReconciliation.ManualReviewCategory);
        Assert.Equal(2, bend.ElementId);
    }

    /// <summary>A genuinely straight multi-pile chain must still come back completely clean - the fix must not turn ordinary real chains into bend findings.</summary>
    [Fact]
    public void A_genuinely_straight_multi_pile_chain_reports_no_bend()
    {
        var p1 = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var p2 = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var p3 = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);
        var p4 = RevitCheckTestBuilders.Pile(4, "P4", 0, 3000);

        var dims = new[] { Edge(101, p1, p2, 201, 202), Edge(102, p2, p3, 203, 204), Edge(103, p3, p4, 205, 206) };
        var note = RevitCheckTestBuilders.TextNote(301, 1, "0° 00' 00\"", RevitCheckTestBuilders.Pt(50, 1500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { p1, p2, p3, p4 }, dimensions: dims, textNotes: new[] { note });

        Assert.Empty(PileChainBearingConsistencyCheck.Run(model, new RuleConfig()));
    }

    /// <summary>A tag-to-tag dimension between two piles, tags sitting exactly on their own pile (the real placement confirmed 2026-08-26).</summary>
    private static Ir.DimensionInfo Edge(
        long dimensionId, Ir.ElementMetadata from, Ir.ElementMetadata to, long tagA, long tagB) =>
        RevitCheckTestBuilders.PileChainDimension(
            dimensionId, 1,
            RevitCheckTestBuilders.TagRef(tagA, RevitCheckTestBuilders.Pt(from.LocalPoint!.X, from.LocalPoint!.Y)),
            RevitCheckTestBuilders.TagRef(tagB, RevitCheckTestBuilders.Pt(to.LocalPoint!.X, to.LocalPoint!.Y)));
}
