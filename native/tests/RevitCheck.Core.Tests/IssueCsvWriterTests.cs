using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;
using Xunit;

namespace RevitCheck.Core.Tests;

public class IssueCsvWriterTests
{
    [Fact]
    public void WritesHeaderAndOneRowPerIssue()
    {
        var issues = new List<Issue>
        {
            new()
            {
                RuleId = "revitcheck.metadata_reconciliation",
                Category = "metadata",
                Severity = "medium",
                ElementId = 42,
                Description = "location: model says 'A', spreadsheet says 'B' (key=K1)",
                SuggestedFix = new Dictionary<string, object?> { ["field"] = "location", ["model_value"] = "A", ["csv_value"] = "B" },
            },
        };

        var csv = IssueCsvWriter.ToCsv(issues);
        var lines = csv.TrimEnd().Split('\n');

        Assert.Equal(2, lines.Length); // header + one data row
        Assert.Contains("IssueId", lines[0]);
        Assert.Contains("Field", lines[0]);
        Assert.Contains("42", lines[1]);
        Assert.Contains("location", lines[1]);
    }

    [Fact]
    public void FlattensAffectedElementIdsToASemicolonSeparatedCell()
    {
        var issues = new List<Issue>
        {
            new()
            {
                RuleId = "revitcheck.metadata_reconciliation",
                Category = "metadata",
                Severity = "medium",
                Description = "3 elements all show location: model says 'A', spreadsheet says 'B': 1, 2, 3",
                SuggestedFix = new Dictionary<string, object?>
                {
                    ["field"] = "location",
                    ["model_value"] = "A",
                    ["csv_value"] = "B",
                    ["affected_element_count"] = 3,
                    ["affected_element_ids"] = new List<long> { 1, 2, 3 },
                },
            },
        };

        var csv = IssueCsvWriter.ToCsv(issues);

        Assert.Contains("1; 2; 3", csv);
        Assert.Contains(",3,", csv); // affected_element_count
    }

    [Fact]
    public void CoverageIssueWithNoSuggestedFix_WritesBlankFieldColumns()
    {
        var issues = new List<Issue>
        {
            new()
            {
                RuleId = "revitcheck.metadata_reconciliation",
                Category = "coverage",
                Severity = "medium",
                Description = "30 element(s) have no value for the key parameter...",
            },
        };

        // Should not throw despite SuggestedFix being null.
        var csv = IssueCsvWriter.ToCsv(issues);

        Assert.Contains("coverage", csv);
    }

    [Fact]
    public void EmptyIssueList_WritesJustTheHeader()
    {
        var csv = IssueCsvWriter.ToCsv(new List<Issue>());

        var lines = csv.TrimEnd().Split('\n');
        Assert.Single(lines);
        Assert.Contains("IssueId", lines[0]);
    }
}
