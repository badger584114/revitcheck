using RevitCheck.Core.Checks;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// Round-trip tests for <see cref="CheckingSessionSerializer"/>, mirroring
/// <c>CaptureSerializerTests.cs</c>'s own coverage shape (schema-version
/// stamping, refusal of a newer schema, forward-compatible load, a real
/// file round trip) plus session-specific state a resume genuinely depends
/// on: a manually-resolved row's reason, and the exact
/// pile-chain-into-per-dimension-findings shape
/// <see cref="InvestigationReconciliation.ExpandByElementIdList"/> produces.
/// </summary>
public class CheckingSessionSerializerTests
{
    private static Issue PerDimensionTriage(long elementId, long viewId) => new()
    {
        RuleId = DimensionProvenanceCheck.RuleId,
        Category = "geometry",
        Severity = "high",
        ElementId = elementId,
        ViewId = viewId,
        ViewName = "PLAN - PILE LAYOUT",
        SheetNo = "2873041",
        Description = $"Dimension {elementId} is drafted.",
        SuggestedFix = new Dictionary<string, object?> { ["provenance"] = "drafted", ["scope"] = "dimension" },
    };

    private static CheckingSession BuildSessionWithEveryRowShape()
    {
        var config = new RuleConfig { PileCategoryName = "Piling", PileChainBearingToleranceDegrees = 0.01 };

        var triage = new[]
        {
            PerDimensionTriage(10, viewId: 100),
            PerDimensionTriage(20, viewId: 100),
            PerDimensionTriage(30, viewId: 200),
            new Issue
            {
                RuleId = DimensionProvenanceCheck.RuleId,
                Category = "coverage",
                Severity = "low",
                Description = "3 dimension(s) could not be classified.",
            },
        };

        var session = CheckingSession.Start(triage, config);

        // View 100: a flagged chain, expanded into per-dimension findings -
        // the shape this whole design exists to get right.
        var chainIssue = new Issue
        {
            RuleId = "revitcheck.pile_chain_bearing_consistency",
            Category = "geometry",
            Severity = "high",
            ElementId = 500,
            UniqueId = "pile-guid-500",
            Description = "Reconstructed bearing disagrees with the drafted call.",
            SuggestedFix = new Dictionary<string, object?> { ["dimension_element_ids"] = new List<long> { 10, 20 } },
        };
        var expanded = InvestigationReconciliation.ExpandByElementIdList(new[] { chainIssue }, "dimension_element_ids");
        session.RecordInvestigation(100, investigatedElementIds: new long[] { 10, 20 }, investigationIssues: expanded);

        // View 200: manually resolved, with a real reason.
        session.ResolveManually(new long[] { 200 }, "Diagrammatic - construction sequence, never live setout.");

        return session;
    }

    [Fact]
    public void RoundTrip_PreservesEveryRowShapeAndStatus()
    {
        var session = BuildSessionWithEveryRowShape();

        var loaded = CheckingSessionSerializer.Loads(CheckingSessionSerializer.Dumps(session));

        Assert.Equal("Piling", loaded.Config.PileCategoryName);
        Assert.Equal(0.01, loaded.Config.PileChainBearingToleranceDegrees);

        var modelWideNote = Assert.Single(loaded.ModelWideNotes);
        Assert.Equal("coverage", modelWideNote.Category);

        Assert.Equal(2, loaded.Views.Count);

        var view100 = loaded.FindView(100)!;
        Assert.Equal(ViewInvestigationStatus.Flagged, view100.Status);
        Assert.Equal(new long[] { 10, 20 }, view100.InvestigatedElementIds);
        Assert.Equal(2, view100.InvestigationIssues.Count);
        Assert.Equal(2, view100.LastReconciliation.ConfirmedProblems.Count);
        var confirmed = view100.LastReconciliation.ConfirmedProblems.Single(i => i.ElementId == 10);
        Assert.Equal("500", confirmed.SuggestedFix!["source_element_id"]!.ToString());
        Assert.Null(confirmed.UniqueId);

        var view200 = loaded.FindView(200)!;
        Assert.Equal(ViewInvestigationStatus.ResolvedManually, view200.Status);
        Assert.Equal("Diagrammatic - construction sequence, never live setout.", view200.ManualResolutionReason);

        var resolution = Assert.Single(loaded.ExportableManualResolutions());
        Assert.Equal(200, resolution.ViewId);
    }

    [Fact]
    public void ResumedSession_can_still_be_investigated_further_and_a_rollup_still_clears()
    {
        // The real Stage 4 scenario RoundTrip_PreservesEveryRowShapeAndStatus
        // above does NOT cover: that test's LastReconciliation was computed
        // BEFORE serialization, so it never exercises Reconcile reading a
        // round-tripped Issue's own SuggestedFix. Resuming, then continuing
        // to investigate, does exactly that - and found a real bug on the
        // Revit machine, 2026-08-31: a resumed session's rollup issue could
        // never clear again, because its drafted_dimension_ids came back as
        // a JsonElement, not a List<long>/List<object> (see
        // InvestigationReconciliation.ElementIdList's own remarks).
        var config = new RuleConfig();
        var rollup = new Issue
        {
            RuleId = DimensionProvenanceCheck.RuleId,
            Category = "geometry",
            Severity = "high",
            ElementId = 900,
            ViewId = 900,
            ViewName = "PLAN - PILE LAYOUT",
            SheetNo = "2873041",
            Description = "Every dimension in this view is drafted.",
            SuggestedFix = new Dictionary<string, object?>
            {
                ["scope"] = "view",
                ["drafted_dimension_ids"] = new List<long> { 10, 20 },
            },
        };
        var session = CheckingSession.Start(new[] { rollup }, config);

        var resumed = CheckingSessionSerializer.Loads(CheckingSessionSerializer.Dumps(session));

        // Investigate the resumed session's view for real - both
        // dimensions come back clean, so the rollup should clear.
        resumed.RecordInvestigation(900, investigatedElementIds: new long[] { 10, 20 }, investigationIssues: Array.Empty<Issue>());

        var view = resumed.FindView(900)!;
        Assert.Equal(ViewInvestigationStatus.Resolved, view.Status);
        Assert.Empty(view.LastReconciliation.StillOpenTriage);
    }

    [Fact]
    public void SchemaVersion_IsWrittenOnEveryDump()
    {
        var json = CheckingSessionSerializer.Dumps(CheckingSession.Start(Array.Empty<Issue>(), new RuleConfig()));

        Assert.Contains($"\"schema_version\": {CheckingSessionSerializer.SchemaVersion}", json);
    }

    [Fact]
    public void ANewerSession_IsRefusedRatherThanMisread()
    {
        var json = CheckingSessionSerializer.Dumps(CheckingSession.Start(Array.Empty<Issue>(), new RuleConfig()))
            .Replace(
                $"\"schema_version\": {CheckingSessionSerializer.SchemaVersion}",
                $"\"schema_version\": {CheckingSessionSerializer.SchemaVersion + 1}");

        Assert.Throws<InvalidOperationException>(() => CheckingSessionSerializer.Loads(json));
    }

    [Fact]
    public void MissingViewsKey_LoadsAsEmptySession_ForwardCompatibility()
    {
        var json = $"{{\"schema_version\": {CheckingSessionSerializer.SchemaVersion}}}";

        var loaded = CheckingSessionSerializer.Loads(json);

        Assert.Empty(loaded.Views);
        Assert.Empty(loaded.ModelWideNotes);
    }

    [Fact]
    public void FileRoundTrip_SurvivesDisk()
    {
        var session = BuildSessionWithEveryRowShape();
        var path = Path.Combine(Path.GetTempPath(), $"revitcheck-session-test-{Guid.NewGuid()}.session.json");
        try
        {
            CheckingSessionSerializer.Save(session, path);
            var loaded = CheckingSessionSerializer.Load(path);
            Assert.Equal(2, loaded.Views.Count);
            Assert.Equal(ViewInvestigationStatus.ResolvedManually, loaded.FindView(200)!.Status);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
