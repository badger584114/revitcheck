using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Reporting;

/// <summary>
/// Prunes dimension-level triage findings (<c>revit.dimension_provenance</c>/
/// <c>revit.dimension_override_consistency</c>) against a per-dimension
/// investigation check's verdicts, so BCF export only ever carries
/// confirmed problems - not the ~250-ish raw triage candidates a real run
/// produces. This is stage 3 of the pipeline named in PLANNING.md §14
/// (2026-08-26, "Product-shape correction"): triage (stage 1, built) →
/// investigation (stage 2, per-element-type verdict checks) →
/// reconciliation + export (this class).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why "investigated element ids" is a separate parameter from
/// "investigation issues", not derived from it.</b> Every check in this
/// codebase reports a problem by emitting an <see cref="Issue"/> and
/// reports "nothing wrong here" by emitting nothing (see
/// <c>MetadataReconciliationCheck</c>, <c>PileModelScheduleConsistencyCheck</c>).
/// That means an investigation check's own issue list can never
/// distinguish "checked this dimension, found it clean" from "never looked
/// at this dimension at all" - both look identical (absent). Treating
/// "not in the issue list" as "confirmed clean" would risk silently
/// suppressing a triage finding nothing has actually verified, which
/// directly violates CLAUDE.md's "report a coverage indicator, never fail
/// silently" - the exact discipline this class exists to uphold, not
/// relax. So a caller must supply the investigation check's full
/// examined-dimension scope separately from its issues; only an id present
/// in that scope, and absent from the issues, is treated as confirmed
/// clean. An id absent from the scope entirely is left alone - still open,
/// still visible, exactly as it was before investigation existed.
/// </para>
/// <para>
/// <b>Three outcomes, not two - added 2026-08-26 per the user's own
/// direction.</b> Some dimensions genuinely need drawing interpretation a
/// script can't do (an ambiguous nearest-pile match, a tag too far from
/// any real pile to trust, two candidates nearly equidistant) - an
/// investigation check that forced those into "clean" or "confirmed
/// problem" would be guessing, which this project's whole history says
/// not to do (CLAUDE.md: "skip rather than guess"). So an investigation
/// check gets a third option: emit an <see cref="Issue"/> with
/// <see cref="ManualReviewCategory"/> for a dimension it examined but
/// couldn't reach a confident automated verdict on. This class then
/// splits its output into three lists instead of one flat one -
/// <see cref="ReconciliationResult.ConfirmedProblems"/> (the only one
/// meant for automatic BCF export), <see cref="ReconciliationResult.NeedsManualReview"/>
/// (investigated, inconclusive - richer, check-specific detail than the
/// original triage flag had, but still needs a human to decide whether it
/// becomes a BCF issue), and <see cref="ReconciliationResult.StillOpenTriage"/>
/// (nothing has investigated this one at all yet - today's status quo,
/// unchanged). The goal named directly by the user: the bulk of triage
/// resolves itself automatically, and what's left is a short, genuinely
/// useful manual-review list, not a flood of everything unresolved.
/// </para>
/// <para>
/// <b>View-rollup triage findings are "un-rolled" before reconciling.</b>
/// Most real triage volume is <c>DimensionProvenanceCheck</c>'s own
/// "wholly-drafted view is one finding, not twenty" rollup
/// (<c>ViewRollupIssue</c>), anchored on the view's own ElementId, which
/// never matches a per-dimension investigation verdict directly. Since
/// 2026-08-26 that rollup's <c>SuggestedFix</c> carries
/// <c>drafted_dimension_ids</c> - the individual dimension ids it
/// summarizes - specifically so this class can check each of them
/// individually. A rollup issue is only dropped when <em>every one</em> of
/// its underlying dimensions has been examined - clean, confirmed
/// problem, <em>or</em> flagged for manual review all count as "examined"
/// here, since the point is whether investigation looked at it, not what
/// it concluded. If even one is still uninvestigated, the rollup stays
/// exactly as it was, since "verify this view against the model" remains
/// a true, honest statement until every dimension in it actually has been.
/// Any of its underlying dimensions that turned out to be problems or
/// need manual review still surface via the appropriate output list
/// either way.
/// </para>
/// <para>
/// <b>Deliberately does NOT reconcile <c>revitcheck.pile_model_schedule_consistency</c>
/// against anything.</b> That check is keyed on pile ElementIds, not
/// dimension ElementIds - it never examined a dimension in the first
/// place, so there is nothing dimension-shaped for this class to prune it
/// against. It stands alone: once wired to its own command, its own
/// findings are already verdicts and should export to BCF directly
/// (<c>writeBcf: true</c>, the same default <c>MetadataReconciliationCommand</c>
/// already uses), not be routed through this reconciliation step. The
/// generic ElementId-based join here naturally produces zero overlap with
/// it - no special-casing needed, it simply never matches.
/// </para>
/// <para>
/// <b>Not wired to any command yet, as of 2026-08-26.</b> The one
/// investigation check that would actually feed this - matching a
/// drafted pile dimension's own stated value against the measured
/// distance between its two nearest real piles - exists only as
/// diagnostic-script exploration
/// (<c>InspectDimensionGeometry.pushbutton</c>'s <c>pile_match</c>
/// addition) awaiting its first real Revit-machine run, not yet a real
/// Core check. This class is built and tested ahead of that on purpose -
/// the reconciliation mechanism itself is a generic, mechanical operation
/// whose correctness doesn't depend on real client data (unlike a
/// convention-specific extractor), so there's nothing to gain by waiting,
/// the same reasoning that let <c>IssueGrouping</c>/<c>IssueCsvWriter</c>
/// get built and tested ahead of the checks that would eventually feed
/// them.
/// </para>
/// </remarks>
public static class InvestigationReconciliation
{
    /// <summary>
    /// The <see cref="Issue.Category"/> value an investigation check uses
    /// to mark "I examined this dimension but can't give a confident
    /// automated verdict - a human needs to look at the drawing." Any
    /// other category on an investigation issue is treated as a confirmed
    /// problem. A recognized constant rather than a magic string, so a
    /// future check can reference it directly instead of risking a typo
    /// that would silently misroute a finding into the wrong output list.
    /// </summary>
    public const string ManualReviewCategory = "manual_review";

    /// <summary>
    /// Reconciles <paramref name="triageIssues"/> against one investigation
    /// check's results. <paramref name="investigatedElementIds"/> is every
    /// dimension ElementId that investigation check actually examined,
    /// regardless of outcome; <paramref name="investigationIssues"/> is
    /// whatever it flagged - each one categorized as a confirmed problem
    /// unless it carries <see cref="ManualReviewCategory"/>. See this
    /// class's own remarks for the full reasoning behind the three-way
    /// split this returns.
    /// </summary>
    public static ReconciliationResult Reconcile(
        IEnumerable<Issue> triageIssues,
        IReadOnlyCollection<long> investigatedElementIds,
        IEnumerable<Issue> investigationIssues)
    {
        var investigationList = investigationIssues as IReadOnlyList<Issue> ?? investigationIssues.ToList();
        var investigatedSet = investigatedElementIds as ISet<long> ?? new HashSet<long>(investigatedElementIds);

        var confirmedProblems = investigationList.Where(i => i.Category != ManualReviewCategory).ToList();
        var needsManualReview = investigationList.Where(i => i.Category == ManualReviewCategory).ToList();

        var problemIds = new HashSet<long>(
            confirmedProblems.Where(i => i.ElementId is not null).Select(i => i.ElementId!.Value));
        var manualReviewIds = new HashSet<long>(
            needsManualReview.Where(i => i.ElementId is not null).Select(i => i.ElementId!.Value));

        var stillOpen = new List<Issue>();
        foreach (var issue in triageIssues)
        {
            if (IsRolledUpDraftedView(issue))
            {
                if (AllDraftedDimensionsResolved(issue, investigatedSet))
                {
                    // Every dimension this rollup summarized has been
                    // examined now (clean, problem, or manual review) - the
                    // rollup's own "verify this against the model"
                    // statement has been honoured, so it's superseded.
                    // Whatever each individual dimension turned out to be
                    // already made it into confirmedProblems/needsManualReview
                    // separately.
                    continue;
                }

                // At least one dimension in this view is still
                // uninvestigated - the rollup stays exactly as it was,
                // not partially rewritten to reflect a partial result.
                stillOpen.Add(issue);
                continue;
            }

            if (issue.ElementId is not { } elementId)
            {
                // No single element to reconcile against (a model-wide
                // coverage note, for instance) - never suppressed.
                stillOpen.Add(issue);
                continue;
            }

            if (problemIds.Contains(elementId) || manualReviewIds.Contains(elementId))
            {
                // Superseded, not duplicated - the investigation check's
                // own, more specific finding for this exact dimension is
                // what should reach the reader, not the vague triage flag
                // sitting alongside it. Which list it lands in is decided
                // above by the investigation issue's own category.
                continue;
            }

            if (investigatedSet.Contains(elementId))
            {
                // Investigated and found clean - resolving exactly this
                // uncertainty is the whole reason investigation exists.
                continue;
            }

            // Nothing has investigated this one yet - stays open and
            // visible, never silently dropped.
            stillOpen.Add(issue);
        }

        return new ReconciliationResult
        {
            ConfirmedProblems = IssueSorting.SortIssues(confirmedProblems),
            NeedsManualReview = IssueSorting.SortIssues(needsManualReview),
            StillOpenTriage = IssueSorting.SortIssues(stillOpen),
        };
    }

    private static bool IsRolledUpDraftedView(Issue issue) =>
        issue.RuleId == "revit.dimension_provenance" &&
        issue.SuggestedFix is { } fix &&
        fix.TryGetValue("scope", out var scope) &&
        (scope as string) == "view";

    private static bool AllDraftedDimensionsResolved(Issue rollupIssue, ISet<long> investigatedSet)
    {
        var ids = DraftedDimensionIds(rollupIssue);
        // No ids recorded at all (a capture/issue predating the
        // 2026-08-26 SuggestedFix addition) - skip rather than guess
        // whether it's resolved; keep the rollup as-is.
        return ids.Count > 0 && ids.All(investigatedSet.Contains);
    }

    private static List<long> DraftedDimensionIds(Issue issue)
    {
        if (issue.SuggestedFix is not { } fix || !fix.TryGetValue("drafted_dimension_ids", out var raw))
        {
            return new List<long>();
        }

        // Comes straight off DimensionProvenanceCheck's own
        // List<long> when reconciling in-process; comes back as a
        // List<object> of boxed numeric JSON values when reconciling
        // against a capture round-tripped through System.Text.Json - both
        // handled rather than assumed to be one shape or the other.
        switch (raw)
        {
            case List<long> longs:
                return longs;
            case System.Collections.IEnumerable enumerable:
                var result = new List<long>();
                foreach (var item in enumerable)
                {
                    switch (item)
                    {
                        case long l:
                            result.Add(l);
                            break;
                        case int i:
                            result.Add(i);
                            break;
                        case double d:
                            result.Add((long)d);
                            break;
                    }
                }

                return result;
            default:
                return new List<long>();
        }
    }
}

/// <summary>
/// The three-way split <see cref="InvestigationReconciliation.Reconcile"/>
/// returns. Only <see cref="ConfirmedProblems"/> is meant for automatic
/// BCF export - see the class's own remarks for the full reasoning behind
/// keeping <see cref="NeedsManualReview"/> and <see cref="StillOpenTriage"/>
/// as separate lists rather than folding either into it.
/// </summary>
public sealed class ReconciliationResult
{
    /// <summary>Investigated, and a specific problem was confirmed. The only list meant for automatic BCF export.</summary>
    public List<Issue> ConfirmedProblems { get; init; } = new();

    /// <summary>Investigated, but the check couldn't reach a confident automated verdict - needs drawing interpretation. Not auto-exported; a human decides whether each one becomes a BCF issue.</summary>
    public List<Issue> NeedsManualReview { get; init; } = new();

    /// <summary>Nothing has investigated this yet - the original triage finding, unchanged.</summary>
    public List<Issue> StillOpenTriage { get; init; } = new();
}
