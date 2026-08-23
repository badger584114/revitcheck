using RevitCheck.Core.Catalog;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>Port of test_dimension_provenance.py's TestScoping (14 tests).</summary>
public class DimensionViewScopingTests
{
    [Fact]
    public void ViewTemplatesExcluded()
    {
        var views = new[] { RevitCheckTestBuilders.View(10), RevitCheckTestBuilders.View(11, isTemplate: true) };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views), new RuleConfig());
        Assert.Equal(new long[] { 10 }, scoped.Select(v => v.ElementId));
    }

    [Fact]
    public void UnplacedViewsExcludedByDefault()
    {
        var views = new[] { RevitCheckTestBuilders.View(10), RevitCheckTestBuilders.View(11, sheetNo: null) };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views), new RuleConfig());
        Assert.Equal(new long[] { 10 }, scoped.Select(v => v.ElementId));
    }

    [Fact]
    public void UnplacedViewsIncludedWhenAsked()
    {
        var views = new[] { RevitCheckTestBuilders.View(10), RevitCheckTestBuilders.View(11, sheetNo: null) };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views), new RuleConfig { SheetedViewsOnly = false });
        Assert.Equal(2, scoped.Count);
    }

    [Fact]
    public void TemplateStillExcludedWhenSweepingEverything()
    {
        var views = new[] { RevitCheckTestBuilders.View(11, sheetNo: null, isTemplate: true) };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views), new RuleConfig { SheetedViewsOnly = false });
        Assert.Empty(scoped);
    }

    [Fact]
    public void UnlinkedDraftingViewExcludedByDefault()
    {
        var views = new[] { RevitCheckTestBuilders.View(10), RevitCheckTestBuilders.View(11, viewType: "DraftingView") };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views), new RuleConfig());
        Assert.Equal(new long[] { 10 }, scoped.Select(v => v.ElementId));
    }

    [Fact]
    public void LinkedDraftingViewStaysInScope()
    {
        var views = new[] { RevitCheckTestBuilders.View(11, viewType: "DraftingView", linkedToModelSection: true) };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views), new RuleConfig());
        Assert.Equal(new long[] { 11 }, scoped.Select(v => v.ElementId));
    }

    [Fact]
    public void UnlinkedDraftingViewIncludedWhenOptedIn()
    {
        var views = new[] { RevitCheckTestBuilders.View(11, viewType: "DraftingView") };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views), new RuleConfig { SkipUnlinkedDraftingViews = false });
        Assert.Equal(new long[] { 11 }, scoped.Select(v => v.ElementId));
    }

    [Fact]
    public void LegendIsNeverLinkedAndStaysExcluded()
    {
        // LinkedToModelSection is a Drafting View concept (a callout
        // references one); a Legend can't be one, so it has no escape
        // hatch out of this exclusion the way a Drafting View does.
        var views = new[] { RevitCheckTestBuilders.View(11, viewType: "Legend") };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views), new RuleConfig());
        Assert.Empty(scoped);
    }

    [Fact]
    public void ReinforcementSheetExcludedByDefault()
    {
        var sheets = new[] { new SheetInfo { ElementId = 1, SheetNumber = "2873101", Name = "SUPER-T GIRDER REINFORCEMENT - SHEET 01" } };
        var views = new[] { RevitCheckTestBuilders.View(10, sheetId: 1) };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views, sheets: sheets), new RuleConfig());
        Assert.Empty(scoped);
    }

    [Fact]
    public void ReinforcementSheetMatchIsCaseInsensitive()
    {
        var sheets = new[] { new SheetInfo { ElementId = 1, SheetNumber = "2873101", Name = "Super-T Girder Reinforcement - Sheet 01" } };
        var views = new[] { RevitCheckTestBuilders.View(10, sheetId: 1) };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views, sheets: sheets), new RuleConfig());
        Assert.Empty(scoped);
    }

    [Fact]
    public void NonReinforcementSheetIsUnaffected()
    {
        var sheets = new[] { new SheetInfo { ElementId = 1, SheetNumber = "2873041", Name = "PILE LAYOUT" } };
        var views = new[] { RevitCheckTestBuilders.View(10, sheetId: 1) };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views, sheets: sheets), new RuleConfig());
        Assert.Equal(new long[] { 10 }, scoped.Select(v => v.ElementId));
    }

    [Fact]
    public void ExcludedSheetTitleKeywordsIsConfigurable()
    {
        var sheets = new[] { new SheetInfo { ElementId = 1, SheetNumber = "2873101", Name = "SUPER-T GIRDER REINFORCEMENT - SHEET 01" } };
        var views = new[] { RevitCheckTestBuilders.View(10, sheetId: 1) };
        var model = RevitCheckTestBuilders.Model(views: views, sheets: sheets);

        var scopedOff = ViewScoping.ViewsInScope(model, new RuleConfig { ExcludedSheetTitleKeywords = new List<string>() });
        Assert.Equal(new long[] { 10 }, scopedOff.Select(v => v.ElementId));

        var scopedCustom = ViewScoping.ViewsInScope(model, new RuleConfig { ExcludedSheetTitleKeywords = new List<string> { "girder" } });
        Assert.Empty(scopedCustom);
    }

    [Fact]
    public void ViewWithNoSheetIsUnaffectedByTheKeywordFilter()
    {
        // A view not on any sheet has already been excluded by
        // SheetedViewsOnly; with that off, it must not additionally be
        // caught by a title match against a sheet it isn't on.
        var views = new[] { RevitCheckTestBuilders.View(10, sheetNo: null) };
        var scoped = ViewScoping.ViewsInScope(RevitCheckTestBuilders.Model(views: views), new RuleConfig { SheetedViewsOnly = false });
        Assert.Equal(new long[] { 10 }, scoped.Select(v => v.ElementId));
    }

    [Fact]
    public void ReinforcementSheetExcludesBothDimensionRules()
    {
        // ExcludedSheetTitleKeywords is about the sheet's convention, not a
        // per-rule concern - both rules share ViewScoping.ViewsInScope, and
        // both should see the same narrower scope. Goes through the actual
        // Catalog (unlike the other scoping tests) specifically to exercise
        // that shared-scope interaction across both registered rules.
        var sheets = new[] { new SheetInfo { ElementId = 1, SheetNumber = "2873101", Name = "SUPER-T GIRDER REINFORCEMENT - SHEET 01" } };
        var views = new[] { RevitCheckTestBuilders.View(10, sheetId: 1) };
        var dims = new[] { RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.DraftedRef() }, overrideText: "A1") };
        var model = RevitCheckTestBuilders.Model(views: views, dimensions: dims, sheets: sheets);

        var catalog = new Catalog.Catalog();
        var config = new RuleConfig();
        catalog.Register(DimensionProvenanceCheck.RuleId, m => DimensionProvenanceCheck.Run(m, config));
        catalog.Register(DimensionOverrideConsistencyCheck.RuleId, m => DimensionOverrideConsistencyCheck.Run(m, config));

        var issues = catalog.RunChecks(model, new[] { DimensionProvenanceCheck.RuleId, DimensionOverrideConsistencyCheck.RuleId });

        // Only the coverage notes survive - "nothing in scope" for
        // provenance, "nothing was checked" for override consistency.
        Assert.All(issues, i => Assert.Equal("coverage", i.Category));
    }
}
