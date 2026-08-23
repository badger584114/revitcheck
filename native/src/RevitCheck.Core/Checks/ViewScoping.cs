using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Checks;

/// <summary>Which views a dimension check should look at - direct port of <c>checks/dimensions.py</c>'s <c>views_in_scope</c> and its helpers.</summary>
public static class ViewScoping
{
    /// <summary>
    /// View templates never hold real dimensions. Unplaced views are
    /// excluded by default since nothing in them is issued to anyone. A
    /// Drafting View not standing in for a section cut is excluded for a
    /// different reason: every dimension in it was always going to classify
    /// Drafted, so checking it is pure volume with no decision left to
    /// make. A view on a sheet whose title matches
    /// <see cref="RuleConfig.ExcludedSheetTitleKeywords"/> is excluded for a
    /// third, different reason again - not "no decision to make" but "this
    /// sheet's convention isn't setout in the first place".
    /// </summary>
    public static List<ViewInfo> ViewsInScope(RevitModel model, RuleConfig config)
    {
        var scoped = new List<ViewInfo>();
        foreach (var view in model.Views)
        {
            if (view.IsTemplate)
            {
                continue;
            }

            if (config.SheetedViewsOnly && view.SheetNo is null)
            {
                continue;
            }

            if (config.SkipUnlinkedDraftingViews && IsUnlinkedDraftingView(view))
            {
                continue;
            }

            if (config.ExcludedSheetTitleKeywords.Count > 0 && SheetTitleExcluded(model, view, config))
            {
                continue;
            }

            scoped.Add(view);
        }

        return scoped;
    }

    private static bool SheetTitleExcluded(RevitModel model, ViewInfo view, RuleConfig config)
    {
        var sheet = model.SheetById(view.SheetId);
        var title = (sheet?.Name ?? "").ToLowerInvariant();
        return config.ExcludedSheetTitleKeywords.Any(keyword => title.Contains(keyword.ToLowerInvariant()));
    }

    /// <summary>A Drafting View standing on its own, not referenced by a callout cut from the model. The one place ViewInfo.IsDraftingView isn't the whole answer.</summary>
    public static bool IsUnlinkedDraftingView(ViewInfo? view) =>
        view is not null && view.IsDraftingView && !view.LinkedToModelSection;
}
