using RevitCheck.Core.Checks;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

public class CaptureCoverageCheckTests
{
    [Fact]
    public void CleanCapture_ReportsNothing()
    {
        var issues = CaptureCoverageCheck.Run(RevitCheckTestBuilders.Model());

        Assert.Empty(issues);
    }

    [Fact]
    public void ExtractionErrors_SurfaceAsAMediumSeverityIssue()
    {
        var model = RevitCheckTestBuilders.Model(extractionErrors: new List<string> { "element 42: parameter read failed" });

        var issues = CaptureCoverageCheck.Run(model);

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Equal("medium", issue.Severity);
        Assert.Contains("1 element(s)", issue.Description);
    }

    [Fact]
    public void ExcludedWorksets_SurfaceAsALowSeverityIssue()
    {
        var model = RevitCheckTestBuilders.Model(excludedWorksets: new List<string> { "Roads" });

        var issues = CaptureCoverageCheck.Run(model);

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Equal("low", issue.Severity);
        Assert.Contains("Roads", issue.Description);
    }

    [Fact]
    public void ExtractionErrorsAndExcludedWorksets_AreSeparateIssues()
    {
        var model = RevitCheckTestBuilders.Model(
            extractionErrors: new List<string> { "boom" },
            excludedWorksets: new List<string> { "Roads" });

        var issues = CaptureCoverageCheck.Run(model);

        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void LongErrorLists_Truncate()
    {
        var errors = Enumerable.Range(1, 8).Select(i => $"element {i}: failed").ToList();
        var model = RevitCheckTestBuilders.Model(extractionErrors: errors);

        var issue = Assert.Single(CaptureCoverageCheck.Run(model));

        Assert.Contains("8 element(s)", issue.Description);
        Assert.Contains("(+3 more)", issue.Description);
        Assert.DoesNotContain("element 6:", issue.Description);
    }
}
