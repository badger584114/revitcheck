using RevitCheck.Core.Capture;
using RevitCheck.Core.Catalog;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// Runs the ported dimension checks against the one real capture committed
/// to the repo (samples/T2DPAA-T2D-C3S-BR-M3D-100304_Peter.capture.json) -
/// PLANNING.md §12's own suggestion for how this file earns its keep as a
/// C#-side test fixture. This is the strongest available check against the
/// translation risk (float formatting, iteration order, string handling)
/// §12 names: real data, not another synthetic scenario.
///
/// The exact counts below were verified once (2026-08-23) against the
/// Python engine's output on the same file
/// (`python3 scripts/check_capture.py &lt;file&gt; --json`) - 957 issues total,
/// 923 revit.dimension_provenance + 34 revit.dimension_override_consistency,
/// 869 high / 87 medium / 1 low, and the full issue_id set matched exactly
/// (a SHA-256 hash over rule/element/view/sheet/description, so an exact
/// match means the two engines agree on every individual finding, not just
/// the totals). If this test ever fails, that's real - either a genuine
/// behavioural difference crept into the C# port, or the committed capture
/// changed, not a flaky test.
///
/// Re-verified 2026-09-02 after a real, intentional behaviour change (both
/// engines, same commit): a Drafted or Mixed dimension carrying a typed
/// override that is not a stated MIN/MAX limit no longer gets its own
/// per-dimension <c>revit.dimension_provenance</c> issue - real confirmed
/// noise on this project (drg-2873061 section 1, dimensions
/// 8199083/9103358; see <see cref="Checks.DimensionProvenanceCheck"/>'s own
/// remarks). First landed as Drafted-only (943 total), then real data on
/// this exact capture showed both real examples classify Mixed, not
/// Drafted, so the suppression was extended to cover Mixed too. Final
/// counts: 928 total (894 provenance + 34 unchanged override-consistency,
/// 855 high / 72 medium / 1 low, 927 geometry + 1 coverage) - the medium
/// count (Mixed's own severity) is what moved on the second pass, high
/// unchanged from the Drafted-only pass. The fixture and the numbers below
/// were regenerated the same way as the original baseline, not hand-edited.
/// </summary>
public class RealCaptureParityTests
{
    private static string CapturePath()
    {
        // native/tests/RevitCheck.Core.Tests/bin/Debug/net8.0 -> repo root
        var dir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "samples", "T2DPAA-T2D-C3S-BR-M3D-100304_Peter.capture.json");
    }

    private static RevitModel LoadRealCapture()
    {
        var path = CapturePath();
        Assert.True(File.Exists(path), $"Real capture not found at {path} - has samples/ moved?");
        return CaptureSerializer.Load(path);
    }

    [Fact]
    public void LoadsWithTheKnownStructuralCounts()
    {
        var model = LoadRealCapture();

        Assert.Equal("T2DPAA-T2D-C3S-BR-M3D-100304_Peter.Griggs", model.DocTitle);
        Assert.Equal(134, model.Sheets.Count);
        Assert.Equal(1566, model.Views.Count);
        Assert.Equal(17117, model.Dimensions.Count);
        Assert.Equal("2024", model.RevitVersion);
    }

    [Fact]
    public void BothDimensionChecksMatchThePythonEngineExactly()
    {
        var model = LoadRealCapture();
        var config = new RuleConfig();

        var catalog = new Catalog.Catalog();
        CheckRegistry.RegisterAll(catalog, config);

        var issues = catalog.RunChecks(model, new[] { DimensionProvenanceCheck.RuleId, DimensionOverrideConsistencyCheck.RuleId });

        Assert.Equal(928, issues.Count);
        Assert.Equal(894, issues.Count(i => i.RuleId == DimensionProvenanceCheck.RuleId));
        Assert.Equal(34, issues.Count(i => i.RuleId == DimensionOverrideConsistencyCheck.RuleId));
        Assert.Equal(855, issues.Count(i => i.Severity == "high"));
        Assert.Equal(72, issues.Count(i => i.Severity == "medium"));
        Assert.Equal(1, issues.Count(i => i.Severity == "low"));
        Assert.Equal(927, issues.Count(i => i.Category == "geometry"));
        Assert.Equal(1, issues.Count(i => i.Category == "coverage"));
    }

    [Fact]
    public void EveryIssueIdMatchesThePythonEngineExactly()
    {
        // The strongest form of this check: not just matching aggregate
        // counts (which could coincidentally match even with some issues
        // differing) but every individual SHA-256 identity hash, against a
        // fixture captured once (2026-08-23) from the Python engine's own
        // issue_id set on this exact file
        // (`python3 scripts/check_capture.py <file> --json`, issue_ids
        // sorted). Two real bugs were found and fixed via this exact
        // comparison, not by any synthetic test: an em-dash silently ported
        // as a plain hyphen in several description strings, and a naive
        // string-quoting substitute for Python's repr() that would have
        // embedded a raw invisible Unicode character (a real override on
        // this capture is a lone U+200E) instead of escaping it visibly. If
        // this ever fails, that's real - either a genuine behavioural
        // difference crept into the C# port, or the committed capture
        // changed - not a flaky test.
        var expectedIds = File.ReadAllLines(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "real_capture_expected_issue_ids.txt"));

        var model = LoadRealCapture();
        var config = new RuleConfig();
        var catalog = new Catalog.Catalog();
        CheckRegistry.RegisterAll(catalog, config);
        var issues = catalog.RunChecks(model, new[] { DimensionProvenanceCheck.RuleId, DimensionOverrideConsistencyCheck.RuleId });

        var actualIds = issues.Select(i => i.IssueId).OrderBy(id => id, StringComparer.Ordinal);
        Assert.Equal(expectedIds.OrderBy(id => id, StringComparer.Ordinal), actualIds);
    }
}
