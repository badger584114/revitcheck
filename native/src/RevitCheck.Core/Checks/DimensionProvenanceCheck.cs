using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Flags dimensions that measure linework rather than the model - direct
/// port of <c>checks/dimensions.py</c>'s <c>check_dimension_provenance</c>.
/// Kept as <c>revit.dimension_provenance</c>, not the <c>revitcheck.</c>
/// prefix new C#-only rules use, deliberately: this rule id must match the
/// Python original so the same real capture can be run through both
/// engines and the issue lists diffed directly - the strongest available
/// check against the translation risk (float formatting, iteration order,
/// string handling) PLANNING.md §12 names.
/// </summary>
public static class DimensionProvenanceCheck
{
    public const string RuleId = "revit.dimension_provenance";

    // Below this, "every dimension in the view is drafted" isn't a
    // meaningful statement about the view - one dimension says nothing
    // about how the view was drafted. Those fall through to per-dimension
    // reporting instead.
    private const int MinDimsForViewRollup = 2;

    public static List<Issue> Run(RevitModel model, RuleConfig config)
    {
        var issues = new List<Issue>();
        var byView = model.DimensionsByView();
        var scoped = ViewScoping.ViewsInScope(model, config);
        var checkedAny = false;

        foreach (var view in scoped)
        {
            if (!byView.TryGetValue(view.ElementId, out var dims) || dims.Count == 0)
            {
                continue;
            }

            checkedAny = true;

            var verdicts = dims.ToDictionary(d => d.ElementId, DimensionClassification.ClassifyDimension);
            var drafted = dims.Where(d => verdicts[d.ElementId] == Provenance.Drafted).ToList();

            var rollsUp =
                config.DimensionProvenance.RollUpFullyDraftedViews
                && dims.Count >= MinDimsForViewRollup
                && drafted.Count > 0
                && (double)drafted.Count / dims.Count >= config.DimensionProvenance.RollupThreshold;

            if (rollsUp)
            {
                issues.Add(ViewRollupIssue(view, dims, drafted, config));

                // A rolled-up view can still hold the rarer verdicts - Mixed
                // or Unknown dimensions that survived because they aren't
                // Drafted and so weren't counted towards the threshold.
                // Those are distinct findings the rollup's "detail linework"
                // summary doesn't cover, so they're still reported
                // individually. Model/Datum dimensions need no issue either way.
                foreach (var dim in dims)
                {
                    if (verdicts[dim.ElementId] is Provenance.Mixed or Provenance.Unknown)
                    {
                        var issue = IssueForDimension(dim, view, verdicts[dim.ElementId], config);
                        if (issue is not null)
                        {
                            issues.Add(issue);
                        }
                    }
                }

                continue;
            }

            foreach (var dim in dims)
            {
                var issue = IssueForDimension(dim, view, verdicts[dim.ElementId], config);
                if (issue is not null)
                {
                    issues.Add(issue);
                }
            }
        }

        if (!checkedAny)
        {
            var suffix = config.SheetedViewsOnly ? ", placed on sheets" : "";
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "low",
                Description =
                    $"No dimensions were found in any view in scope ({scoped.Count} views checked{suffix}), " +
                    "so this rule reported nothing because there was nothing to report on — not because " +
                    "the model is clean.",
            });
        }

        return issues;
    }

    /// <summary>
    /// Views whose dimensions are *all* drafted - the handoff to the
    /// planned follow-up tool: these are the views whose setout can't be
    /// checked from the file and has to be compared against the model
    /// directly. Exposed separately so a caller doesn't have to parse it
    /// back out of Issue descriptions.
    /// </summary>
    public static List<ViewInfo> DraftedViews(RevitModel model, RuleConfig config)
    {
        var byView = model.DimensionsByView();
        var result = new List<ViewInfo>();
        foreach (var view in ViewScoping.ViewsInScope(model, config))
        {
            if (!byView.TryGetValue(view.ElementId, out var dims) || dims.Count < MinDimsForViewRollup)
            {
                continue;
            }

            if (dims.All(d => DimensionClassification.ClassifyDimension(d) == Provenance.Drafted))
            {
                result.Add(view);
            }
        }

        return result;
    }

    private static Issue? IssueForDimension(DimensionInfo dim, ViewInfo? view, Provenance verdict, RuleConfig config)
    {
        var kind = dim.IsSpot ? "Spot dimension" : "Dimension";
        var uniqueId = view?.SheetUniqueId ?? dim.UniqueId;

        switch (verdict)
        {
            case Provenance.Drafted:
            {
                var detail = ViewScoping.IsUnlinkedDraftingView(view)
                    ? $"{kind} in {DimensionDescriptions.DescribeView(view)} measures detail linework. A drafting view has no model behind it, so this cannot track the model by any means — correct for a standard detail, a drift risk if it is project-specific setout."
                    : $"{kind} in {DimensionDescriptions.DescribeView(view)} measures detail linework, not model geometry — it will not update when the model changes, and will keep agreeing with the line it measures while doing so.";

                return new Issue
                {
                    RuleId = RuleId,
                    Category = "geometry",
                    ElementId = dim.ElementId,
                    ViewId = dim.ViewId,
                    ViewName = view?.Name,
                    SheetNo = view?.SheetNo,
                    UniqueId = uniqueId,
                    Description = detail,
                    Severity = DimensionDescriptions.DraftedSeverity(view, config),
                    SuggestedFix = new Dictionary<string, object?>
                    {
                        ["provenance"] = "drafted",
                        ["references"] = dim.References.Count,
                        ["action"] = "re-dimension to model geometry, or verify against the model",
                    },
                };
            }

            case Provenance.Mixed:
                return new Issue
                {
                    RuleId = RuleId,
                    Category = "geometry",
                    ElementId = dim.ElementId,
                    ViewId = dim.ViewId,
                    ViewName = view?.Name,
                    SheetNo = view?.SheetNo,
                    UniqueId = uniqueId,
                    Description =
                        $"{kind} in {DimensionDescriptions.DescribeView(view)} measures model geometry at one end and detail linework at the other, so part of it tracks the model and part of it does not.",
                    Severity = config.MixedProvenanceSeverity,
                    SuggestedFix = new Dictionary<string, object?>
                    {
                        ["provenance"] = "mixed",
                        ["drafted_references"] = dim.References.Count(r => DimensionClassification.ClassifyReference(r) == Provenance.Drafted),
                        ["references"] = dim.References.Count,
                    },
                };

            case Provenance.Unknown:
                return new Issue
                {
                    RuleId = RuleId,
                    Category = "geometry",
                    ElementId = dim.ElementId,
                    ViewId = dim.ViewId,
                    ViewName = view?.Name,
                    SheetNo = view?.SheetNo,
                    UniqueId = uniqueId,
                    Description =
                        $"{kind} in {DimensionDescriptions.DescribeView(view)} has references that could not be resolved, so whether it tracks the model is unknown — it was not checked.",
                    Severity = "low",
                    SuggestedFix = new Dictionary<string, object?> { ["provenance"] = "unknown" },
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// One issue standing in for a view whose dimensions are (almost) all
    /// drafted. Two wordings, not one, because "every" and "946 of 950" are
    /// different claims a reader shouldn't have to open the model to tell apart.
    /// </summary>
    private static Issue ViewRollupIssue(ViewInfo view, List<DimensionInfo> dims, List<DimensionInfo> drafted, RuleConfig config)
    {
        var fullyDrafted = drafted.Count == dims.Count;
        string subject;
        string verb;
        if (fullyDrafted)
        {
            subject = $"Every dimension in {DimensionDescriptions.DescribeView(view)} ({drafted.Count} of them)";
            verb = "is";
        }
        else
        {
            subject = $"{drafted.Count} of {dims.Count} dimensions in {DimensionDescriptions.DescribeView(view)}";
            verb = "are";
        }

        string summary;
        if (ViewScoping.IsUnlinkedDraftingView(view))
        {
            summary =
                $"{subject} {verb} taken from detail linework. A drafting view has no model behind it, so " +
                "that is expected for a standard detail — but if this view carries project-specific setout, " +
                "nothing in the file can show whether it has drifted.";
        }
        else
        {
            var scope = fullyDrafted ? "this view" : "that part of the view";
            summary =
                $"{subject} {verb} taken from detail linework. Nothing in {scope} tracks the model, and " +
                "nothing in the file can show whether it has drifted — it can only be verified against the " +
                "model itself.";
        }

        return new Issue
        {
            RuleId = RuleId,
            Category = "geometry",
            ElementId = view.ElementId,
            ViewId = view.ElementId,
            ViewName = view.Name,
            SheetNo = view.SheetNo,
            UniqueId = view.SheetUniqueId ?? view.UniqueId,
            Description = summary,
            Severity = DimensionDescriptions.DraftedSeverity(view, config),
            SuggestedFix = new Dictionary<string, object?>
            {
                ["provenance"] = "drafted",
                ["dimensions"] = dims.Count,
                ["drafted_dimensions"] = drafted.Count,
                ["scope"] = "view",
                ["action"] = "verify this view's setout against the model",
            },
        };
    }
}
