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
/// Closing off dimension checking (2026-09-10). Triage says a drafted
/// dimension cannot be shown correct from the file; what it never said is
/// whether anything will ever be able to. On real model 100302, 54 of the
/// 133 dimensions triage puts in front of a reviewer can be reached by no
/// check at all, and sat in StillOpenTriage looking like work not yet done.
/// Numbers here are that model's own.
/// </summary>
public class DimensionResolutionTests
{
    private static DimensionInfo DetailLinework(long id, long viewId = 10) =>
        RevitCheckTestBuilders.Dimension(
            id, viewId,
            new[]
            {
                RevitCheckTestBuilders.DraftedRef(201),
                RevitCheckTestBuilders.DraftedRef(202),
            });

    private static DimensionInfo PileTagToTag(long id, long viewId = 10) =>
        RevitCheckTestBuilders.Dimension(
            id, viewId,
            new[]
            {
                RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 0)),
                RevitCheckTestBuilders.TagRef(202, RevitCheckTestBuilders.Pt(0, 1000)),
            });

    /// <summary>
    /// Named as the dimension check, not the bearing one. Both reconcile a
    /// triaged pile dimension, but only this one compares the dimension's
    /// own stated distance against the model - the bearing check uses it as
    /// chain topology and never reads its value.
    /// </summary>
    [Fact]
    public void A_pile_tag_to_tag_dimension_is_reachable_by_the_dimension_check()
    {
        var path = DimensionResolution.For(PileTagToTag(1), RevitCheckTestBuilders.View(10));

        Assert.True(path.Reachable);
        Assert.Equal(PileDimensionConsistencyCheck.RuleId, path.RuleId);
    }

    /// <summary>
    /// The 25 detail-linework dimensions on the real model. PLANNING.md §14
    /// went at this seven times: GlobalPoint null on all 17 references
    /// tested, Location returning (0,0,0) for real model geometry,
    /// Dimension.Curve throwing on 41 of 46, and the one reliable anchor
    /// being where the text sits - 527m from its own witness lines on a
    /// real dimension. This is a statement of reach, not of difficulty.
    /// </summary>
    [Fact]
    public void A_detail_linework_dimension_is_reachable_by_nothing_and_says_why()
    {
        var path = DimensionResolution.For(DetailLinework(1), RevitCheckTestBuilders.View(10));

        Assert.False(path.Reachable);
        Assert.Null(path.RuleId);
        Assert.Contains("witness points", path.Reason);
    }

    /// <summary>A drafting view has no model behind it - a harder no again, and a different reason.</summary>
    [Fact]
    public void A_dimension_in_a_drafting_view_is_unreachable_for_a_different_reason()
    {
        var path = DimensionResolution.For(
            DetailLinework(1),
            RevitCheckTestBuilders.View(10, viewType: "DraftingView"));

        Assert.False(path.Reachable);
        Assert.Contains("no model behind it", path.Reason);
    }

    /// <summary>
    /// The breakdown §18 asked for: a reviewer should be able to tell which
    /// button applies to a view without already knowing the answer.
    /// </summary>
    [Fact]
    public void Describe_tallies_a_view_by_what_can_reach_it()
    {
        var view = RevitCheckTestBuilders.View(10);
        var described = DimensionResolution.Describe(
            new[] { PileTagToTag(1), PileTagToTag(2), DetailLinework(3) }, view);

        Assert.Contains("2 pile tag-to-tag", described);
        Assert.Contains("1 unreachable", described);
    }

    private static Issue Triage(long elementId, DimensionInfo dimension, ViewInfo view)
    {
        var issues = DimensionProvenanceCheck.Run(
            RevitCheckTestBuilders.Model(views: new[] { view }, dimensions: new[] { dimension }),
            new RuleConfig());

        return Assert.Single(issues, i => i.ElementId == elementId);
    }

    /// <summary>
    /// The behaviour that actually closes the workflow: a dimension nothing
    /// can reach is a reviewer's by construction, so it reports as needing a
    /// person rather than sitting open for a tool that is not coming. It
    /// counts as examined for exactly the reason a reviewer's own "Needs
    /// Manual Review" click already does - it is the same outcome, reached
    /// without making them click it 54 times.
    /// </summary>
    [Fact]
    public void An_unreachable_triaged_dimension_needs_a_person_rather_than_staying_open()
    {
        var view = RevitCheckTestBuilders.View(10);
        var triage = Triage(1, DetailLinework(1), view);

        var result = InvestigationReconciliation.Reconcile(
            new[] { triage }, new long[0], new List<Issue>());

        Assert.Empty(result.StillOpenTriage);
        Assert.Equal(0, result.OpenDimensionCount);
        var manual = Assert.Single(result.NeedsManualReview);
        Assert.Equal(InvestigationReconciliation.ManualReviewCategory, manual.Category);
        Assert.Contains("No automated check can settle this", manual.Description);
    }

    /// <summary>
    /// And the converse, which is what stops this becoming a way to make
    /// real work disappear: a dimension a tool CAN reach stays open until
    /// something actually reaches it.
    /// </summary>
    [Fact]
    public void A_reachable_triaged_dimension_still_stays_open_until_investigated()
    {
        var view = RevitCheckTestBuilders.View(10);
        var triage = Triage(1, PileTagToTag(1), view);

        var result = InvestigationReconciliation.Reconcile(
            new[] { triage }, new long[0], new List<Issue>());

        Assert.Single(result.StillOpenTriage);
        Assert.Empty(result.NeedsManualReview);
        Assert.Equal(1, result.OpenDimensionCount);
    }

    /// <summary>
    /// A triage issue from before the marker existed carries neither key.
    /// It must stay open - failing towards "still outstanding" is the safe
    /// direction, since the opposite quietly moves real work into a bucket
    /// nobody is expected to act on.
    /// </summary>
    [Fact]
    public void A_triage_issue_with_no_resolution_marker_stays_open()
    {
        var issue = new Issue
        {
            RuleId = DimensionProvenanceCheck.RuleId,
            Category = "geometry",
            Severity = "high",
            ElementId = 1,
            Description = "Dimension 1 is drafted.",
            SuggestedFix = new Dictionary<string, object?> { ["provenance"] = "drafted" },
        };

        var result = InvestigationReconciliation.Reconcile(
            new[] { issue }, new long[0], new List<Issue>());

        Assert.Single(result.StillOpenTriage);
        Assert.Empty(result.NeedsManualReview);
    }

    /// <summary>
    /// Real shape from 'Datum 0 K.S' (7 of 7 unreachable) and
    /// 'DRG-2871008 - SECTION 1' (6 of 6): a view whose every drafted
    /// dimension is out of reach could never clear no matter what was run.
    /// The rollup now settles once everything reachable has been examined -
    /// which, here, is nothing.
    /// </summary>
    [Fact]
    public void A_rollup_of_only_unreachable_dimensions_does_not_nag_forever()
    {
        var view = RevitCheckTestBuilders.View(10, name: "Datum 0 K.S", viewType: "EngineeringPlan");
        var dimensions = Enumerable.Range(1, 7).Select(i => DetailLinework(i)).ToList();

        var rollup = Assert.Single(
            DimensionProvenanceCheck.Run(
                RevitCheckTestBuilders.Model(views: new[] { view }, dimensions: dimensions),
                new RuleConfig()),
            i => (i.SuggestedFix?.TryGetValue("scope", out var scope) ?? false) && scope?.ToString() == "view");

        var result = InvestigationReconciliation.Reconcile(
            new[] { rollup }, new long[0], new List<Issue>());

        Assert.Empty(result.StillOpenTriage);
    }
}
