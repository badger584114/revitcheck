using System.Collections.Generic;
using System.Linq;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Every check that can settle a triaged dimension, run against one view -
/// what the Check Dimensions button records as a single investigation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Moved out of the command 2026-09-11, because the command got it
/// wrong.</b> As first built, Check Dimensions ran Pile Chain Bearing and
/// recorded that check's investigated scope, but never ran
/// <see cref="PileDimensionConsistencyCheck"/> - so a pile view's
/// tag-to-tag dimensions reconciled on chain topology without their stated
/// values being read, which is exactly the trap PLANNING.md §27 was written
/// to close. It was found by reading the code, not by any test, because a
/// command cannot be tested. This can.
/// </para>
/// <para>
/// <b>"Investigated" means the dimension's own value was compared</b> - by
/// the drafted-dimension check, the spot elevation check, or the pile
/// dimension check. Pile Chain Bearing contributes findings only: it reads
/// a run's shape, never a dimension's number. The one exception is
/// bookkeeping. A dimension carrying a bearing finding is listed too, so
/// that <see cref="CheckingSession.RecordInvestigation"/> replaces that
/// finding on a re-run instead of accumulating copies. That cannot make a
/// dimension reconcile as clean: a problem or manual-review finding
/// outranks "investigated" in <see cref="InvestigationReconciliation.Reconcile"/>,
/// and a recorded finding is only ever cleared by a run that examined the
/// same dimension again.
/// </para>
/// <para>
/// Findings from the two whole-model pile checks arrive without view
/// context, and the chain-keyed and pair-keyed ones name a list of
/// dimensions rather than one. Both are expanded per dimension and given
/// this view's name and sheet, so every dimension they cover reconciles as
/// a finding rather than as clean.
/// </para>
/// </remarks>
public static class DimensionInvestigation
{
    private const string DimensionIdsKey = "dimension_element_ids";

    public static (List<Issue> Issues, List<long> InvestigatedElementIds) Run(
        RevitModel model, RuleConfig config, long viewId, string? viewName, string? sheetNo)
    {
        var issues = new List<Issue>();
        var investigated = new List<long>();

        var drawn = DrawnDimensionConsistencyCheck.RunWithScope(model, config);
        issues.AddRange(drawn.Issues);
        investigated.AddRange(drawn.InvestigatedElementIds);

        var spot = SpotElevationConsistencyCheck.RunWithScope(model, config);
        issues.AddRange(spot.Issues);
        investigated.AddRange(spot.InvestigatedElementIds);

        // The check that answers triage's question for a tag-to-tag
        // dimension, and the one DimensionResolution names for it: stated
        // distance against the real pile spacing.
        var pileDimension = PileDimensionConsistencyCheck.RunWithScope(model, config);
        issues.AddRange(InViewPerDimension(pileDimension.Issues, viewId, viewName, sheetNo));
        investigated.AddRange(pileDimension.InvestigatedElementIds);

        // Findings only - its own investigated scope is chain topology, not
        // a comparison of any dimension's value. See the remarks.
        var bearing = PileChainBearingConsistencyCheck.RunWithScope(model, config);
        var bearingIssues = InViewPerDimension(bearing.Issues, viewId, viewName, sheetNo);
        issues.AddRange(bearingIssues);

        var dimensionIds = new HashSet<long>(model.Dimensions.Select(d => d.ElementId));
        investigated.AddRange(bearingIssues
            .Where(i => i.ElementId is { } id && dimensionIds.Contains(id))
            .Select(i => i.ElementId!.Value));

        return (issues, investigated.Distinct().ToList());
    }

    /// <summary>
    /// One copy per dimension a finding names, carrying the view it was
    /// found in - a whole-model check has no view of its own.
    /// </summary>
    private static List<Issue> InViewPerDimension(
        IEnumerable<Issue> issues, long viewId, string? viewName, string? sheetNo) =>
        InvestigationReconciliation.ExpandByElementIdList(issues, DimensionIdsKey)
            .Select(i => new Issue
            {
                RuleId = i.RuleId,
                Category = i.Category,
                Severity = i.Severity,
                ElementId = i.ElementId,
                UniqueId = i.UniqueId,
                ViewId = i.ViewId ?? viewId,
                ViewName = i.ViewName ?? viewName,
                SheetNo = i.SheetNo ?? sheetNo,
                Description = i.Description,
                SuggestedFix = i.SuggestedFix,
            })
            .ToList();
}
