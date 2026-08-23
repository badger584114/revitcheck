using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Coverage reporting for the extraction step itself - a near-literal port
/// of <c>checks/coverage.py</c>. A rule, not a report-formatting detail: the
/// adapter isolates per-element extraction failures so one unreadable
/// element can't abort a whole capture, but that means a capture that
/// silently dropped a chunk of the model would otherwise be indistinguishable
/// from a clean one. This travels with the issue list wherever it goes,
/// instead of depending on whoever formats the output remembering to print it.
/// </summary>
public static class CaptureCoverageCheck
{
    public const string RuleId = "revitcheck.capture_coverage";

    // Long error lists are usually one repeated cause. Show enough to
    // recognise it, not enough to bury the real findings.
    private const int MaxListed = 5;

    public static List<Issue> Run(RevitModel model)
    {
        var issues = new List<Issue>();

        if (model.ExtractionErrors.Count > 0)
        {
            var listed = model.ExtractionErrors.Take(MaxListed).ToList();
            var remainder = model.ExtractionErrors.Count - listed.Count;
            var detail = string.Join("; ", listed);
            if (remainder > 0)
            {
                detail += $" (+{remainder} more)";
            }

            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "medium",
                Description =
                    $"{model.ExtractionErrors.Count} element(s) could not be read from the model " +
                    $"and were not checked: {detail}",
            });
        }

        if (model.ExcludedWorksets.Count > 0)
        {
            var listed = model.ExcludedWorksets.Take(MaxListed).ToList();
            var remainder = model.ExcludedWorksets.Count - listed.Count;
            var names = string.Join(", ", listed);
            if (remainder > 0)
            {
                names += $" (+{remainder} more)";
            }

            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "low",
                Description =
                    $"{model.ExcludedWorksets.Count} workset(s) were excluded from this capture by " +
                    "user selection and nothing on them was checked - not because the model is clean " +
                    $"there, but because it wasn't looked at: {names}",
            });
        }

        return issues;
    }
}
