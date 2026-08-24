using RevitCheck.Core.Checks;
using RevitCheck.Core.Csv;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Mapping;
using RevitCheck.Core.Reporting;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

public class IssueGroupingTests
{
    private static Issue MismatchIssue(long elementId, string field, string modelValue, string csvValue) => new()
    {
        RuleId = MetadataReconciliationCheck.RuleId,
        Category = "metadata",
        Severity = "medium",
        ElementId = elementId,
        Description = $"{field}: model says '{modelValue}', spreadsheet says '{csvValue}' (key=K{elementId})",
        SuggestedFix = new Dictionary<string, object?> { ["field"] = field, ["model_value"] = modelValue, ["csv_value"] = csvValue },
    };

    private static RevitModel ModelWithElements(params (long Id, string? Family, string? Type)[] elements) =>
        RevitCheckTestBuilders.Model(elements.Select(e =>
            RevitCheckTestBuilders.Element(e.Id, category: "Structural Foundations", familyName: e.Family, typeName: e.Type)));

    [Fact]
    public void CollapsesManyElementsWithSameFamilyTypeFieldAndValues()
    {
        var model = ModelWithElements((1, "Pile", "600dia"), (2, "Pile", "600dia"), (3, "Pile", "600dia"));
        var issues = new List<Issue>
        {
            MismatchIssue(1, "locationheirarchykey", "3FC", "3BDE"),
            MismatchIssue(2, "locationheirarchykey", "3FC", "3BDE"),
            MismatchIssue(3, "locationheirarchykey", "3FC", "3BDE"),
        };

        var grouped = IssueGrouping.GroupMetadataMismatches(model, issues);

        var issue = Assert.Single(grouped);
        Assert.Equal(3, issue.SuggestedFix!["affected_element_count"]);
        Assert.Equal(new List<long> { 1, 2, 3 }, issue.SuggestedFix["affected_element_ids"]);
        Assert.Contains("3 elements", issue.Description);
        Assert.Contains("Pile", issue.Description);
    }

    [Fact]
    public void DoesNotMergeAcrossDifferentValues()
    {
        var model = ModelWithElements((1, "Pile", "600dia"), (2, "Pile", "600dia"));
        var issues = new List<Issue>
        {
            MismatchIssue(1, "locationheirarchykey", "3FC", "3BDE"),
            MismatchIssue(2, "locationheirarchykey", "2; 2P; 3", "3BDE"), // different model value
        };

        var grouped = IssueGrouping.GroupMetadataMismatches(model, issues);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void DoesNotMergeAcrossDifferentFamilies()
    {
        var model = ModelWithElements((1, "Pile", "600dia"), (2, "Anchor Bolt", "M20"));
        var issues = new List<Issue>
        {
            MismatchIssue(1, "locationheirarchykey", "3FC", "3BDE"),
            MismatchIssue(2, "locationheirarchykey", "3FC", "3BDE"),
        };

        var grouped = IssueGrouping.GroupMetadataMismatches(model, issues);

        Assert.Equal(2, grouped.Count);
    }

    [Fact]
    public void SingleOccurrence_PassesThroughUnchanged_KeepsItsElementIdAnchor()
    {
        var model = ModelWithElements((1, "Pile", "600dia"));
        var original = MismatchIssue(1, "locationheirarchykey", "3FC", "3BDE");

        var grouped = IssueGrouping.GroupMetadataMismatches(model, new List<Issue> { original });

        var issue = Assert.Single(grouped);
        Assert.Same(original, issue);
        Assert.Equal(1, issue.ElementId);
    }

    [Fact]
    public void NonMetadataIssues_PassThroughUnchanged()
    {
        var model = ModelWithElements((1, "Pile", "600dia"));
        var coverageIssue = new Issue
        {
            RuleId = MetadataReconciliationCheck.RuleId,
            Category = "coverage",
            Description = "30 element(s) have no value for the key parameter...",
        };

        var grouped = IssueGrouping.GroupMetadataMismatches(model, new List<Issue> { coverageIssue });

        Assert.Same(coverageIssue, Assert.Single(grouped));
    }

    [Fact]
    public void TruncatesAffectedElementIdsListPastFive()
    {
        var elements = Enumerable.Range(1, 8).Select(i => ((long)i, (string?)"Pile", (string?)"600dia")).ToArray();
        var model = ModelWithElements(elements);
        var issues = Enumerable.Range(1, 8)
            .Select(i => MismatchIssue(i, "locationheirarchykey", "3FC", "3BDE"))
            .ToList();

        var grouped = IssueGrouping.GroupMetadataMismatches(model, issues);

        var issue = Assert.Single(grouped);
        Assert.Equal(8, issue.SuggestedFix!["affected_element_count"]);
        Assert.Contains("(+3 more)", issue.Description);
    }

    [Fact]
    public void EndToEnd_RealCheckOutputCollapsesToOneIssuePerSystematicError()
    {
        // Integration-style: run the real check against several elements
        // that share one systematic error, confirming the whole
        // Run -> Group pipeline (as MetadataReconciliationCommand actually
        // calls it) collapses them, not just the grouping function in
        // isolation.
        var mapping = new ParameterMapping
        {
            KeyParameterName = "Asset_ID",
            KeyCsvColumn = "Asset ID",
            Fields = new Dictionary<string, FieldMapping>
            {
                ["location"] = new() { Comparison = ComparisonType.ExactString, CsvColumn = "Location", DefaultParameter = "Location" },
            },
        };
        var csv = new CsvTable
        {
            Headers = new[] { "Asset ID", "Location" },
            Rows = Enumerable.Range(1, 4).Select(i => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
            {
                ["Asset ID"] = $"A{i}",
                ["Location"] = "3BDE",
            }).ToList(),
        };
        var elements = Enumerable.Range(1, 4).Select(i =>
        {
            var e = RevitCheckTestBuilders.Element(i, category: "Structural Foundations", familyName: "Pile", typeName: "600dia", keyValue: $"A{i}",
                parameters: new Dictionary<string, ParameterValue>
                {
                    ["Asset_ID"] = new() { StorageType = ParameterStorageType.String, RawString = $"A{i}", DisplayString = $"A{i}" },
                    ["Location"] = new() { StorageType = ParameterStorageType.String, RawString = "3FC", DisplayString = "3FC" },
                });
            return e;
        });
        var model = RevitCheckTestBuilders.Model(elements);

        var rawIssues = MetadataReconciliationCheck.Run(model, mapping, csv, new ReconciliationConfig());
        Assert.Equal(4, rawIssues.Count); // one per element, ungrouped - the check's own contract

        var grouped = IssueGrouping.GroupMetadataMismatches(model, rawIssues);
        var issue = Assert.Single(grouped);
        Assert.Equal(4, issue.SuggestedFix!["affected_element_count"]);
    }
}
