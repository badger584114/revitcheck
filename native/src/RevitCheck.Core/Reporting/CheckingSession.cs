using RevitCheck.Core.Checks;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Reporting;

/// <summary>
/// A view's status in the interactive checking workflow (PLANNING.md §16).
/// </summary>
public enum ViewInvestigationStatus
{
    /// <summary>Triage found something here; nothing has investigated it yet.</summary>
    Pending,

    /// <summary>Investigated - every triage finding either resolved clean or was superseded by a confirmed-clean investigation result.</summary>
    Resolved,

    /// <summary>Investigated, but at least one finding couldn't reach a confident automated verdict - needs a human to read the drawing.</summary>
    NeedsManualReview,

    /// <summary>At least one <em>confirmed</em> problem exists here (from reconciliation, or from a non-dimension-linked investigation check like the pile schedule check) - the only status that blocks a manual dismissal from silently standing in for it.</summary>
    Flagged,

    /// <summary>A human judged this view out of scope for checking entirely (a diagrammatic/construction-sequence sheet) and dismissed it directly, not via any automated check. Kept distinct from <see cref="Resolved"/> - that means an automated check confirmed the model against the drawing; this means a human decided there was nothing to check in the first place. Never wins over <see cref="Flagged"/> - see <see cref="ViewChecklistEntry.Status"/>.</summary>
    ResolvedManually,
}

/// <summary>
/// One view's row in the checking-session checklist: its slice of the
/// combined dimension triage, everything an investigation check has found
/// for it so far, and (if a human dismissed it directly) why.
/// </summary>
public sealed class ViewChecklistEntry
{
    public required long ViewId { get; init; }
    public string? ViewName { get; init; }
    public string? SheetNo { get; init; }

    /// <summary>This view's slice of the triage issues <see cref="CheckingSession.Start"/> was given - reconciled against <see cref="InvestigationIssues"/> on every <see cref="CheckingSession.RecordInvestigation"/> call.</summary>
    public List<Issue> TriageIssues { get; init; } = new();

    /// <summary>Every dimension id an investigation check has examined for this view so far, accumulated across calls - see <see cref="InvestigationReconciliation"/>'s own remarks on why "examined" must be tracked separately from "flagged".</summary>
    public List<long> InvestigatedElementIds { get; init; } = new();

    /// <summary>Dimension-keyed investigation findings accumulated for this view (already post-<see cref="InvestigationReconciliation.ExpandByElementIdList"/>, e.g. from <c>revitcheck.pile_chain_bearing_consistency</c>) - reconciled against <see cref="TriageIssues"/> to produce <see cref="LastReconciliation"/>.</summary>
    public List<Issue> InvestigationIssues { get; init; } = new();

    /// <summary>
    /// Findings from an investigation check with no dimension linkage at all
    /// (e.g. <c>revitcheck.pile_model_schedule_consistency</c>, keyed on
    /// pile ElementIds - see <see cref="InvestigationReconciliation"/>'s own
    /// remarks on why that check "stands alone"). Never reconciled against
    /// triage - it never examined a dimension in the first place - but
    /// already a verdict, not a candidate, so it counts toward
    /// <see cref="Status"/> being <see cref="ViewInvestigationStatus.Flagged"/>
    /// and flows into <see cref="CheckingSession.ExportableConfirmedProblems"/>
    /// exactly like a reconciled confirmed problem does.
    /// </summary>
    public List<Issue> OtherInvestigationFindings { get; init; } = new();

    /// <summary>The result of reconciling <see cref="TriageIssues"/> against <see cref="InvestigationIssues"/>/<see cref="InvestigatedElementIds"/> as of the last <see cref="CheckingSession.RecordInvestigation"/> call - or, before any call, of reconciling against nothing (so everything is still open).</summary>
    public ReconciliationResult LastReconciliation { get; set; } = new();

    /// <summary>Null unless a human bulk-dismissed this view via <see cref="CheckingSession.ResolveManually"/>; the reason they gave (may be empty - a reason is encouraged at the UI layer, not enforced here).</summary>
    public string? ManualResolutionReason { get; set; }

    /// <summary>
    /// Derived from the fields above on every read, never tracked
    /// incrementally - see PLANNING.md §16 for the full reasoning.
    /// </summary>
    /// <remarks>
    /// Precedence: a confirmed problem (from reconciliation or from
    /// <see cref="OtherInvestigationFindings"/>) always wins, even over a
    /// manual dismissal - <see cref="ManualResolutionReason"/> being set
    /// does not, by itself, mean the view reads as
    /// <see cref="ViewInvestigationStatus.ResolvedManually"/>. This is
    /// deliberate: a blanket "this sheet type was never live setout"
    /// judgement is not the same act as evaluating one specific
    /// already-confirmed finding, and must never be allowed to quietly bury
    /// it. Below that, a manual dismissal suppresses the
    /// <see cref="ViewInvestigationStatus.Pending"/>/
    /// <see cref="ViewInvestigationStatus.NeedsManualReview"/> nagging it
    /// exists to short-circuit.
    /// </remarks>
    public ViewInvestigationStatus Status
    {
        get
        {
            if (LastReconciliation.ConfirmedProblems.Count > 0 || OtherInvestigationFindings.Count > 0)
            {
                return ViewInvestigationStatus.Flagged;
            }

            if (ManualResolutionReason is not null)
            {
                return ViewInvestigationStatus.ResolvedManually;
            }

            if (LastReconciliation.NeedsManualReview.Count > 0)
            {
                return ViewInvestigationStatus.NeedsManualReview;
            }

            if (LastReconciliation.StillOpenTriage.Count > 0)
            {
                return ViewInvestigationStatus.Pending;
            }

            return ViewInvestigationStatus.Resolved;
        }
    }
}

/// <summary>A manually-dismissed view, kept for the export-time audit trail - see <see cref="CheckingSession.ExportableManualResolutions"/>.</summary>
public sealed class ManualResolutionRecord
{
    public required long ViewId { get; init; }
    public string? ViewName { get; init; }
    public string? SheetNo { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Cross-command session state for the interactive checking workflow
/// (PLANNING.md §16): combined dimension triage grouped into one checklist
/// row per view, updated as investigation checks run against each view in
/// turn, exported as three separate audiences (confirmed problems for BCF,
/// manual-review items and manual dismissals for a human to read) rather
/// than one flat list. Pure Core, no Revit API - see
/// <see cref="CheckingSessionSerializer"/> for the JSON persistence that
/// lets a session survive a Revit restart.
/// </summary>
public sealed class CheckingSession
{
    /// <summary>The <see cref="RuleConfig"/> the triage run that built this session used - kept on the session (rather than re-supplied per call) so a resumed session and every investigation command run against it stay consistent with the same tolerances/category names, without each caller having to know to pass the same instance again.</summary>
    public RuleConfig Config { get; init; } = new();

    /// <summary>Triage issues with no <see cref="Issue.ViewId"/> at all (a model-wide coverage note, for instance) - never a checklist row, since there is no view to cycle to or investigate. Always open; surfaced via <see cref="ExportableStillOpenTriage"/>.</summary>
    public List<Issue> ModelWideNotes { get; init; } = new();

    public List<ViewChecklistEntry> Views { get; init; } = new();

    /// <summary>
    /// Groups <paramref name="triageIssues"/> by <see cref="Issue.ViewId"/>
    /// into one <see cref="ViewChecklistEntry"/> per view. Deliberately uses
    /// the triage issues' own <c>ViewId</c> rather than
    /// <c>DimensionProvenanceCheck.DraftedViews()</c>, which only returns
    /// <em>wholly</em>-drafted views and would miss the threshold-based
    /// rollup population and any mixed/unknown-verdict view entirely.
    /// </summary>
    public static CheckingSession Start(IEnumerable<Issue> triageIssues, RuleConfig config)
    {
        var triageList = triageIssues as IReadOnlyList<Issue> ?? triageIssues.ToList();
        var session = new CheckingSession { Config = config };

        foreach (var issue in triageList.Where(i => i.ViewId is null))
        {
            session.ModelWideNotes.Add(issue);
        }

        foreach (var group in triageList.Where(i => i.ViewId is not null).GroupBy(i => i.ViewId!.Value))
        {
            var first = group.First();
            var entry = new ViewChecklistEntry
            {
                ViewId = group.Key,
                ViewName = first.ViewName,
                SheetNo = first.SheetNo,
                TriageIssues = group.ToList(),
            };
            // Reconcile against nothing yet - equivalent to "not
            // investigated", so every per-dimension finding starts open and
            // every rollup finding stays exactly as triage reported it.
            entry.LastReconciliation = InvestigationReconciliation.Reconcile(
                entry.TriageIssues, investigatedElementIds: Array.Empty<long>(), investigationIssues: Array.Empty<Issue>());
            session.Views.Add(entry);
        }

        return session;
    }

    public ViewChecklistEntry? FindView(long viewId) => Views.FirstOrDefault(e => e.ViewId == viewId);

    /// <summary>
    /// Records one investigation-check run against one view and re-derives
    /// that row's reconciliation. Two shapes, selected by
    /// <paramref name="otherFindingsRuleId"/>:
    /// </summary>
    /// <remarks>
    /// <b>Dimension-linked</b> (<paramref name="otherFindingsRuleId"/> is
    /// null, e.g. <c>revitcheck.pile_chain_bearing_consistency</c> after
    /// <see cref="InvestigationReconciliation.ExpandByElementIdList"/>):
    /// <paramref name="investigatedElementIds"/>/<paramref name="investigationIssues"/>
    /// accumulate into the row, then get reconciled against
    /// <see cref="ViewChecklistEntry.TriageIssues"/> via
    /// <see cref="InvestigationReconciliation.Reconcile"/>.
    /// <para>
    /// <b>Not dimension-linked</b> (<paramref name="otherFindingsRuleId"/>
    /// set, e.g. <c>revitcheck.pile_model_schedule_consistency</c> - see
    /// <see cref="InvestigationReconciliation"/>'s own remarks on why that
    /// check "stands alone"): <paramref name="investigationIssues"/>
    /// replace (not accumulate onto) any prior findings from that same rule
    /// id in <see cref="ViewChecklistEntry.OtherInvestigationFindings"/> -
    /// replace, not accumulate, so a pile fixed since the last run doesn't
    /// leave a stale finding behind - and nothing is reconciled, since there
    /// is no dimension linkage to reconcile against.
    /// </para>
    /// A view id with no existing checklist row (triage found nothing to
    /// flag there) is a no-op, not an error - there is nothing to
    /// investigate results into.
    /// </remarks>
    /// <remarks>
    /// <b>Dimension-linked calls supersede, not just accumulate, for the
    /// exact ids in <paramref name="investigatedElementIds"/></b> - a real
    /// bug found on the Revit machine, 2026-08-31. Before this,
    /// <see cref="ViewChecklistEntry.InvestigationIssues"/> only ever grew,
    /// so a stale <c>manual_review</c> Issue an automated check recorded
    /// for one dimension would keep showing in
    /// <see cref="ReconciliationResult.NeedsManualReview"/> forever, even
    /// after a human selected that exact dimension and marked it Resolved
    /// (which correctly calls this method with an empty
    /// <paramref name="investigationIssues"/> for that id - "clean" has
    /// nothing to overwrite the old entry with, since nothing ever removed
    /// it). Any existing entry whose <see cref="Issue.ElementId"/> is in
    /// this call's <paramref name="investigatedElementIds"/> is removed
    /// before <paramref name="investigationIssues"/> is added - the latest
    /// verdict for a given dimension always wins, whether that verdict came
    /// from an automated check re-run or a human's own manual click.
    /// </remarks>
    /// <remarks>
    /// <b>An <see cref="Issue"/> with no <see cref="Issue.ElementId"/> is
    /// dropped from the dimension-linked path entirely</b> - a real bug
    /// found on the Revit machine, 2026-09-02
    /// (<c>SpotElevationConsistencyCommand</c>'s first real dual-mode
    /// run). CLAUDE.md's "report a coverage indicator, never fail
    /// silently" means an investigation check may reasonably append one
    /// whole-check summary Issue (no single element to anchor it to) even
    /// on a clean run, the way <c>SpotElevationConsistencyCheck.RunWithScope</c>
    /// does - but <see cref="InvestigationReconciliation.Reconcile"/>
    /// categorizes *any* issue not carrying
    /// <see cref="InvestigationReconciliation.ManualReviewCategory"/> as a
    /// confirmed problem, with no per-element key to make sense of, so a
    /// clean run's own coverage note was silently flipping the view to
    /// <see cref="ViewInvestigationStatus.Flagged"/>. Worse, the
    /// supersede fix directly above only matches issues that <i>have</i> an
    /// <see cref="Issue.ElementId"/>, so a null-element issue was never
    /// being cleaned up either - re-running the same check kept adding
    /// another one. A whole-check summary belongs in the standalone
    /// JSON/CSV/BCF output and the command's own on-screen summary text,
    /// not in a session's per-dimension reconciliation, which has no
    /// meaningful bucket for it - so it never reaches
    /// <see cref="ViewChecklistEntry.InvestigationIssues"/> at all.
    /// </remarks>
    public void RecordInvestigation(
        long viewId,
        IReadOnlyCollection<long> investigatedElementIds,
        IEnumerable<Issue> investigationIssues,
        string? otherFindingsRuleId = null)
    {
        var entry = FindView(viewId);
        if (entry is null)
        {
            return;
        }

        var issuesList = investigationIssues as IReadOnlyList<Issue> ?? investigationIssues.ToList();

        if (otherFindingsRuleId is not null)
        {
            entry.OtherInvestigationFindings.RemoveAll(i => i.RuleId == otherFindingsRuleId);
            entry.OtherInvestigationFindings.AddRange(issuesList);
            return;
        }

        foreach (var id in investigatedElementIds)
        {
            if (!entry.InvestigatedElementIds.Contains(id))
            {
                entry.InvestigatedElementIds.Add(id);
            }
        }

        // See this method's own remarks on why a null-ElementId issue is
        // dropped here rather than accumulated - Reconcile has no
        // meaningful per-dimension bucket for one.
        var dimensionLinkedIssues = issuesList.Where(i => i.ElementId is not null).ToList();

        var investigatedNow = new HashSet<long>(investigatedElementIds);
        entry.InvestigationIssues.RemoveAll(i => i.ElementId is { } id && investigatedNow.Contains(id));
        entry.InvestigationIssues.AddRange(dimensionLinkedIssues);
        entry.LastReconciliation = InvestigationReconciliation.Reconcile(
            entry.TriageIssues, entry.InvestigatedElementIds, entry.InvestigationIssues);
    }

    /// <summary>
    /// Bulk-dismisses every view in <paramref name="viewIds"/> with one
    /// shared <paramref name="reason"/> - the mechanism a whole diagrammatic
    /// sheet gets cycled past in one action rather than view by view. A view
    /// id with no existing row is skipped, not an error. Does not, by
    /// itself, change a view's <see cref="ViewChecklistEntry.Status"/> away
    /// from <see cref="ViewInvestigationStatus.Flagged"/> - see
    /// <see cref="ViewChecklistEntry.Status"/>'s own remarks.
    /// </summary>
    /// <remarks>
    /// <paramref name="reason"/> is coerced to <c>""</c> if null - a real
    /// bug found on the Revit machine, 2026-08-31: <c>Status</c> uses
    /// <c>ManualResolutionReason is not null</c> as the sole signal that a
    /// view was dismissed at all, so calling this method with
    /// <paramref name="reason"/> null (the checklist window's own reason
    /// prompt returned exactly that for a confirmed-but-blank box) silently
    /// did nothing - indistinguishable from "never dismissed". Calling this
    /// method at all means "resolve these views"; the reason is
    /// supplementary detail, never the signal that resolution happened, so
    /// this coercion happens here rather than trusting every future caller
    /// to remember it.
    /// </remarks>
    public void ResolveManually(IEnumerable<long> viewIds, string? reason)
    {
        var storedReason = reason ?? "";
        foreach (var viewId in viewIds)
        {
            var entry = FindView(viewId);
            if (entry is not null)
            {
                entry.ManualResolutionReason = storedReason;
            }
        }
    }

    /// <summary>Every confirmed problem across every view - reconciled per-dimension findings plus every non-dimension-linked investigation finding (already a verdict, not a candidate). The only list meant for automatic BCF export.</summary>
    public List<Issue> ExportableConfirmedProblems() =>
        IssueSorting.SortIssues(
            Views.SelectMany(v => v.LastReconciliation.ConfirmedProblems.Concat(v.OtherInvestigationFindings)));

    /// <summary>
    /// Investigated, but inconclusive - needs a human to read the drawing.
    /// JSON/CSV audit output only, never BCF. Excludes any view whose
    /// <see cref="ViewChecklistEntry.Status"/> reads
    /// <see cref="ViewInvestigationStatus.ResolvedManually"/> - a manual
    /// dismissal is specifically meant to stop that view nagging for
    /// attention, and its own reason is already the audit trail
    /// (<see cref="ExportableManualResolutions"/>), so surfacing its
    /// stale manual-review items here too would be noise, not safety - the
    /// one case where dismissal can't suppress anything
    /// (<see cref="ViewInvestigationStatus.Flagged"/>) is a different
    /// status entirely and is never filtered out here.
    /// </summary>
    public List<Issue> ExportableManualReview() =>
        IssueSorting.SortIssues(
            Views.Where(v => v.Status != ViewInvestigationStatus.ResolvedManually)
                .SelectMany(v => v.LastReconciliation.NeedsManualReview));

    /// <summary>
    /// Nothing has investigated this yet, across every not-manually-resolved
    /// view (see <see cref="ExportableManualReview"/> for why
    /// <see cref="ViewInvestigationStatus.ResolvedManually"/> views are
    /// excluded here too), plus every <see cref="ModelWideNotes"/> entry
    /// (which by definition can never be investigated - there is no view to
    /// cycle to, and so can never be manually resolved either). JSON/CSV
    /// audit output only, never BCF.
    /// </summary>
    public List<Issue> ExportableStillOpenTriage() =>
        IssueSorting.SortIssues(
            Views.Where(v => v.Status != ViewInvestigationStatus.ResolvedManually)
                .SelectMany(v => v.LastReconciliation.StillOpenTriage)
                .Concat(ModelWideNotes));

    /// <summary>Every view a human bulk-dismissed and why - the audit trail CLAUDE.md's "a rule must say how it reached its conclusion" calls for. Never a BCF finding list source by definition - nothing was found, a human just judged the view out of scope.</summary>
    public List<ManualResolutionRecord> ExportableManualResolutions() =>
        Views
            .Where(v => v.ManualResolutionReason is not null)
            .Select(v => new ManualResolutionRecord
            {
                ViewId = v.ViewId,
                ViewName = v.ViewName,
                SheetNo = v.SheetNo,
                Reason = v.ManualResolutionReason,
            })
            .ToList();
}
