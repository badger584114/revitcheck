namespace RevitCheck.Addin.Commands;

/// <summary>
/// Formats a short, listed sample of <c>RevitModel.ExtractionErrors</c> for
/// a <c>TaskDialog</c> summary - added 2026-08-28 alongside the two pile
/// commands, whose extraction-error count on a real run (62, with 0
/// captured schedules where 2 real ones exist) turned out to be
/// undiagnosable from the count alone: nothing in this codebase surfaced
/// the actual error text anywhere before this, only a bare number. Same
/// "list a few, note how many more" shape
/// <c>PileModelScheduleConsistencyCheck.BuildBlankKeyIssue</c> already
/// uses for the same reason - a TaskDialog is not the place to dump
/// hundreds of lines, but a bare count that hides every message is not
/// useful either.
/// </summary>
internal static class ExtractionErrorSample
{
    private const int MaxListed = 5;

    /// <summary>Empty string if <paramref name="errors"/> is empty; otherwise a "\n\n" + listed sample block, ready to append straight onto a summary string.</summary>
    public static string Format(IReadOnlyList<string> errors)
    {
        if (errors.Count == 0)
        {
            return "";
        }

        var listed = errors.Take(MaxListed).ToList();
        var remainder = errors.Count - listed.Count;
        var lines = string.Join("\n", listed.Select(e => $"- {e}"));
        var more = remainder > 0 ? $"\n(+{remainder} more)" : "";
        return $"\n\nExtraction errors:\n{lines}{more}";
    }
}
