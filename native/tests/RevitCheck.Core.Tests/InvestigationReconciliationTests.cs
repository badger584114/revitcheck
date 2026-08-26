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
    public void Boxed_object_list_shape_from_a_round_tripped_capture_is_handled()
    {
        // A List<long> serialized through System.Text.Json and deserialized
        // generically comes back as a List<object> of boxed longs/doubles,
        // not the original List<long> - both shapes must resolve the same.
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
}
