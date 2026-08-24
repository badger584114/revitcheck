using System.Text.Json;
using System.Text.Json.Serialization;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Reporting;

/// <summary>
/// Minimal issue-list -&gt; JSON writer - the one output shape a command
/// needs to hand something back to the user right now. Not a port of the
/// Python side's <c>report.py</c> (summarize/to_json/to_markdown/to_bcf) -
/// that's a bigger surface than the first wired-up button needs, and
/// growing it here speculatively would be guessing ahead of a second
/// caller, which this project's own working conventions warn against.
/// </summary>
public static class IssueJsonWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string ToJson(IReadOnlyList<Issue> issues)
    {
        var sorted = IssueSorting.SortIssues(issues);
        var rows = sorted.Select(issue => new
        {
            issue_id = issue.IssueId,
            rule_id = issue.RuleId,
            category = issue.Category,
            severity = issue.Severity,
            description = issue.Description,
            element_id = issue.ElementId,
            unique_id = issue.UniqueId,
            view_id = issue.ViewId,
            view_name = issue.ViewName,
            sheet_no = issue.SheetNo,
            suggested_fix = issue.SuggestedFix,
        });

        return JsonSerializer.Serialize(new { count = sorted.Count, issues = rows }, Options);
    }

    public static string Write(IReadOnlyList<Issue> issues, string path)
    {
        File.WriteAllText(path, ToJson(issues));
        return path;
    }
}
