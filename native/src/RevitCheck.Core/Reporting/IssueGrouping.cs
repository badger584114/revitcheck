using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Reporting;

/// <summary>
/// Collapses many metadata-reconciliation mismatch issues that share the
/// same (category, family, type, field, model value, csv value) into one,
/// with an affected-element count and a truncated sample of ids - the same
/// "a wholly-drafted view is one finding, not twenty" precedent
/// <c>revit.dimension_provenance</c> already established, applied to this
/// check's own shape: many physically different elements that happen to
/// carry an identical systematic error (real example, 2026-08-24: a
/// cluster of piles all showing the same stale location code). Confirmed
/// by the user as a requirement for output/reporting work.
/// </summary>
/// <remarks>
/// Deliberately a separate reporting step, not built into
/// <see cref="MetadataReconciliationCheck.Run"/> itself - the check's own
/// contract stays "one authoritative issue per (element, field) finding",
/// which is what every existing test already assumes and what an
/// element-anchored consumer (Select by ID, a future BCF export) still
/// needs for the single-occurrence case. Grouping is something applied on
/// top for a human-facing report, not a change to what the check itself
/// found.
/// </remarks>
public static class IssueGrouping
{
    private const int MaxListedElements = 5;

    public static List<Issue> GroupMetadataMismatches(RevitModel model, IReadOnlyList<Issue> issues)
    {
        var passthrough = new List<Issue>();
        var buckets = new Dictionary<GroupKey, List<Issue>>();

        foreach (var issue in issues)
        {
            var key = TryBuildGroupKey(model, issue);
            if (key is null)
            {
                // Not a groupable mismatch (a coverage note, an issue with
                // no single element, or missing the structured data
                // grouping needs) - passes through unchanged.
                passthrough.Add(issue);
                continue;
            }

            if (!buckets.TryGetValue(key.Value, out var bucket))
            {
                bucket = new List<Issue>();
                buckets[key.Value] = bucket;
            }

            bucket.Add(issue);
        }

        var result = new List<Issue>(passthrough);
        foreach (var entry in buckets)
        {
            // A group of one is just the original issue - nothing to
            // collapse, and collapsing it would only lose its ElementId
            // anchor for no benefit.
            result.Add(entry.Value.Count == 1 ? entry.Value[0] : BuildGroupedIssue(entry.Key, entry.Value));
        }

        return result;
    }

    private static GroupKey? TryBuildGroupKey(RevitModel model, Issue issue)
    {
        if (issue.RuleId != MetadataReconciliationCheck.RuleId ||
            issue.Category != "metadata" ||
            issue.ElementId is not { } elementId ||
            issue.SuggestedFix is not { } fix)
        {
            return null;
        }

        if (!fix.TryGetValue("field", out var fieldObj) || fieldObj is not string field)
        {
            return null;
        }

        var element = model.ElementById(elementId);
        if (element is null)
        {
            // Shouldn't happen (the issue came from checking this exact
            // model), but if it does, don't guess at a grouping key -
            // pass the issue through on its own.
            return null;
        }

        var modelValue = fix.TryGetValue("model_value", out var mv) ? mv as string : null;
        var csvValue = fix.TryGetValue("csv_value", out var cv) ? cv as string : null;

        return new GroupKey(element.Category, element.FamilyName, element.TypeName, field, modelValue, csvValue);
    }

    private static Issue BuildGroupedIssue(GroupKey key, List<Issue> bucket)
    {
        var first = bucket[0];

        var elementIds = bucket
            .Where(i => i.ElementId is not null)
            .Select(i => i.ElementId!.Value)
            .OrderBy(id => id)
            .ToList();
        var listed = elementIds.Take(MaxListedElements).ToList();
        var remainder = elementIds.Count - listed.Count;
        var idsText = string.Join(", ", listed);
        if (remainder > 0)
        {
            idsText += $" (+{remainder} more)";
        }

        var what = string.Join(" / ", new[] { key.Category, key.FamilyName, key.TypeName }
            .Where(s => !string.IsNullOrEmpty(s)));

        return new Issue
        {
            RuleId = first.RuleId,
            Category = first.Category,
            Severity = first.Severity,
            Description =
                $"{bucket.Count} elements ({what}) all show {key.Field}: model says " +
                $"'{key.ModelValue}', spreadsheet says '{key.CsvValue}': {idsText}",
            SuggestedFix = new Dictionary<string, object?>
            {
                ["field"] = key.Field,
                ["model_value"] = key.ModelValue,
                ["csv_value"] = key.CsvValue,
                ["affected_element_count"] = bucket.Count,
                ["affected_element_ids"] = elementIds,
            },
        };
    }

    private readonly record struct GroupKey(
        string? Category, string? FamilyName, string? TypeName, string Field, string? ModelValue, string? CsvValue);
}
