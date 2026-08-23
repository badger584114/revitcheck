using RevitCheck.Core.Issues;
using Xunit;

namespace RevitCheck.Core.Tests;

public class IssueIdentityTests
{
    private static Issue Make(string ruleId = "revitcheck.metadata_reconciliation", long? elementId = 1,
        string description = "girder_depth_mm: model says '445', spreadsheet says '450' (key=ASSET-001)",
        string severity = "medium", Dictionary<string, object?>? suggestedFix = null, string? uniqueId = null) =>
        new()
        {
            RuleId = ruleId,
            Category = "metadata",
            Description = description,
            Severity = severity,
            ElementId = elementId,
            SuggestedFix = suggestedFix,
            UniqueId = uniqueId,
        };

    [Fact]
    public void SameIdentityFields_ProduceSameIssueId()
    {
        Assert.Equal(Make().IssueId, Make().IssueId);
    }

    [Fact]
    public void DifferentElementId_ProducesDifferentIssueId()
    {
        Assert.NotEqual(Make(elementId: 1).IssueId, Make(elementId: 2).IssueId);
    }

    [Fact]
    public void SeverityIsExcludedFromTheHash()
    {
        Assert.Equal(Make(severity: "medium").IssueId, Make(severity: "high").IssueId);
    }

    [Fact]
    public void SuggestedFixIsExcludedFromTheHash()
    {
        var withFix = Make(suggestedFix: new Dictionary<string, object?> { ["csv_value"] = "450" });
        var withoutFix = Make(suggestedFix: null);

        Assert.Equal(withFix.IssueId, withoutFix.IssueId);
    }

    [Fact]
    public void UniqueIdIsExcludedFromTheHash()
    {
        Assert.Equal(Make(uniqueId: "guid-1").IssueId, Make(uniqueId: null).IssueId);
    }

    [Fact]
    public void IssueIdIsTwelveLowercaseHexCharacters()
    {
        Assert.Matches("^[0-9a-f]{12}$", Make().IssueId);
    }
}
