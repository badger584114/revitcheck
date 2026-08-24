using System.Globalization;
using CsvHelper;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Reporting;

/// <summary>
/// Issue-list -&gt; CSV writer, alongside <see cref="IssueJsonWriter"/> -
/// added 2026-08-24 at the user's request, for reviewing/filtering findings
/// in a spreadsheet rather than reading JSON by hand. Backed by CsvHelper
/// (already a dependency, used for reading reference CSVs) for the same
/// real-world-quoting robustness reason <c>Csv/CsvReader.cs</c> gives.
/// </summary>
/// <remarks>
/// <see cref="Issue.SuggestedFix"/> is a free-form <c>Dictionary&lt;string,
/// object?&gt;</c> whose keys vary by rule - not something a fixed set of
/// CSV columns can represent in general. Rather than guess at every rule's
/// shape, this flattens only the keys <c>MetadataReconciliationCheck</c>
/// and <see cref="IssueGrouping"/> actually use today (<c>field</c>,
/// <c>model_value</c>, <c>csv_value</c>, <c>affected_element_count</c>,
/// <c>affected_element_ids</c>) - any other key is silently dropped from
/// the CSV (still present in the JSON export, which stays the complete,
/// lossless record). Revisit if a second rule's SuggestedFix shape needs
/// representing here too.
/// </remarks>
public static class IssueCsvWriter
{
    private sealed class Row
    {
        public string IssueId { get; init; } = "";
        public string RuleId { get; init; } = "";
        public string Category { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Description { get; init; } = "";
        public long? ElementId { get; init; }
        public string? UniqueId { get; init; }
        public long? ViewId { get; init; }
        public string? ViewName { get; init; }
        public string? SheetNo { get; init; }
        public string? Field { get; init; }
        public string? ModelValue { get; init; }
        public string? CsvValue { get; init; }
        public int? AffectedElementCount { get; init; }
        public string? AffectedElementIds { get; init; }
    }

    public static string ToCsv(IReadOnlyList<Issue> issues)
    {
        var sorted = IssueSorting.SortIssues(issues);
        var rows = sorted.Select(issue =>
        {
            var fix = issue.SuggestedFix;
            return new Row
            {
                IssueId = issue.IssueId,
                RuleId = issue.RuleId,
                Category = issue.Category,
                Severity = issue.Severity,
                Description = issue.Description,
                ElementId = issue.ElementId,
                UniqueId = issue.UniqueId,
                ViewId = issue.ViewId,
                ViewName = issue.ViewName,
                SheetNo = issue.SheetNo,
                Field = StringField(fix, "field"),
                ModelValue = StringField(fix, "model_value"),
                CsvValue = StringField(fix, "csv_value"),
                AffectedElementCount = IntField(fix, "affected_element_count"),
                AffectedElementIds = ListField(fix, "affected_element_ids"),
            };
        });

        using var stringWriter = new StringWriter();
        using (var csv = new CsvWriter(stringWriter, CultureInfo.InvariantCulture))
        {
            csv.WriteRecords(rows);
        }

        return stringWriter.ToString();
    }

    public static string Write(IReadOnlyList<Issue> issues, string path)
    {
        File.WriteAllText(path, ToCsv(issues));
        return path;
    }

    private static string? StringField(Dictionary<string, object?>? fix, string key) =>
        fix is not null && fix.TryGetValue(key, out var value) ? value as string : null;

    private static int? IntField(Dictionary<string, object?>? fix, string key)
    {
        if (fix is null || !fix.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            _ => int.TryParse(value.ToString(), out var parsed) ? parsed : null,
        };
    }

    private static string? ListField(Dictionary<string, object?>? fix, string key)
    {
        if (fix is null || !fix.TryGetValue(key, out var value) || value is not System.Collections.IEnumerable list || value is string)
        {
            return null;
        }

        var items = list.Cast<object?>().Select(v => v?.ToString() ?? "");
        return string.Join("; ", items);
    }
}
