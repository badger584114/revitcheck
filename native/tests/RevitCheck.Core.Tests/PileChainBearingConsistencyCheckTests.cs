using RevitCheck.Core.Issues;
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
    /// <summary>
    /// Findings that say something about the drawing, as opposed to the
    /// coverage note naming piles no run reached (added 2026-09-09). Tests
    /// whose subject is a verdict filter it out; tests about coverage
    /// assert on it directly.
    /// </summary>
    private static List<Issue> Verdicts(IEnumerable<Issue> issues) =>
        issues.Where(i => i.Category != "coverage").ToList();

    [Fact]
    public void Real_two_pile_chain_is_no_longer_given_a_verdict_at_all()
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

        var (issues, investigated) = PileChainBearingConsistencyCheck.RunWithScope(model, new RuleConfig());

        // Empty because it was SKIPPED, not because it was checked and
        // found clean - PileChainMinimumPiles rose to 3 on 2026-09-07 after
        // a two-pile run on model 100302 was given a confident verdict it
        // could not support. The empty investigated scope is what proves
        // the difference; asserting only on the issue list would let a
        // skipped run masquerade as a clean one.
        Assert.Empty(Verdicts(issues));
        Assert.Empty(investigated);
        // Skipped, and no longer silently: since 2026-09-09 a pile no run
        // covered is named, so "empty because skipped" can never again read
        // as "empty because clean" - which is how a real planted 50mm error
        // on pile 5506399 went unmentioned (PLANNING.md §23).
        var skipped = Assert.Single(issues);
        Assert.Equal("coverage", skipped.Category);
        Assert.Contains("not covered by any checked run", skipped.Description);
    }

    [Fact]
    public void Synthetic_clean_chain_with_a_matching_note_reports_nothing()
    {
        // Three piles, not two. This test read as "a clean chain reports
        // nothing" while actually asserting that a chain below the 3-pile
        // minimum is skipped - vacuous since §20 raised that minimum, and
        // only visible once a skipped chain stopped being silent
        // (2026-09-09). A genuinely clean chain reports nothing and leaves
        // no pile uncovered, which is what this now checks.
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var pileC = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);
        var dims = new[] { Edge(100, pileA, pileB, 200, 201), Edge(101, pileB, pileC, 202, 203) };
        // Due north (0deg) or due south (180deg, the reciprocal) - use a
        // note reading exactly "0° 00' 00"".
        var note = RevitCheckTestBuilders.TextNote(300, 1, "0° 00' 00\"", RevitCheckTestBuilders.Pt(50, 500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB, pileC }, dimensions: dims, textNotes: new[] { note });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void Chain_disagreeing_with_its_matched_note_is_flagged_high()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000); // due north, 0deg
        var pileC = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);
        var dims = new[] { Edge(100, pileA, pileB, 200, 201), Edge(101, pileB, pileC, 202, 203) };
        // Printed as 90 deg (due east) - a real 90 degree drafting/model
        // disagreement, not invented noise near the tolerance boundary.
        var note = RevitCheckTestBuilders.TextNote(300, 1, "90° 00' 00\"", RevitCheckTestBuilders.Pt(50, 500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB, pileC }, dimensions: dims, textNotes: new[] { note });

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
        var pileC = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);
        var dims = new[] { Edge(100, pileA, pileB, 200, 201), Edge(101, pileB, pileC, 202, 203) };
        // Real confirmed case, PLANNING.md §14: a real printed bearing
        // note that belongs to a chain outside this run's scope stayed
        // unmatched rather than being force-matched - this mirrors that,
        // a note far outside the default 10m cap.
        var farNote = RevitCheckTestBuilders.TextNote(300, 1, "0° 00' 00\"", RevitCheckTestBuilders.Pt(50_000, 500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB, pileC }, dimensions: dims, textNotes: new[] { farNote });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("no bearing call could be", issue.Description);
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

        // No verdict - but the two piles it declined to check are named.
        Assert.Empty(Verdicts(issues));
        var skipped = Assert.Single(issues);
        Assert.Equal("coverage", skipped.Category);
        Assert.Contains("1", skipped.Description);
    }

    [Fact]
    public void RunWithScope_reports_an_evaluated_chains_dimension_as_investigated_whether_clean_or_flagged()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000); // due north, 0deg
        var pileC = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);
        var dims = new[] { Edge(100, pileA, pileB, 200, 201), Edge(101, pileB, pileC, 202, 203) };
        // Printed 90deg - a real disagreement, so this chain gets flagged,
        // not clean. RunWithScope should still count its dimensions as
        // investigated - a verdict was reached, whether or not it passed.
        var note = RevitCheckTestBuilders.TextNote(300, 1, "90° 00' 00\"", RevitCheckTestBuilders.Pt(50, 500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB, pileC }, dimensions: dims, textNotes: new[] { note });

        var (issues, investigated) = PileChainBearingConsistencyCheck.RunWithScope(model, new RuleConfig());

        Assert.Single(issues);
        Assert.Equal(new[] { 100L, 101L }, investigated.OrderBy(x => x).ToArray());
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

        Assert.Empty(Verdicts(issues));
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

        var issue = Assert.Single(Verdicts(issues));
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

        var issue = Assert.Single(Verdicts(issues));
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

        Assert.Empty(Verdicts(issues));
        Assert.Empty(investigated);
    }

    [Fact]
    public void A_dimension_already_part_of_a_real_evaluated_chain_is_not_also_flagged_for_manual_review()
    {
        // Three piles, so the chain is actually evaluated - see
        // Synthetic_clean_chain_with_a_matching_note_reports_nothing.
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var pileC = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);
        var dims = new[] { Edge(100, pileA, pileB, 200, 201), Edge(101, pileB, pileC, 202, 203) };
        var note = RevitCheckTestBuilders.TextNote(300, 1, "0° 00' 00\"", RevitCheckTestBuilders.Pt(50, 500));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB, pileC }, dimensions: dims, textNotes: new[] { note });

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
    /// The real false positive, rebuilt 2026-09-07 from the real corner in
    /// model 100302: tag-to-tag dimensioning joined two setout lines
    /// meeting at 84.56° into one topologically simple 14-pile chain, and
    /// the old endpoint-to-endpoint bearing measured straight across the
    /// corner - landing 152,199 arcseconds from *both* legs. Each leg must
    /// be checked against its own call instead, with the corner reported
    /// for a human rather than averaged away.
    /// </summary>
    [Fact]
    public void Two_setout_lines_meeting_at_a_shared_pile_are_not_flagged_as_one_wrong_bearing()
    {
        // Leg A: piles 1→2→3 on 355°08'11" (that chain's own real mean).
        // Leg B: piles 3→4→5 on 270°34'52". Pile 3 is the shared corner.
        var p1 = RevitCheckTestBuilders.Pile(1, "P1", 0.0, 0.0);
        var p2 = RevitCheckTestBuilders.Pile(2, "P2", -254.35178378282274, 2989.198081440321);
        var p3 = RevitCheckTestBuilders.Pile(3, "P3", -508.70356756564547, 5978.396162880642);
        var p4 = RevitCheckTestBuilders.Pile(4, "P4", -3508.54919037101, 6008.830343262169);
        var p5 = RevitCheckTestBuilders.Pile(5, "P5", -6508.394813176374, 6039.264523643696);

        var dims = new[]
        {
            Edge(101, p1, p2, 201, 202),
            Edge(102, p2, p3, 203, 204),
            Edge(103, p3, p4, 205, 206),
            Edge(104, p4, p5, 207, 208),
        };

        var noteA = RevitCheckTestBuilders.TextNote(301, 1, "355° 08' 11\"", RevitCheckTestBuilders.Pt(100.0, 0.0));
        var noteB = RevitCheckTestBuilders.TextNote(302, 1, "270° 34' 53\"", RevitCheckTestBuilders.Pt(-6608.394813176374, 6039.264523643696));

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { p1, p2, p3, p4, p5 },
            dimensions: dims,
            textNotes: new[] { noteA, noteB });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        // The regression: neither leg disagrees with its own call, so
        // nothing here is a confirmed bearing problem.
        Assert.DoesNotContain(issues, i => i.Category == "geometry");

        var bend = Assert.Single(issues, i => i.Category == InvestigationReconciliation.ManualReviewCategory);
        Assert.Equal(3, bend.ElementId);
        Assert.Contains("changes direction", bend.Description);
    }

    /// <summary>
    /// The stated limit of the method, pinned so it cannot regress into a
    /// silent one: real pile placement scatter reaches 0.085° within a
    /// genuinely straight run, so a corner shallower than the 0.2°
    /// tolerance is below the noise and is deliberately not split. An
    /// earlier 60" tolerance, calibrated against the 6'25" separation
    /// between two adjacent setout lines on a different model, produced
    /// four false corners on the first real chain it met.
    /// </summary>
    [Fact]
    public void A_direction_change_below_real_placement_scatter_is_not_treated_as_a_corner()
    {
        // 6'25" apart - the real separation between two adjacent setout
        // lines on model 100304, and below this model's own scatter.
        var p1 = RevitCheckTestBuilders.Pile(1, "P1", 0.0, 0.0);
        var p2 = RevitCheckTestBuilders.Pile(2, "P2", 765.1278858813357, -2900.7894301804736);
        var p3 = RevitCheckTestBuilders.Pile(3, "P3", 1530.2557717626714, -5801.578860360947);
        var p4 = RevitCheckTestBuilders.Pile(4, "P4", 2300.796739915849, -8700.935102080402);

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { p1, p2, p3, p4 },
            dimensions: new[] { Edge(101, p1, p2, 201, 202), Edge(102, p2, p3, 203, 204), Edge(103, p3, p4, 205, 206) },
            textNotes: new[] { RevitCheckTestBuilders.TextNote(301, 1, "165° 10' 13\"", RevitCheckTestBuilders.Pt(100, 0)) });

        var issues = PileChainBearingConsistencyCheck.Run(model, new RuleConfig());

        Assert.DoesNotContain(issues, i => i.Category == InvestigationReconciliation.ManualReviewCategory);
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

    /// <summary>
    /// The real 2026-09-07 mis-assignment, built from the actual pile
    /// layout drawing. A three-pile spur runs west off the top of the
    /// Abutment A line, sharing its end pile with that line - and the
    /// line's own "175° 08' 40"" call is printed directly over the shared
    /// pile, so by distance alone the spur's nearest call is the main
    /// line's, at ~0mm. The spur was reported 84.6° wrong while its own
    /// call, "90° 35' 22"", sat a few metres away and agrees with the
    /// reconstruction to 6.6 arcseconds.
    ///
    /// Rotation is what tells them apart: a bearing call is drawn parallel
    /// to the line it describes.
    /// </summary>
    [Fact]
    public void A_spur_takes_its_own_parallel_bearing_call_not_the_main_lines_nearer_one()
    {
        var junction = RevitCheckTestBuilders.Pile(1, "PIL234301", 0.0, 0.0);
        var spurMid = RevitCheckTestBuilders.Pile(2, "PIL234342", -1147.9392505982264, 11.81003539293781);
        var spurEnd = RevitCheckTestBuilders.Pile(3, "PIL234341", -4902.740545020125, 50.43955011461157);

        var dims = new[] { Edge(101, junction, spurMid, 201, 202), Edge(102, spurMid, spurEnd, 203, 204) };

        // Printed over the shared junction pile, rotated to run along the
        // main line - nearest by distance, wrong by rotation.
        var mainLineCall = RevitCheckTestBuilders.TextNote(
            301, 1, "175° 08' 40\"", RevitCheckTestBuilders.Pt(0.0, 0.0), directionDegrees: 175.14444);
        // The spur's own call, further away but drawn along the spur.
        var spurCall = RevitCheckTestBuilders.TextNote(
            302, 1, "90° 35' 22\"", RevitCheckTestBuilders.Pt(-2500.0, 900.0), directionDegrees: 90.58944);

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { junction, spurMid, spurEnd },
            dimensions: dims,
            textNotes: new[] { mainLineCall, spurCall });

        // Matched to its own call and agreeing with it, so nothing is
        // reported at all - where distance alone produced a confident
        // 84.6-degree false positive.
        Assert.Empty(PileChainBearingConsistencyCheck.Run(model, new RuleConfig()));
    }

    /// <summary>
    /// Drafters eyeball the rotation, so the filter has to be generous
    /// (the user, 2026-09-07). A call set out 12° off its own line is
    /// still that line's call.
    /// </summary>
    [Fact]
    public void A_hand_placed_call_rotated_off_its_line_still_matches_it()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var pileC = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);
        var dims = new[] { Edge(100, pileA, pileB, 200, 201), Edge(101, pileB, pileC, 202, 203) };

        var note = RevitCheckTestBuilders.TextNote(
            300, 1, "0° 00' 00\"", RevitCheckTestBuilders.Pt(50, 1000), directionDegrees: 12.0);

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB, pileC }, dimensions: dims, textNotes: new[] { note });

        Assert.Empty(PileChainBearingConsistencyCheck.Run(model, new RuleConfig()));
    }

    /// <summary>
    /// Two calls equally near a run is the shared-end-pile case again, and
    /// picking either would be a guess. A coverage gap is the honest
    /// answer, not a confident verdict.
    /// </summary>
    [Fact]
    public void Two_equally_near_parallel_calls_are_refused_rather_than_guessed_between()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var pileC = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);
        var dims = new[] { Edge(100, pileA, pileB, 200, 201), Edge(101, pileB, pileC, 202, 203) };

        var a = RevitCheckTestBuilders.TextNote(300, 1, "0° 00' 00\"", RevitCheckTestBuilders.Pt(50, 1000), 0.0);
        var b = RevitCheckTestBuilders.TextNote(301, 1, "0° 30' 00\"", RevitCheckTestBuilders.Pt(-50, 1000), 0.0);

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB, pileC }, dimensions: dims, textNotes: new[] { a, b });

        var issue = Assert.Single(PileChainBearingConsistencyCheck.Run(model, new RuleConfig()));
        Assert.Equal("coverage", issue.Category);
    }
}
