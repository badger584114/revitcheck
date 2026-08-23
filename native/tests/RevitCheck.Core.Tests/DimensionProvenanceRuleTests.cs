using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>Port of test_dimension_provenance.py's TestRule (21 tests).</summary>
public class DimensionProvenanceRuleTests
{
    private static List<Issues.Issue> Run(Ir.RevitModel model, RuleConfig? config = null) =>
        DimensionProvenanceCheck.Run(model, config ?? new RuleConfig());

    [Fact]
    public void LiveViewReportsNothing()
    {
        var dims = new[]
        {
            RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.ModelRef(101) }),
            RevitCheckTestBuilders.Dimension(2, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.DatumRef() }),
        };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: dims));
        Assert.Empty(issues);
    }

    [Fact]
    public void SingleDraftedDimensionInALiveView()
    {
        var dims = new[]
        {
            RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.ModelRef(101) }),
            RevitCheckTestBuilders.Dimension(2, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(201) }),
        };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: dims));
        var issue = Assert.Single(issues);
        Assert.Equal(2, issue.ElementId);
        Assert.Equal("high", issue.Severity);
        Assert.Equal("S101", issue.SheetNo);
        Assert.Contains("detail linework", issue.Description);
    }

    [Fact]
    public void UniqueIdFallsBackToTheDimensionWhenNoSheetAnchor()
    {
        // bcf.py's Component AuthoringToolId depends on this surviving the
        // trip from the IR onto the Issue that gets exported. The sheet's
        // own unique_id is preferred when there is one (next test) - this
        // is the fallback for a view with no sheet anchor.
        var dims = new[] { RevitCheckTestBuilders.Dimension(2, 10, new[] { RevitCheckTestBuilders.DraftedRef() }, uniqueId: "abc-123") };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: dims));
        Assert.Equal("abc-123", Assert.Single(issues).UniqueId);
    }

    [Fact]
    public void UniqueIdPrefersTheSheetOverTheDimension()
    {
        // Changed 2026-08-22: a real Forma import couldn't place issues
        // pinned to a Dimension/View element ('may not match the current
        // model') - neither has 3D placement for a model viewer to
        // resolve, where a sheet is exactly what a document-coordination
        // platform navigates to directly.
        var dims = new[] { RevitCheckTestBuilders.Dimension(2, 10, new[] { RevitCheckTestBuilders.DraftedRef() }, uniqueId: "dim-guid") };
        var views = new[] { RevitCheckTestBuilders.View(10, sheetUniqueId: "sheet-guid") };
        var issues = Run(RevitCheckTestBuilders.Model(views: views, dimensions: dims));
        Assert.Equal("sheet-guid", Assert.Single(issues).UniqueId);
    }

    [Fact]
    public void FullyDraftedViewRollsUpToOneIssue()
    {
        // Twenty identical findings on one view is noise; the view is the
        // real finding, and it's the unit the follow-up tool works on.
        var dims = Enumerable.Range(1, 5)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(201) }))
            .ToArray();
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: dims));
        var issue = Assert.Single(issues);
        Assert.Equal(10, issue.ElementId); // the view, not a dimension
        Assert.Equal("view", issue.SuggestedFix!["scope"]);
        Assert.Equal(5, issue.SuggestedFix!["dimensions"]);
    }

    [Fact]
    public void UniqueIdOnARollupFallsBackToTheView()
    {
        var dims = Enumerable.Range(1, 5)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(201) }))
            .ToArray();
        var views = new[] { RevitCheckTestBuilders.View(10, uniqueId: "view-guid-1") };
        var issues = Run(RevitCheckTestBuilders.Model(views: views, dimensions: dims));
        Assert.Equal("view-guid-1", Assert.Single(issues).UniqueId);
    }

    [Fact]
    public void UniqueIdOnARollupPrefersTheSheet()
    {
        var dims = Enumerable.Range(1, 5)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(201) }))
            .ToArray();
        var views = new[] { RevitCheckTestBuilders.View(10, uniqueId: "view-guid-1", sheetUniqueId: "sheet-guid-1") };
        var issues = Run(RevitCheckTestBuilders.Model(views: views, dimensions: dims));
        Assert.Equal("sheet-guid-1", Assert.Single(issues).UniqueId);
    }

    [Fact]
    public void MajorityDraftedViewRollsUpWithTheLiveDimensionExcluded()
    {
        // The real-world case that motivated the threshold: a view can be
        // almost entirely drafted with a handful of dimensions that
        // genuinely track the model. The rollup should still fire, and the
        // live dimension shouldn't appear as an issue at all (it's fine).
        var drafted = Enumerable.Range(1, 9)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(200 + i) }))
            .ToList();
        var live = new[] { RevitCheckTestBuilders.Dimension(50, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.ModelRef(301) }) };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: drafted.Concat(live)));
        var issue = Assert.Single(issues);
        Assert.Equal(10, issue.ElementId);
        Assert.Equal("view", issue.SuggestedFix!["scope"]);
        Assert.Equal(10, issue.SuggestedFix!["dimensions"]);
        Assert.Equal(9, issue.SuggestedFix!["drafted_dimensions"]);
        Assert.Contains("9 of 10 dimensions", issue.Description);
        Assert.DoesNotContain("Every dimension", issue.Description);
    }

    [Fact]
    public void BelowThresholdDoesNotRollUp()
    {
        // 7 of 10 drafted (70%) is below the default 90% threshold, so this
        // should still fall through to per-dimension reporting.
        var drafted = Enumerable.Range(1, 7)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(200 + i) }))
            .ToList();
        var live = Enumerable.Range(8, 3)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.ModelRef(300 + i) }))
            .ToList();
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: drafted.Concat(live)));
        Assert.Equal(7, issues.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7 }, issues.Select(i => i.ElementId!.Value).OrderBy(x => x));
    }

    [Fact]
    public void MixedAndUnknownDimensionsStillReportedInsideARollup()
    {
        // A Mixed or Unknown dimension is a distinct finding the rollup's
        // "detail linework" summary doesn't cover, so it must survive
        // alongside the rollup rather than being silently absorbed.
        var drafted = Enumerable.Range(1, 9)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(200 + i) }))
            .ToList();
        var mixed = new[] { RevitCheckTestBuilders.Dimension(99, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.DraftedRef() }) };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: drafted.Concat(mixed)));
        Assert.Equal(2, issues.Count);
        var rollup = issues.Single(i => i.SuggestedFix is not null && i.SuggestedFix.TryGetValue("scope", out var s) && (string)s! == "view");
        var mixedIssue = issues.Single(i => i.ElementId == 99);
        Assert.Equal(9, rollup.SuggestedFix!["drafted_dimensions"]);
        Assert.Equal("medium", mixedIssue.Severity);
    }

    [Fact]
    public void RollupThresholdIsConfigurable()
    {
        var drafted = Enumerable.Range(1, 7)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(200 + i) }))
            .ToList();
        var live = Enumerable.Range(8, 3)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.ModelRef(300 + i) }))
            .ToList();
        var config = new RuleConfig { DimensionProvenance = new DimensionProvenanceOptions { RollupThreshold = 0.7 } };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: drafted.Concat(live)), config);
        var issue = Assert.Single(issues);
        Assert.Equal("view", issue.SuggestedFix!["scope"]);
    }

    [Fact]
    public void RollUpCanBeTurnedOff()
    {
        var dims = Enumerable.Range(1, 5)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(201) }))
            .ToArray();
        var config = new RuleConfig { DimensionProvenance = new DimensionProvenanceOptions { RollUpFullyDraftedViews = false } };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: dims), config);
        Assert.Equal(5, issues.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, issues.Select(i => i.ElementId!.Value).OrderBy(x => x));
    }

    [Fact]
    public void SingleDimensionViewDoesNotRollUp()
    {
        // One drafted dimension says nothing about how the view was
        // drafted, so it's reported as itself rather than as a verdict on
        // the whole view.
        var dims = new[] { RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(201) }) };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: dims));
        var issue = Assert.Single(issues);
        Assert.Equal(1, issue.ElementId);
        Assert.False(issue.SuggestedFix!.ContainsKey("scope"));
    }

    [Fact]
    public void UnlinkedDraftingViewIsSkippedByDefault()
    {
        // A free-standing drafting view's dimensions were always going to
        // be Drafted - there's no decision left to report, so by default
        // it's out of scope entirely rather than producing a low-severity
        // finding for every one of them.
        var dims = Enumerable.Range(1, 3)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(201) }))
            .ToArray();
        var views = new[] { RevitCheckTestBuilders.View(10, name: "TYPICAL DETAIL", viewType: "DraftingView") };
        var issues = Run(RevitCheckTestBuilders.Model(views: views, dimensions: dims));
        Assert.True(issues.Count == 0 || issues.All(i => i.Category == "coverage"));
    }

    [Fact]
    public void UnlinkedDraftingViewCheckedWhenOptedIn()
    {
        // Still reachable via config, e.g. for an audit that wants full
        // coverage on record rather than the reduced-volume default.
        var dims = Enumerable.Range(1, 3)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(201) }))
            .ToArray();
        var views = new[] { RevitCheckTestBuilders.View(10, name: "TYPICAL DETAIL", viewType: "DraftingView") };
        var config = new RuleConfig { SkipUnlinkedDraftingViews = false };
        var issues = Run(RevitCheckTestBuilders.Model(views: views, dimensions: dims), config);
        var issue = Assert.Single(issues);
        Assert.Equal("low", issue.Severity);
        Assert.Contains("no model behind", issue.Description);
    }

    [Fact]
    public void LinkedDraftingViewIsCheckedAtModelSeverity()
    {
        // A drafting view referenced by a "Reference other view" callout
        // from a section is standing in for that section - it never has
        // model geometry either way, but treating it as a harmless
        // standard detail would hide the real drift risk it carries.
        var dims = Enumerable.Range(1, 3)
            .Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(201) }))
            .ToArray();
        var views = new[] { RevitCheckTestBuilders.View(10, name: "ABUTMENT A SECTION (ref)", viewType: "DraftingView", linkedToModelSection: true) };
        var issues = Run(RevitCheckTestBuilders.Model(views: views, dimensions: dims));
        var issue = Assert.Single(issues);
        Assert.Equal("high", issue.Severity);
        Assert.DoesNotContain("no model behind", issue.Description);
        Assert.Equal("view", issue.SuggestedFix!["scope"]);
    }

    [Fact]
    public void MixedProvenanceReportedSeparately()
    {
        var dims = new[] { RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.DraftedRef() }) };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: dims));
        var issue = Assert.Single(issues);
        Assert.Equal("medium", issue.Severity);
        Assert.Equal(1, issue.SuggestedFix!["drafted_references"]);
    }

    [Fact]
    public void SpotDimensionIsLabelledAsOne()
    {
        var dims = new[]
        {
            RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.DraftedRef() }, spot: true),
            RevitCheckTestBuilders.Dimension(2, 10, new[] { RevitCheckTestBuilders.ModelRef() }),
        };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: dims));
        var issue = Assert.Single(issues);
        Assert.StartsWith("Spot dimension", issue.Description);
    }

    [Fact]
    public void UnresolvedDimensionIsALowCoverageFinding()
    {
        var dims = new[]
        {
            RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.UnresolvedRef() }),
            RevitCheckTestBuilders.Dimension(2, 10, new[] { RevitCheckTestBuilders.ModelRef() }),
        };
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: dims));
        var issue = Assert.Single(issues);
        Assert.Equal("low", issue.Severity);
        Assert.Contains("not checked", issue.Description);
    }

    [Fact]
    public void NoDimensionsReportsCoverageNotSilence()
    {
        // The bug this guards against is the expensive one: a rule that
        // ran against nothing looking exactly like a clean model.
        var issues = Run(RevitCheckTestBuilders.Model(views: new[] { RevitCheckTestBuilders.View(10) }, dimensions: Array.Empty<DimensionInfo>()));
        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Null(issue.ElementId);
    }

    [Fact]
    public void DimensionsOnlyInUnplacedViewsStillReportsCoverage()
    {
        var dims = new[] { RevitCheckTestBuilders.Dimension(1, 11, new[] { RevitCheckTestBuilders.DraftedRef() }) };
        var views = new[] { RevitCheckTestBuilders.View(10), RevitCheckTestBuilders.View(11, sheetNo: null) };
        var issues = Run(RevitCheckTestBuilders.Model(views: views, dimensions: dims));
        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
    }
}

/// <summary>Port of test_dimension_provenance.py's TestDraftedViewsHandoff (2 tests).</summary>
public class DraftedViewsHandoffTests
{
    [Fact]
    public void ListsOnlyFullyDraftedViews()
    {
        var views = new[] { RevitCheckTestBuilders.View(10, name: "ALL DRAFTED"), RevitCheckTestBuilders.View(11, name: "LIVE") };
        var dims = new[]
        {
            RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.DraftedRef() }),
            RevitCheckTestBuilders.Dimension(2, 10, new[] { RevitCheckTestBuilders.DraftedRef() }),
            RevitCheckTestBuilders.Dimension(3, 11, new[] { RevitCheckTestBuilders.ModelRef() }),
            RevitCheckTestBuilders.Dimension(4, 11, new[] { RevitCheckTestBuilders.DraftedRef() }),
        };
        var result = DimensionProvenanceCheck.DraftedViews(RevitCheckTestBuilders.Model(views: views, dimensions: dims), new RuleConfig());
        Assert.Equal(new[] { "ALL DRAFTED" }, result.Select(v => v.Name));
    }

    [Fact]
    public void EmptyWhenNothingIsDrafted()
    {
        var views = new[] { RevitCheckTestBuilders.View(10) };
        var dims = new[] { 1, 2 }.Select(i => RevitCheckTestBuilders.Dimension(i, 10, new[] { RevitCheckTestBuilders.ModelRef() }));
        Assert.Empty(DimensionProvenanceCheck.DraftedViews(RevitCheckTestBuilders.Model(views: views, dimensions: dims), new RuleConfig()));
    }
}
