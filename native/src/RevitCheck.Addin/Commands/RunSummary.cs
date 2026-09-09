using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// The shape every check command's dialog uses: what was looked at, what it
/// was compared against, what disagrees - then one line per indicator that
/// has something to say.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added 2026-09-09, per the user - the dialogs were "very confusing".</b>
/// They had each accumulated their own mixture of input counts, extraction
/// samples, the full config path with every setting it pinned, and
/// paragraphs explaining what a check does not do. The one thing a reader
/// wants - <i>which element is wrong, and by how much</i> - was the hardest
/// thing to find in any of them.
/// </para>
/// <para>
/// One implementation rather than four, deliberately. The bespoke version
/// in <c>PileModelScheduleConsistencyCommand</c> had drifted out of step
/// with its own check - it rebuilt the candidate-schedule list under a
/// stricter rule and could name none where the check had compared against
/// several - and four copies of this shape would drift the same way.
/// </para>
/// <para>
/// <b>Nothing is suppressed.</b> Every finding, coverage notes included, is
/// in the JSON/CSV/BCF written beside the model and in the checklist. What
/// changed is that a note is counted here and read there. The three
/// footnotes exist so CLAUDE.md's "report a coverage indicator, never fail
/// silently" still holds - each appears only when non-zero, so a clean run
/// stays short and anything worth knowing is never absent.
/// </para>
/// </remarks>
internal static class RunSummary
{
    /// <summary>The category a check uses for "this is wrong", as opposed to manual review or coverage.</summary>
    private static readonly string[] VerdictCategories = { "geometry", "metadata" };

    public static string Build(
        IEnumerable<string> headerLines,
        IReadOnlyList<Issue> issues,
        Func<Issue, string> describeVerdict,
        string verdictNoun,
        IReadOnlyList<string> extractionErrors,
        Document doc,
        List<string>? unresolvedCategories = null)
    {
        var lines = headerLines.ToList();

        var verdicts = issues.Where(IsVerdict).ToList();

        lines.Add(string.Empty);
        if (verdicts.Count == 0)
        {
            lines.Add($"No {verdictNoun} mismatches.");
        }
        else
        {
            lines.Add($"{verdicts.Count} {verdictNoun}(s) mismatched:");
            lines.AddRange(verdicts.Select(describeVerdict));
        }

        var footnotes = new List<string>();

        // Manual review and coverage are counted apart, not lumped: one
        // says a person has to decide, the other says nothing could be
        // decided. Collapsing them is the distinction §21 and §22 were
        // spent establishing.
        var manualReview = issues.Count(i => string.Equals(
            i.Category, InvestigationReconciliation.ManualReviewCategory, StringComparison.OrdinalIgnoreCase));
        if (manualReview > 0)
        {
            footnotes.Add($"{manualReview} item(s) need a person to look - see the results file.");
        }

        var coverage = issues.Count(i => string.Equals(
            i.Category, InvestigationReconciliation.CoverageCategory, StringComparison.OrdinalIgnoreCase));
        if (coverage > 0)
        {
            footnotes.Add($"{coverage} note(s) on what could not be checked - see the results file.");
        }

        // Extraction errors reach no Issue on these commands' paths, so
        // this is the only place they appear at all - which is why the
        // messages themselves come too, but only when there are any. A
        // bare count would leave a real extraction failure undiagnosable;
        // printing the block unconditionally is what made these dialogs
        // long in the first place.
        if (extractionErrors.Count > 0)
        {
            footnotes.Add(
                $"{extractionErrors.Count} element(s) could not be read at all." +
                ExtractionErrorSample.Format(extractionErrors));
        }

        var pinned = PinnedSettingCount(doc);
        if (pinned > 0)
        {
            footnotes.Add(
                $"This model's config pins {pinned} setting(s) away from the built-in defaults - " +
                "Rule Config shows which.");
        }

        if (footnotes.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(footnotes);
        }

        return string.Join("\n", lines) +
               (unresolvedCategories is null ? string.Empty : CategoryScope.Note(unresolvedCategories));
    }

    private static bool IsVerdict(Issue issue) =>
        VerdictCategories.Contains(issue.Category, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many settings this model's config holds away from the current
    /// defaults - worth one line when non-zero, since a config pinning a
    /// superseded tolerance is invisible otherwise and silently decided two
    /// real runs (PLANNING.md §22, §23).
    /// </summary>
    private static int PinnedSettingCount(Document doc)
    {
        try
        {
            var json = RuleConfigSource.ReadRaw(doc);
            return json is null ? 0 : RuleConfigSerializer.DescribeOverrides(json).Count;
        }
        catch
        {
            // Never let a reporting nicety fail the run that produced the
            // findings - Rule Config reports properly on this file anyway.
            return 0;
        }
    }

    /// <summary>A string field from an issue's own structured data, or null when absent.</summary>
    public static string? Text(Issue issue, string field) =>
        issue.SuggestedFix is not null &&
        issue.SuggestedFix.TryGetValue(field, out var raw) &&
        raw?.ToString() is { Length: > 0 } value
            ? value
            : null;

    /// <summary>
    /// An element as a reviewer refers to it: its mark when it has one,
    /// always its ElementId - the number they type into Select by ID.
    /// </summary>
    public static string Label(Issue issue, string keyField = "pile_key")
    {
        var key = Text(issue, keyField);
        return $"{(key is null ? string.Empty : key + " ")}({issue.ElementId?.ToString() ?? "no element"})";
    }

    /// <summary>A numeric field from an issue's own structured data, formatted, or empty when absent.</summary>
    public static string Amount(Issue issue, string field, string unit, string format = "0.#")
    {
        if (issue.SuggestedFix is null ||
            !issue.SuggestedFix.TryGetValue(field, out var raw) ||
            raw is null ||
            !double.TryParse(
                raw.ToString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            return string.Empty;
        }

        return $" - {value.ToString(format, System.Globalization.CultureInfo.InvariantCulture)}{unit}";
    }
}
