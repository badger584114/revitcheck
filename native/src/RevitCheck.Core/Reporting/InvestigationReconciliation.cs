using System.Text.Json;
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
    /// The <see cref="Issue.RuleId"/> a human's own per-dimension verdict
    /// carries, distinct from any automated check's rule id - added
    /// 2026-08-31, real user feedback: there was no way at all to mark one
    /// specific dimension resolved or confirmed while manually checking it
    /// against the drawing, only a whole view via
    /// <see cref="CheckingSession.ResolveManually"/>. A person's verdict on
    /// one dimension is, functionally, just another investigation source -
    /// it goes through the exact same <see cref="CheckingSession.RecordInvestigation"/>
    /// path an automated check uses (accumulate into
    /// <see cref="ViewChecklistEntry.InvestigatedElementIds"/>, reconcile
    /// against triage), no separate mechanism needed. This constant exists
    /// so the audit trail is honest that a finding came from a person, not
    /// automation - CLAUDE.md's "a rule must say how it reached its
    /// conclusion" applies to a human's own judgement too.
    /// </summary>
    public const string ManualVerdictRuleId = "revitcheck.manual_verdict";

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
        StringValue(scope) == "view";

    /// <summary>
    /// Same real shape problem as <see cref="ElementIdList"/> (see its own
    /// remarks), for a string value instead of a list one - a round-tripped
    /// session's <c>"scope"</c> comes back as a <see cref="JsonElement"/>,
    /// not a <see cref="string"/>, so <c>scope as string</c> silently
    /// returned null and every resumed session's rollup issue read as an
    /// ordinary per-element issue instead - found by the same real Stage 4
    /// run, alongside the <see cref="ElementIdList"/> fix.
    /// </summary>
    private static string? StringValue(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null,
    };

    private static bool AllDraftedDimensionsResolved(Issue rollupIssue, ISet<long> investigatedSet)
    {
        var ids = ElementIdList(rollupIssue.SuggestedFix, "drafted_dimension_ids");
        // No ids recorded at all (a capture/issue predating the
        // 2026-08-26 SuggestedFix addition) - skip rather than guess
        // whether it's resolved; keep the rollup as-is.
        return ids.Count > 0 && ids.All(investigatedSet.Contains);
    }

    /// <summary>
    /// Reads a list of element ids out of an <see cref="Issue.SuggestedFix"/>
    /// entry - shared by <see cref="AllDraftedDimensionsResolved"/> (key
    /// <c>"drafted_dimension_ids"</c>) and <see cref="ExpandByElementIdList"/>
    /// (a caller-chosen key, e.g. <c>"dimension_element_ids"</c>). Three
    /// shapes handled, not assumed to be just one:
    /// </summary>
    /// <remarks>
    /// <b>The real, verified shape (fixed 2026-08-31, found on the real
    /// Revit machine during Stage 4):</b> a <c>Dictionary&lt;string, object?&gt;</c>
    /// deserialized by <c>System.Text.Json</c> (via <c>CheckingSessionSerializer.Load</c>,
    /// resuming a saved session) does NOT come back as boxed <c>long</c>/
    /// <c>List&lt;object&gt;</c> values the way this method originally
    /// assumed - .NET's default <c>object</c>-typed deserialization gives
    /// a boxed <see cref="JsonElement"/> instead, confirmed directly (a
    /// throwaway round-trip check, not guessed): <c>JsonElement</c> does
    /// NOT implement <see cref="System.Collections.IEnumerable"/> either,
    /// so the generic <c>IEnumerable</c> case below never matched it -
    /// every triage rollup's <c>drafted_dimension_ids</c> silently read as
    /// empty after a resume, meaning <see cref="AllDraftedDimensionsResolved"/>
    /// could never clear a rolled-up view's status again once its session
    /// had been saved and reloaded even once. The
    /// <c>List&lt;object&gt;</c> case below is kept anyway (harmless, and
    /// was already tested) in case some other in-process boxing path
    /// produces that shape, but it is not what a real round trip actually
    /// produces - <see cref="JsonElement"/> is.
    /// </remarks>
    private static List<long> ElementIdList(Dictionary<string, object?>? fix, string key)
    {
        if (fix is null || !fix.TryGetValue(key, out var raw))
        {
            return new List<long>();
        }

        switch (raw)
        {
            case List<long> longs:
                return longs;
            case JsonElement { ValueKind: JsonValueKind.Array } element:
            {
                var result = new List<long>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var value))
                    {
                        result.Add(value);
                    }
                }

                return result;
            }

            case System.Collections.IEnumerable enumerable:
                var listResult = new List<long>();
                foreach (var item in enumerable)
                {
                    switch (item)
                    {
                        case long l:
                            listResult.Add(l);
                            break;
                        case int i:
                            listResult.Add(i);
                            break;
                        case double d:
                            listResult.Add((long)d);
                            break;
                    }
                }

                return listResult;
            default:
                return new List<long>();
        }
    }

    /// <summary>
    /// Expands one issue keyed on a "container" element (a pile, for
    /// <see cref="Checks.PileChainBearingConsistencyCheck"/>) into one copy
    /// per id in its <c>SuggestedFix[<paramref name="suggestedFixKey"/>]</c>
    /// list, each copy's <see cref="Issue.ElementId"/> overwritten to that
    /// id. The original element id is preserved under
    /// <c>SuggestedFix["source_element_id"]</c> for context; the original
    /// <see cref="Issue.UniqueId"/> is dropped on every expanded copy - it
    /// names the container element (a pile's GUID), which would misleadingly
    /// label a dimension once <see cref="Issue.ElementId"/> no longer does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists - a real correctness bug caught before any code
    /// was written (PLANNING.md §16, 2026-08-26).</b>
    /// <see cref="Reconcile"/> joins purely on <see cref="Issue.ElementId"/>
    /// matching a dimension id. <see cref="Checks.PileChainBearingConsistencyCheck"/>'s
    /// issues carry <c>ElementId = &lt;a pile&gt;</c>, with the dimension
    /// ids that built the chain only inside
    /// <c>SuggestedFix["dimension_element_ids"]</c>. Fed straight into
    /// <see cref="Reconcile"/> as <c>investigationIssues</c>, a
    /// <b>flagged</b> chain's dimensions would each resolve to "investigated
    /// (their id is in <c>investigatedElementIds</c>), not itself in the
    /// problem list (the problem is filed under the pile's id, not
    /// theirs)" - i.e. silently reconciled as <em>clean</em>, dropping a
    /// real triage finding. Calling this method first, before ever handing
    /// a check's output to <see cref="Reconcile"/>, is what closes that
    /// gap: after expansion, the flagged chain's own problem issue is
    /// already filed under each affected dimension's id, so it lands in
    /// <see cref="ReconciliationResult.ConfirmedProblems"/> instead.
    /// </para>
    /// <para>
    /// <b>An issue with nothing at that key is passed through unchanged,
    /// not dropped.</b> Not every issue a check emits carries this
    /// linkage - a coverage note (e.g. "no piles found") has no element
    /// list to expand against, and dropping it would violate CLAUDE.md's
    /// "report a coverage indicator, never fail silently".
    /// </para>
    /// <para>
    /// <b>Known gap:</b> the expanded copies inherit the original issue's
    /// <see cref="Issue.ViewId"/>/<see cref="Issue.ViewName"/>/
    /// <see cref="Issue.SheetNo"/> verbatim - null for a whole-model check
    /// like <c>PileChainBearingConsistencyCheck</c>, which never set them in
    /// the first place. <see cref="Reconcile"/> itself never reads those
    /// fields, so this doesn't affect reconciliation - but it does mean a
    /// caller wiring this into a per-view command (PLANNING.md §16 Stage 3)
    /// should patch them in afterward from its own known active-view
    /// context if it wants the exported Issue to show which sheet it's on.
    /// </para>
    /// </remarks>
    public static List<Issue> ExpandByElementIdList(IEnumerable<Issue> issues, string suggestedFixKey)
    {
        var expanded = new List<Issue>();
        foreach (var issue in issues)
        {
            var ids = ElementIdList(issue.SuggestedFix, suggestedFixKey);
            if (ids.Count == 0)
            {
                expanded.Add(issue);
                continue;
            }

            foreach (var elementId in ids)
            {
                var fix = issue.SuggestedFix is { } original
                    ? new Dictionary<string, object?>(original)
                    : new Dictionary<string, object?>();
                fix["source_element_id"] = issue.ElementId;

                expanded.Add(new Issue
                {
                    RuleId = issue.RuleId,
                    Category = issue.Category,
                    Description = issue.Description,
                    Severity = issue.Severity,
                    ElementId = elementId,
                    ViewId = issue.ViewId,
                    ViewName = issue.ViewName,
                    SheetNo = issue.SheetNo,
                    SuggestedFix = fix,
                });
            }
        }

        return expanded;
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
