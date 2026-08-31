using RevitCheck.Core.Checks;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;
using Xunit;

namespace RevitCheck.Core.Tests;

public class InvestigationReconciliationTests
{
    private static Issue PerDimensionTriage(long elementId) => new()
    {
        RuleId = DimensionProvenanceCheck.RuleId,
        Category = "geometry",
        Severity = "high",
        ElementId = elementId,
        Description = $"Dimension {elementId} is drafted.",
        SuggestedFix = new Dictionary<string, object?> { ["provenance"] = "drafted", ["scope"] = "dimension" },
    };

    private static Issue RollupTriage(long viewElementId, IEnumerable<long>? draftedDimensionIds = null) => new()
    {
        RuleId = DimensionProvenanceCheck.RuleId,
        Category = "geometry",
        Severity = "high",
        ElementId = viewElementId,
        Description = $"Every dimension in view {viewElementId} is drafted.",
        SuggestedFix = draftedDimensionIds is null
            ? new Dictionary<string, object?> { ["provenance"] = "drafted", ["scope"] = "view" }
            : new Dictionary<string, object?>
            {
                ["provenance"] = "drafted",
                ["scope"] = "view",
                ["drafted_dimension_ids"] = draftedDimensionIds.ToList(),
            },
    };

    private static Issue InvestigationProblem(long dimensionElementId) => new()
    {
        RuleId = "revitcheck.pile_dimension_geometry_consistency",
        Category = "geometry",
        Severity = "high",
        ElementId = dimensionElementId,
        Description = $"Dimension {dimensionElementId} disagrees with the measured pile-to-pile distance.",
    };

    private static Issue InvestigationManualReview(long dimensionElementId, string why) => new()
    {
        RuleId = "revitcheck.pile_dimension_geometry_consistency",
        Category = InvestigationReconciliation.ManualReviewCategory,
        Severity = "medium",
        ElementId = dimensionElementId,
        Description = $"Dimension {dimensionElementId}: {why}",
    };

    private static Issue CoverageNote() => new()
    {
        RuleId = DimensionProvenanceCheck.RuleId,
        Category = "coverage",
        Severity = "low",
        Description = "3 dimension(s) could not be classified.",
    };

    [Fact]
    public void Investigated_and_clean_dimension_is_dropped_from_every_list()
    {
        var triage = new[] { PerDimensionTriage(1) };

        var result = InvestigationReconciliation.Reconcile(triage, investigatedElementIds: new long[] { 1 }, investigationIssues: Array.Empty<Issue>());

        Assert.Empty(result.ConfirmedProblems);
        Assert.Empty(result.NeedsManualReview);
        Assert.Empty(result.StillOpenTriage);
    }

    [Fact]
    public void Investigated_and_confirmed_problem_replaces_the_triage_finding()
    {
        var triage = new[] { PerDimensionTriage(1) };
        var investigationIssues = new[] { InvestigationProblem(1) };

        var result = InvestigationReconciliation.Reconcile(triage, investigatedElementIds: new long[] { 1 }, investigationIssues);

        var issue = Assert.Single(result.ConfirmedProblems);
        Assert.Equal("revitcheck.pile_dimension_geometry_consistency", issue.RuleId);
        Assert.Contains("measured pile-to-pile distance", issue.Description);
        Assert.Empty(result.NeedsManualReview);
        Assert.Empty(result.StillOpenTriage);
    }

    [Fact]
    public void Investigated_but_inconclusive_goes_to_manual_review_not_confirmed_problems()
    {
        var triage = new[] { PerDimensionTriage(1) };
        var investigationIssues = new[] { InvestigationManualReview(1, "two candidate piles are nearly equidistant") };

        var result = InvestigationReconciliation.Reconcile(triage, investigatedElementIds: new long[] { 1 }, investigationIssues);

        Assert.Empty(result.ConfirmedProblems);
        var issue = Assert.Single(result.NeedsManualReview);
        Assert.Contains("nearly equidistant", issue.Description);
        Assert.Empty(result.StillOpenTriage);
    }

    [Fact]
    public void Not_investigated_dimension_stays_open()
    {
        var triage = new[] { PerDimensionTriage(1) };

        var result = InvestigationReconciliation.Reconcile(triage, investigatedElementIds: Array.Empty<long>(), investigationIssues: Array.Empty<Issue>());

        var issue = Assert.Single(result.StillOpenTriage);
        Assert.Equal(DimensionProvenanceCheck.RuleId, issue.RuleId);
        Assert.Equal(1, issue.ElementId);
        Assert.Empty(result.ConfirmedProblems);
        Assert.Empty(result.NeedsManualReview);
    }

    [Fact]
    public void Coverage_note_with_no_element_id_is_never_suppressed()
    {
        var triage = new[] { CoverageNote() };

        var result = InvestigationReconciliation.Reconcile(triage, investigatedElementIds: new long[] { 1, 2, 3 }, investigationIssues: Array.Empty<Issue>());

        Assert.Single(result.StillOpenTriage);
    }

    [Fact]
    public void Rollup_issue_is_dropped_only_once_every_underlying_dimension_has_been_examined()
    {
        var rollup = RollupTriage(100, draftedDimensionIds: new long[] { 1, 2, 3 });

        // Only 2 of 3 investigated - rollup must stay exactly as-is.
        var partial = InvestigationReconciliation.Reconcile(
            new[] { rollup }, investigatedElementIds: new long[] { 1, 2 }, investigationIssues: Array.Empty<Issue>());
        var partialIssue = Assert.Single(partial.StillOpenTriage);
        Assert.Equal(100, partialIssue.ElementId);

        // All 3 examined now (2 clean, 1 a confirmed problem) - rollup is
        // superseded, replaced by the one specific finding.
        var complete = InvestigationReconciliation.Reconcile(
            new[] { rollup }, investigatedElementIds: new long[] { 1, 2, 3 }, investigationIssues: new[] { InvestigationProblem(3) });
        Assert.Empty(complete.StillOpenTriage);
        var completeIssue = Assert.Single(complete.ConfirmedProblems);
        Assert.Equal(3, completeIssue.ElementId);
        Assert.Contains("pile-to-pile", completeIssue.Description);
    }

    [Fact]
    public void Rollup_issue_resolved_by_a_mix_of_all_three_outcomes_splits_correctly()
    {
        var rollup = RollupTriage(100, draftedDimensionIds: new long[] { 1, 2, 3 });
        var investigationIssues = new[]
        {
            InvestigationProblem(2),
            InvestigationManualReview(3, "tag sits far from any real pile - may be leader-offset"),
        };

        // 1: clean, 2: confirmed problem, 3: manual review - all three
        // examined, so the rollup is fully resolved and superseded.
        var result = InvestigationReconciliation.Reconcile(
            new[] { rollup }, investigatedElementIds: new long[] { 1, 2, 3 }, investigationIssues);

        Assert.Empty(result.StillOpenTriage);
        Assert.Single(result.ConfirmedProblems);
        Assert.Equal(2, result.ConfirmedProblems[0].ElementId);
        Assert.Single(result.NeedsManualReview);
        Assert.Equal(3, result.NeedsManualReview[0].ElementId);
    }

    [Fact]
    public void Rollup_issue_with_all_dimensions_clean_is_dropped_with_nothing_left_behind()
    {
        var rollup = RollupTriage(100, draftedDimensionIds: new long[] { 1, 2 });

        var result = InvestigationReconciliation.Reconcile(
            new[] { rollup }, investigatedElementIds: new long[] { 1, 2 }, investigationIssues: Array.Empty<Issue>());

        Assert.Empty(result.StillOpenTriage);
        Assert.Empty(result.ConfirmedProblems);
        Assert.Empty(result.NeedsManualReview);
    }

    [Fact]
    public void Rollup_issue_with_no_recorded_dimension_ids_is_left_alone_not_guessed_at()
    {
        // Simulates a capture/issue predating the drafted_dimension_ids
        // addition - skip rather than assume it's resolvable.
        var rollup = RollupTriage(100, draftedDimensionIds: null);

        var result = InvestigationReconciliation.Reconcile(
            new[] { rollup }, investigatedElementIds: new long[] { 1, 2, 3 }, investigationIssues: Array.Empty<Issue>());

        var issue = Assert.Single(result.StillOpenTriage);
        Assert.Equal(100, issue.ElementId);
    }

    [Fact]
    public void Boxed_object_list_shape_is_handled()
    {
        // Kept as a defensive case even though it's not what a real
        // System.Text.Json round trip actually produces (see the real
        // JsonElement-shaped test below, and ElementIdList's own remarks) -
        // some other in-process boxing path could still hand this shape in,
        // and there is no cost to handling it too.
        var rollup = new Issue
        {
            RuleId = DimensionProvenanceCheck.RuleId,
            Category = "geometry",
            ElementId = 100,
            Description = "rollup",
            SuggestedFix = new Dictionary<string, object?>
            {
                ["scope"] = "view",
                ["drafted_dimension_ids"] = new List<object> { 1L, 2L },
            },
        };

        var result = InvestigationReconciliation.Reconcile(
            new[] { rollup }, investigatedElementIds: new long[] { 1, 2 }, investigationIssues: Array.Empty<Issue>());

        Assert.Empty(result.StillOpenTriage);
    }

    [Fact]
    public void JsonElement_shape_from_a_real_round_tripped_session_is_handled()
    {
        // The real shape a resumed session's SuggestedFix actually comes
        // back as - found on the real Revit machine, 2026-08-31 (Stage 4):
        // System.Text.Json's default object-typed deserialization gives a
        // boxed JsonElement, not List<object>, and JsonElement doesn't
        // implement IEnumerable either - the case above alone silently
        // failed to resolve any rollup issue in a resumed session. This
        // test round-trips through the real serializer (not a hand-built
        // JsonElement) so it fails the same way the real bug did if the
        // fix regresses.
        var rollup = new Issue
        {
            RuleId = DimensionProvenanceCheck.RuleId,
            Category = "geometry",
            ElementId = 100,
            Description = "rollup",
            SuggestedFix = new Dictionary<string, object?>
            {
                ["scope"] = "view",
                ["drafted_dimension_ids"] = new List<long> { 1, 2 },
            },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(rollup);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Issue>(json)!;
        Assert.IsType<System.Text.Json.JsonElement>(roundTripped.SuggestedFix!["drafted_dimension_ids"]);

        var result = InvestigationReconciliation.Reconcile(
            new[] { roundTripped }, investigatedElementIds: new long[] { 1, 2 }, investigationIssues: Array.Empty<Issue>());

        Assert.Empty(result.StillOpenTriage);
    }

    [Fact]
    public void Multiple_dimensions_are_reconciled_independently_across_all_three_outcomes()
    {
        var triage = new[] { PerDimensionTriage(1), PerDimensionTriage(2), PerDimensionTriage(3), PerDimensionTriage(4) };
        var investigationIssues = new[]
        {
            InvestigationProblem(2),
            InvestigationManualReview(4, "ambiguous nearest pile"),
        };

        // 1: clean (dropped), 2: problem, 3: not investigated (kept open), 4: manual review
        var result = InvestigationReconciliation.Reconcile(triage, investigatedElementIds: new long[] { 1, 2, 4 }, investigationIssues);

        Assert.Single(result.ConfirmedProblems);
        Assert.Equal(2, result.ConfirmedProblems[0].ElementId);
        Assert.Single(result.NeedsManualReview);
        Assert.Equal(4, result.NeedsManualReview[0].ElementId);
        Assert.Single(result.StillOpenTriage);
        Assert.Equal(3, result.StillOpenTriage[0].ElementId);
    }

    [Fact]
    public void Reconcile_has_no_rule_id_awareness_caller_must_only_feed_it_dimension_scoped_investigation_issues()
    {
        // Reconcile joins purely on ElementId - it has no idea which check
        // produced investigationIssues. That's fine in practice because
        // PileModelScheduleConsistencyCheck is keyed on Pile ElementIds,
        // which never collide with a Dimension's, so passing its output
        // here would naturally never match anything real (see this class's
        // own remarks on why it "stands alone"). This test documents the
        // actual boundary: it's a caller discipline, not something this
        // method enforces - feeding it an element-id-keyed check whose ids
        // DO collide with dimension ids (as forced here, unrealistically,
        // via id 1 on both sides) supersedes triage exactly as designed.
        var triage = new[] { PerDimensionTriage(1) };
        var pileIssue = new Issue
        {
            RuleId = PileModelScheduleConsistencyCheck.RuleId,
            Category = "geometry",
            ElementId = 1,
            Description = "Pile 1 drifted from its schedule row.",
        };

        var result = InvestigationReconciliation.Reconcile(triage, investigatedElementIds: new long[] { 1 }, investigationIssues: new[] { pileIssue });

        var issue = Assert.Single(result.ConfirmedProblems);
        Assert.Equal(PileModelScheduleConsistencyCheck.RuleId, issue.RuleId);
    }

    private static Issue PileKeyedChainIssue(long pileElementId, string pileUniqueId, IEnumerable<long> dimensionElementIds) => new()
    {
        RuleId = PileChainBearingConsistencyCheck.RuleId,
        Category = "geometry",
        Severity = "high",
        ElementId = pileElementId,
        UniqueId = pileUniqueId,
        Description = $"Reconstructed a chain: real bearing disagrees with the drafted bearing call.",
        SuggestedFix = new Dictionary<string, object?>
        {
            ["pile_element_ids"] = new List<long> { pileElementId },
            ["dimension_element_ids"] = dimensionElementIds.ToList(),
        },
    };

    [Fact]
    public void ExpandByElementIdList_emits_one_copy_per_id_reassigning_element_id_and_dropping_unique_id()
    {
        var pileIssue = PileKeyedChainIssue(pileElementId: 500, pileUniqueId: "pile-guid-500", dimensionElementIds: new long[] { 10, 20 });

        var expanded = InvestigationReconciliation.ExpandByElementIdList(new[] { pileIssue }, "dimension_element_ids");

        Assert.Equal(2, expanded.Count);
        Assert.Equal(new long?[] { 10, 20 }, expanded.Select(i => i.ElementId));
        Assert.All(expanded, i => Assert.Null(i.UniqueId));
        Assert.All(expanded, i => Assert.Equal(PileChainBearingConsistencyCheck.RuleId, i.RuleId));
        Assert.All(expanded, i => Assert.Equal(500L, i.SuggestedFix!["source_element_id"]));
        // The rest of the original SuggestedFix survives the expansion too.
        Assert.Equal(new List<long> { 500 }, expanded[0].SuggestedFix!["pile_element_ids"]);
    }

    [Fact]
    public void ExpandByElementIdList_passes_through_an_issue_with_nothing_at_that_key_unchanged()
    {
        var coverageIssue = new Issue
        {
            RuleId = PileChainBearingConsistencyCheck.RuleId,
            Category = "coverage",
            Severity = "low",
            Description = "No captured elements have category 'Structural Foundations'.",
        };

        var expanded = InvestigationReconciliation.ExpandByElementIdList(new[] { coverageIssue }, "dimension_element_ids");

        var issue = Assert.Single(expanded);
        Assert.Same(coverageIssue, issue);
    }

    [Fact]
    public void ExpandByElementIdList_then_Reconcile_confirms_every_dimension_in_a_flagged_chain_regression_test()
    {
        // The exact regression PLANNING.md §16 designed ExpandByElementIdList
        // to prevent: PileChainBearingConsistencyCheck's issue is keyed on a
        // pile's ElementId (500), not either dimension's (10, 20). Feeding
        // it straight into Reconcile as investigationIssues would resolve
        // both dimensions as "investigated, not in the problem list" -
        // silently clean. Expanding it first is what makes them resolve as
        // confirmed problems instead.
        var pileIssue = PileKeyedChainIssue(pileElementId: 500, pileUniqueId: "pile-guid-500", dimensionElementIds: new long[] { 10, 20 });
        var triage = new[] { PerDimensionTriage(10), PerDimensionTriage(20) };

        // The bug, demonstrated directly: unexpanded, the pile issue's
        // ElementId (500) never matches either dimension's own triage id
        // (10, 20), so each dimension's own per-dimension triage finding
        // silently resolves as "investigated, found clean" - dropped from
        // every list, nothing keyed to dimension 10 or 20 anywhere in the
        // result, even though the chain they belong to is genuinely
        // flagged. (Reconcile does still surface the pile-keyed issue
        // itself in ConfirmedProblems - it's a real investigation finding -
        // but that alone doesn't tell a per-dimension reader which
        // dimensions it actually implicates.)
        var unexpanded = InvestigationReconciliation.Reconcile(
            triage, investigatedElementIds: new long[] { 10, 20 }, investigationIssues: new[] { pileIssue });
        Assert.Empty(unexpanded.StillOpenTriage);
        Assert.DoesNotContain(unexpanded.ConfirmedProblems, i => i.ElementId == 10 || i.ElementId == 20);

        // Expanded first, as every real caller must: both dimensions surface
        // as confirmed problems in their own right, not silently dropped.
        var expanded = InvestigationReconciliation.ExpandByElementIdList(new[] { pileIssue }, "dimension_element_ids");
        var result = InvestigationReconciliation.Reconcile(
            triage, investigatedElementIds: new long[] { 10, 20 }, investigationIssues: expanded);

        Assert.Equal(2, result.ConfirmedProblems.Count);
        Assert.Equal(new long?[] { 10, 20 }, result.ConfirmedProblems.Select(i => i.ElementId).OrderBy(id => id));
        Assert.Empty(result.StillOpenTriage);
    }
}
