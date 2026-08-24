using RevitCheck.Core.Json;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// Guards the hand-rolled policy (see its own docstring for why it exists -
/// a real MissingMethodException on the Revit machine, System.Text.Json's
/// built-in JsonNamingPolicy.SnakeCaseLower being newer than what Revit's
/// process actually resolves at runtime) against ever silently diverging
/// from what it replaced. Every name here is an actual property this
/// project's schemas use today - a future field with an unusual shape
/// (an acronym run, a leading underscore, digits) should get a case added
/// here before trusting it round-trips through a real file on disk.
/// </summary>
public class SnakeCaseLowerNamingPolicyTests
{
    [Theory]
    [InlineData("SchemaVersion", "schema_version")]
    [InlineData("KeyParameterName", "key_parameter_name")]
    [InlineData("KeyCsvColumn", "key_csv_column")]
    [InlineData("ToleranceMm", "tolerance_mm")]
    [InlineData("CaseInsensitive", "case_insensitive")]
    [InlineData("RequireModelValue", "require_model_value")]
    [InlineData("DefaultParameter", "default_parameter")]
    [InlineData("CsvColumn", "csv_column")]
    [InlineData("RuleId", "rule_id")]
    [InlineData("SuggestedFix", "suggested_fix")]
    [InlineData("UniqueId", "unique_id")]
    [InlineData("IssueId", "issue_id")]
    [InlineData("DocTitle", "doc_title")]
    [InlineData("RevitVersion", "revit_version")]
    [InlineData("CapturedAt", "captured_at")]
    [InlineData("ExtractionErrors", "extraction_errors")]
    [InlineData("ExcludedWorksets", "excluded_worksets")]
    [InlineData("BuiltInCategory", "built_in_category")]
    public void MatchesTheBuiltInPolicyForEveryRealSchemaField(string clrName, string expectedJsonName)
    {
        Assert.Equal(expectedJsonName, SnakeCaseLowerNamingPolicy.Instance.ConvertName(clrName));

        // The built-in policy is still available here (this test project
        // targets net8.0, where it's real) - compare against it directly
        // rather than only against a hardcoded expectation, so this test
        // fails the moment the two implementations disagree on anything,
        // not just on the names currently listed above.
        Assert.Equal(System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(clrName), expectedJsonName);
    }
}
