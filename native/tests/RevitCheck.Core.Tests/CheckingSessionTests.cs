using RevitCheck.Core.Checks;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;
using Xunit;

namespace RevitCheck.Core.Tests;

public class CheckingSessionTests
{
    private static Issue PerDimensionTriage(long elementId, long viewId, string viewName = "PLAN - PILE LAYOUT", string sheetNo = "2873041") => new()
    {
        RuleId = DimensionProvenanceCheck.RuleId,
        Category = "geometry",
        Severity = "high",
        ElementId = elementId,
        ViewId = viewId,
        ViewName = viewName,
        SheetNo = sheetNo,
        Description = $"Dimension {elementId} is drafted.",
        SuggestedFix = new Dictionary<string, object?> { ["provenance"] = "drafted", ["scope"] = "dimension" },
    };

    private static Issue RollupTriage(long viewId, IEnumerable<long> draftedDimensionIds, string viewName = "PLAN - PILE LAYOUT", string sheetNo = "2873041") => new()
    {
        RuleId = DimensionProvenanceCheck.RuleId,
        Category = "geometry",
        Severity = "high",
        ElementId = viewId,
        ViewId = viewId,
        ViewName = viewName,
        SheetNo = sheetNo,
        Description = $"Every dimension in view {viewId} is drafted.",
        SuggestedFix = new Dictionary<string, object?>
        {
            ["provenance"] = "drafted",
            ["scope"] = "view",
            ["drafted_dimension_ids"] = draftedDimensionIds.ToList(),
        },
    };

    private static Issue ModelWideCoverageNote() => new()
    {
        RuleId = DimensionProvenanceCheck.RuleId,
        Category = "coverage",
        Severity = "low",
        Description = "3 dimension(s) could not be classified.",
    };

    private static Issue InvestigationProblem(long dimensionElementId) => new()
    {
        RuleId = "revitcheck.pile_chain_bearing_consistency",
        Category = "geometry",
        Severity = "high",
        ElementId = dimensionElementId,
        Description = $"Dimension {dimensionElementId} disagrees with the reconstructed bearing.",
    };

    private static Issue InvestigationManualReview(long dimensionElementId) => new()
    {
        RuleId = "revitcheck.pile_chain_bearing_consistency",
        Category = InvestigationReconciliation.ManualReviewCategory,
        Severity = "medium",
        ElementId = dimensionElementId,
        Description = $"Dimension {dimensionElementId}: ambiguous nearest pile.",
    };

    /// <summary>
    /// A whole-check coverage summary with no single element to anchor to -
    /// the real shape AbutmentElevationConsistencyCheck.RunWithScope always
    /// appends, even on a clean run (CLAUDE.md's "report a coverage
    /// indicator, never fail silently"). Real bug, 2026-09-02: passed into
    /// RecordInvestigation unfiltered, this used to be categorized as a
    /// confirmed problem by Reconcile (no ManualReviewCategory), silently
    /// flagging a genuinely clean view - and, since the supersede fix only
    /// matches issues with an ElementId, it was never cleaned up on re-run
    /// either.
    /// </summary>
    private static Issue InvestigationCoverageNote(string ruleId = "revitcheck.abutment_elevation_consistency") => new()
    {
        RuleId = ruleId,
        Category = "coverage",
        Severity = "low",
        Description = "3 Spot Elevation(s) found; 3 had a geometry search performed (3 confirmed, 0 mismatched, 0 with no nearby geometry, 0 with no drafted value to check).",
    };

    private static Issue PileScheduleFinding(long pileElementId) => new()
    {
        RuleId = "revitcheck.pile_model_schedule_consistency",
        Category = "geometry",
        Severity = "high",
        ElementId = pileElementId,
        Description = $"Pile {pileElementId} drifted from its schedule row.",
    };

    // ----- Start -----

    [Fact]
    public void Start_groups_triage_by_view_id_and_routes_view_less_issues_to_model_wide_notes()
    {
        var triage = new Issue[]
        {
            PerDimensionTriage(10, viewId: 100),
            PerDimensionTriage(11, viewId: 100),
            PerDimensionTriage(20, viewId: 200, viewName: "PLAN - ABUTMENT A1", sheetNo: "2871008"),
            ModelWideCoverageNote(),
        };

        var session = CheckingSession.Start(triage, new RuleConfig());

        Assert.Equal(2, session.Views.Count);
        var view100 = session.FindView(100)!;
        Assert.Equal("PLAN - PILE LAYOUT", view100.ViewName);
        Assert.Equal("2873041", view100.SheetNo);
        Assert.Equal(2, view100.TriageIssues.Count);
        var view200 = session.FindView(200)!;
        Assert.Equal("PLAN - ABUTMENT A1", view200.ViewName);

        var note = Assert.Single(session.ModelWideNotes);
        Assert.Equal("coverage", note.Category);
    }

    [Fact]
    public void Start_gives_every_view_a_status_before_any_investigation_runs()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };

        var session = CheckingSession.Start(triage, new RuleConfig());

        Assert.Equal(ViewInvestigationStatus.Pending, session.FindView(100)!.Status);
    }

    [Fact]
    public void Start_keeps_the_config_it_was_given_for_later_investigation_commands_to_reuse()
    {
        var config = new RuleConfig { PileCategoryName = "Piling" };

        var session = CheckingSession.Start(Array.Empty<Issue>(), config);

        Assert.Same(config, session.Config);
    }

    // ----- RecordInvestigation, dimension-linked -----

    [Fact]
    public void RecordInvestigation_confirmed_problem_flags_the_view()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: new[] { InvestigationProblem(10) });

        var view = session.FindView(100)!;
        Assert.Equal(ViewInvestigationStatus.Flagged, view.Status);
        Assert.Single(session.ExportableConfirmedProblems());
    }

    [Fact]
    public void RecordInvestigation_clean_result_resolves_the_view()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: Array.Empty<Issue>());

        Assert.Equal(ViewInvestigationStatus.Resolved, session.FindView(100)!.Status);
        Assert.Empty(session.ExportableConfirmedProblems());
        Assert.Empty(session.ExportableStillOpenTriage().Where(i => i.ElementId == 10));
    }

    [Fact]
    public void RecordInvestigation_a_whole_check_coverage_note_does_not_flag_an_otherwise_clean_view()
    {
        // Real bug, 2026-09-02: a coverage-style issue with no ElementId
        // (the shape AbutmentElevationConsistencyCheck.RunWithScope always
        // appends) used to be silently promoted to a confirmed problem,
        // flagging a view that was actually clean. See
        // InvestigationCoverageNote's own remarks.
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: new[] { InvestigationCoverageNote() });

        Assert.Equal(ViewInvestigationStatus.Resolved, session.FindView(100)!.Status);
        Assert.Empty(session.ExportableConfirmedProblems());
    }

    [Fact]
    public void RecordInvestigation_a_whole_check_coverage_note_does_not_accumulate_across_repeated_runs()
    {
        // The supersede fix (RecordInvestigation_supersedes_a_stale_verdict...
        // below) only ever matched issues with an ElementId, so a
        // null-element coverage note was never being cleaned up on re-run -
        // re-running the same check kept adding another one. Now it's never
        // added to InvestigationIssues at all, so this can't recur.
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: new[] { InvestigationCoverageNote() });
        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: new[] { InvestigationCoverageNote() });
        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: new[] { InvestigationCoverageNote() });

        Assert.Equal(ViewInvestigationStatus.Resolved, session.FindView(100)!.Status);
        Assert.Empty(session.ExportableConfirmedProblems());
    }

    [Fact]
    public void RecordInvestigation_manual_review_result_marks_the_view_needing_manual_review()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: new[] { InvestigationManualReview(10) });

        Assert.Equal(ViewInvestigationStatus.NeedsManualReview, session.FindView(100)!.Status);
        Assert.Empty(session.ExportableConfirmedProblems());
        Assert.Single(session.ExportableManualReview());
    }

    [Fact]
    public void RecordInvestigation_accumulates_across_multiple_calls_for_the_same_view()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100), PerDimensionTriage(11, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: Array.Empty<Issue>());
        // Dimension 10 already investigated once - still Pending because 11 hasn't been examined yet.
        Assert.Equal(ViewInvestigationStatus.Pending, session.FindView(100)!.Status);

        session.RecordInvestigation(100, investigatedElementIds: new long[] { 11 }, investigationIssues: new[] { InvestigationProblem(11) });

        var view = session.FindView(100)!;
        Assert.Equal(ViewInvestigationStatus.Flagged, view.Status);
        Assert.Equal(new long[] { 10, 11 }, view.InvestigatedElementIds);
    }

    [Fact]
    public void RecordInvestigation_supersedes_a_stale_verdict_for_the_same_element_instead_of_accumulating_it()
    {
        // Real bug found on the Revit machine, 2026-08-31: an automated
        // check (e.g. PileChainBearingConsistencyCheck's near-miss
        // detection) flags dimension 10 manual_review; a human then
        // selects it and clicks Mark Resolved, which correctly calls
        // RecordInvestigation with an empty investigationIssues for that
        // same id - "clean" had nothing to overwrite the stale
        // manual_review Issue with, since nothing ever removed it, so it
        // kept showing in NeedsManualReview forever. The fix: recording a
        // NEW verdict for an id removes any OLD verdict issue for that
        // same id first - the latest call always wins, whichever source
        // (automated re-run or a human's own click) made it.
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: new[] { InvestigationManualReview(10) });
        Assert.Equal(ViewInvestigationStatus.NeedsManualReview, session.FindView(100)!.Status);

        // A human checks it against the drawing and marks it resolved.
        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: Array.Empty<Issue>());

        var view = session.FindView(100)!;
        Assert.Equal(ViewInvestigationStatus.Resolved, view.Status);
        Assert.Empty(view.LastReconciliation.NeedsManualReview);
        Assert.Empty(session.ExportableManualReview());
    }

    [Fact]
    public void RecordInvestigation_supersession_only_affects_the_ids_being_reinvestigated_now()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100), PerDimensionTriage(11, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10, 11 },
            investigationIssues: new[] { InvestigationManualReview(10), InvestigationProblem(11) });

        // Re-investigate only dimension 10 (resolved) - dimension 11's
        // confirmed problem must survive untouched.
        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: Array.Empty<Issue>());

        var view = session.FindView(100)!;
        Assert.Empty(view.LastReconciliation.NeedsManualReview);
        var confirmed = Assert.Single(view.LastReconciliation.ConfirmedProblems);
        Assert.Equal(11, confirmed.ElementId);
    }

    [Fact]
    public void RecordInvestigation_for_a_view_with_no_checklist_row_is_a_harmless_no_op()
    {
        var session = CheckingSession.Start(Array.Empty<Issue>(), new RuleConfig());

        session.RecordInvestigation(999, investigatedElementIds: new long[] { 1 }, investigationIssues: new[] { InvestigationProblem(1) });

        Assert.Empty(session.Views);
        Assert.Empty(session.ExportableConfirmedProblems());
    }

    // ----- RecordInvestigation, non-dimension-linked (otherFindingsRuleId) -----

    [Fact]
    public void RecordInvestigation_with_other_findings_rule_id_flags_the_view_without_reconciling_triage()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.RecordInvestigation(
            100, investigatedElementIds: new long[] { 700 }, investigationIssues: new[] { PileScheduleFinding(700) },
            otherFindingsRuleId: "revitcheck.pile_model_schedule_consistency");

        var view = session.FindView(100)!;
        Assert.Equal(ViewInvestigationStatus.Flagged, view.Status);
        // Dimension 10's own triage is untouched by a check with no dimension linkage - still open.
        Assert.Contains(session.ExportableStillOpenTriage(), i => i.ElementId == 10);
        Assert.Contains(session.ExportableConfirmedProblems(), i => i.ElementId == 700);
    }

    [Fact]
    public void RecordInvestigation_with_other_findings_rule_id_replaces_a_prior_runs_findings_for_that_rule_not_accumulates()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());
        const string ruleId = "revitcheck.pile_model_schedule_consistency";

        session.RecordInvestigation(100, new long[] { 700 }, new[] { PileScheduleFinding(700) }, otherFindingsRuleId: ruleId);
        // Re-run after the pile was fixed - no findings this time.
        session.RecordInvestigation(100, new long[] { 700 }, Array.Empty<Issue>(), otherFindingsRuleId: ruleId);

        Assert.Empty(session.FindView(100)!.OtherInvestigationFindings);
        Assert.Equal(ViewInvestigationStatus.Pending, session.FindView(100)!.Status);
    }

    // ----- Manual per-dimension verdicts (RecordInvestigation reused directly by the checklist window) -----

    [Fact]
    public void A_humans_own_verdict_on_one_dimension_within_a_rollup_is_recorded_the_same_way_an_automated_check_would_be()
    {
        // Real user feedback, 2026-08-31: there was no way to weigh in on
        // one specific dimension while manually checking it against the
        // drawing, only a whole view via ResolveManually. The fix reuses
        // RecordInvestigation directly - a human's own verdict is just
        // another investigation source. This test proves the real mixed
        // scenario: one dimension resolved by an automated check, one
        // resolved by a human with no issue (clean), one confirmed a real
        // problem by a human - the rollup should only clear once all three
        // are accounted for, and the confirmed one should be the human's,
        // correctly attributed via ManualVerdictRuleId.
        var rollup = RollupTriage(100, draftedDimensionIds: new long[] { 10, 20, 30 });
        var session = CheckingSession.Start(new[] { rollup }, new RuleConfig());

        // An automated pile check investigates dimension 10 and finds it clean.
        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10 }, investigationIssues: Array.Empty<Issue>());
        Assert.Equal(ViewInvestigationStatus.Pending, session.FindView(100)!.Status);

        // A human manually resolves dimension 30 (checked it, it's fine) -
        // recorded exactly the same way as the automated check above.
        session.RecordInvestigation(100, investigatedElementIds: new long[] { 30 }, investigationIssues: Array.Empty<Issue>());
        Assert.Equal(ViewInvestigationStatus.Pending, session.FindView(100)!.Status);

        // A human manually confirms dimension 20 as a real problem.
        var manualProblem = new Issue
        {
            RuleId = InvestigationReconciliation.ManualVerdictRuleId,
            Category = "geometry",
            Severity = "high",
            ElementId = 20,
            ViewId = 100,
            Description = "Manually confirmed as a real problem by a reviewer.",
        };
        session.RecordInvestigation(100, investigatedElementIds: new long[] { 20 }, investigationIssues: new[] { manualProblem });

        var view = session.FindView(100)!;
        // All three dimensions now accounted for - the rollup is fully
        // superseded, replaced by the one real confirmed problem.
        Assert.Equal(ViewInvestigationStatus.Flagged, view.Status);
        var confirmed = Assert.Single(view.LastReconciliation.ConfirmedProblems);
        Assert.Equal(InvestigationReconciliation.ManualVerdictRuleId, confirmed.RuleId);
        Assert.Equal(20, confirmed.ElementId);
        Assert.Empty(view.LastReconciliation.StillOpenTriage);
        var exported = Assert.Single(session.ExportableConfirmedProblems());
        Assert.Equal(20, exported.ElementId);
    }

    // ----- ResolveManually -----

    [Fact]
    public void ResolveManually_on_a_pending_view_marks_it_resolved_manually_and_removes_it_from_still_open_triage()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.ResolveManually(new long[] { 100 }, "Diagrammatic - construction sequence, never live setout.");

        var view = session.FindView(100)!;
        Assert.Equal(ViewInvestigationStatus.ResolvedManually, view.Status);
        Assert.Equal("Diagrammatic - construction sequence, never live setout.", view.ManualResolutionReason);
        var record = Assert.Single(session.ExportableManualResolutions());
        Assert.Equal(100, record.ViewId);
        Assert.Equal("Diagrammatic - construction sequence, never live setout.", record.Reason);
        Assert.DoesNotContain(session.ExportableStillOpenTriage(), i => i.ElementId == 10);
    }

    [Fact]
    public void ResolveManually_with_a_null_reason_still_marks_the_view_resolved()
    {
        // Real bug found on the Revit machine, 2026-08-31: the checklist
        // window's reason prompt returns null for a confirmed-but-blank
        // box, and Status used to read ManualResolutionReason's nullness
        // as the sole "was this dismissed at all" signal - so dismissing
        // with an empty reason silently did nothing.
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.ResolveManually(new long[] { 100 }, reason: null);

        var view = session.FindView(100)!;
        Assert.Equal(ViewInvestigationStatus.ResolvedManually, view.Status);
        Assert.Equal("", view.ManualResolutionReason);
        Assert.Single(session.ExportableManualResolutions());
    }

    [Fact]
    public void ResolveManually_bulk_resolves_every_view_id_given_in_one_call()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100), PerDimensionTriage(20, viewId: 200) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        session.ResolveManually(new long[] { 100, 200 }, "Whole sheet is diagrammatic.");

        Assert.Equal(ViewInvestigationStatus.ResolvedManually, session.FindView(100)!.Status);
        Assert.Equal(ViewInvestigationStatus.ResolvedManually, session.FindView(200)!.Status);
        Assert.Equal(2, session.ExportableManualResolutions().Count);
    }

    [Fact]
    public void ResolveManually_on_a_view_with_no_checklist_row_is_a_harmless_no_op()
    {
        var session = CheckingSession.Start(Array.Empty<Issue>(), new RuleConfig());

        session.ResolveManually(new long[] { 999 }, "doesn't exist");

        Assert.Empty(session.ExportableManualResolutions());
    }

    // ----- The safety rule: ResolvedManually must never bury a real confirmed problem -----

    [Fact]
    public void ResolveManually_cannot_bury_a_view_that_already_carries_a_confirmed_problem()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());
        session.RecordInvestigation(100, new long[] { 10 }, new[] { InvestigationProblem(10) });
        Assert.Equal(ViewInvestigationStatus.Flagged, session.FindView(100)!.Status);

        session.ResolveManually(new long[] { 100 }, "Thought this was diagrammatic.");

        // Status stays Flagged, not ResolvedManually - the confirmed problem
        // is never allowed to quietly stand aside for a blanket dismissal.
        var view = session.FindView(100)!;
        Assert.Equal(ViewInvestigationStatus.Flagged, view.Status);
        // The reason is still recorded - a human did attempt to dismiss it,
        // and that's worth keeping visible even though it didn't take effect.
        Assert.Equal("Thought this was diagrammatic.", view.ManualResolutionReason);
        // And the confirmed problem itself still exports for real, exactly as before.
        Assert.Single(session.ExportableConfirmedProblems());
    }

    [Fact]
    public void ResolveManually_cannot_bury_a_view_flagged_only_via_other_investigation_findings()
    {
        var triage = new[] { PerDimensionTriage(10, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());
        session.RecordInvestigation(
            100, new long[] { 700 }, new[] { PileScheduleFinding(700) },
            otherFindingsRuleId: "revitcheck.pile_model_schedule_consistency");
        Assert.Equal(ViewInvestigationStatus.Flagged, session.FindView(100)!.Status);

        session.ResolveManually(new long[] { 100 }, "Thought this was diagrammatic.");

        Assert.Equal(ViewInvestigationStatus.Flagged, session.FindView(100)!.Status);
        Assert.Single(session.ExportableConfirmedProblems());
    }

    // ----- The full regression: a flagged pile chain, expanded, recorded, and reconciled through CheckingSession -----

    [Fact]
    public void ExpandByElementIdList_into_RecordInvestigation_flags_every_dimension_in_a_flagged_chain_not_just_the_pile()
    {
        var chainIssue = new Issue
        {
            RuleId = "revitcheck.pile_chain_bearing_consistency",
            Category = "geometry",
            Severity = "high",
            ElementId = 500, // the pile - not either dimension
            UniqueId = "pile-guid-500",
            Description = "Reconstructed bearing disagrees with the drafted call.",
            SuggestedFix = new Dictionary<string, object?> { ["dimension_element_ids"] = new List<long> { 10, 20 } },
        };
        var triage = new[] { PerDimensionTriage(10, viewId: 100), PerDimensionTriage(20, viewId: 100) };
        var session = CheckingSession.Start(triage, new RuleConfig());

        var expanded = InvestigationReconciliation.ExpandByElementIdList(new[] { chainIssue }, "dimension_element_ids");
        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10, 20 }, investigationIssues: expanded);

        var view = session.FindView(100)!;
        Assert.Equal(ViewInvestigationStatus.Flagged, view.Status);
        Assert.Equal(2, view.LastReconciliation.ConfirmedProblems.Count);
        Assert.Empty(view.LastReconciliation.StillOpenTriage);
        Assert.Equal(
            new long?[] { 10, 20 },
            session.ExportableConfirmedProblems().Select(i => i.ElementId).OrderBy(id => id));
    }
}
