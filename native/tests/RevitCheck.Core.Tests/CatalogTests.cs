using RevitCheck.Core.Catalog;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void DuplicateRegister_Throws()
    {
        var catalog = new Catalog.Catalog();
        catalog.Register("test.rule", _ => new List<Issue>());

        Assert.Throws<InvalidOperationException>(() => catalog.Register("test.rule", _ => new List<Issue>()));
    }

    [Fact]
    public void UnknownRuleIdInConfig_IsANoOp_NotAnError()
    {
        var catalog = new Catalog.Catalog();
        catalog.Register("test.real_rule", _ => new List<Issue> { new() { RuleId = "test.real_rule", Category = "test", Description = "found" } });

        var issues = catalog.RunChecks(RevitCheckTestBuilders.Model(), new[] { "test.real_rule", "test.not_built_yet" });

        Assert.Single(issues);
    }

    [Fact]
    public void AThrowingRule_BecomesACoverageIssue_AndDoesNotSuppressOtherRules()
    {
        var catalog = new Catalog.Catalog();
        catalog.Register("test.throws", _ => throw new InvalidOperationException("boom"));
        catalog.Register("test.good", _ => new List<Issue> { new() { RuleId = "test.good", Category = "test", Description = "found something" } });

        var issues = catalog.RunChecks(RevitCheckTestBuilders.Model());

        Assert.Equal(2, issues.Count);
        var failure = issues.Single(i => i.RuleId == "test.throws");
        Assert.Equal("coverage", failure.Category);
        Assert.Equal("high", failure.Severity);
        Assert.Contains("boom", failure.Description);
        Assert.Contains(issues, i => i.RuleId == "test.good");
    }

    [Fact]
    public void NullEnabledRuleIds_RunsEveryRegisteredRule()
    {
        var catalog = new Catalog.Catalog();
        catalog.Register("test.a", _ => new List<Issue> { new() { RuleId = "test.a", Category = "test", Description = "a" } });
        catalog.Register("test.b", _ => new List<Issue> { new() { RuleId = "test.b", Category = "test", Description = "b" } });

        var issues = catalog.RunChecks(RevitCheckTestBuilders.Model());

        Assert.Equal(2, issues.Count);
    }
}
