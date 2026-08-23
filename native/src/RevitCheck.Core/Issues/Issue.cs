using System.Security.Cryptography;
using System.Text;

namespace RevitCheck.Core.Issues;

/// <summary>
/// Near-literal port of Python's <c>issue.py</c>. <see cref="IssueId"/> is
/// derived, not stored - the same finding on the same model gets the same
/// id on every run, so a reviewer's selection survives a re-run. What feeds
/// the hash is what *identifies* the finding (rule, location, description) -
/// not <see cref="Severity"/> (re-tiering a rule in config must not
/// re-identify its findings) and not <see cref="SuggestedFix"/> or
/// <see cref="UniqueId"/> (rewording a fix, or a capture predating
/// <c>UniqueId</c>, must not either).
/// </summary>
public sealed class Issue
{
    public required string RuleId { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }

    /// <summary>"low" | "medium" | "high"</summary>
    public string Severity { get; init; } = "medium";

    /// <summary>The element to select and zoom to. Null only for a finding with no single element (e.g. a model-wide coverage warning).</summary>
    public long? ElementId { get; init; }

    public long? ViewId { get; init; }
    public string? ViewName { get; init; }
    public string? SheetNo { get; init; }

    public Dictionary<string, object?>? SuggestedFix { get; init; }

    /// <summary>Revit's Element.UniqueId, when the rule that built this Issue had one to hand. Not part of the identity hash - see class remarks.</summary>
    public string? UniqueId { get; init; }

    public string IssueId
    {
        get
        {
            var parts = string.Join(
                "\x1f",
                RuleId,
                ElementId?.ToString() ?? "",
                ViewId?.ToString() ?? "",
                SheetNo ?? "",
                Description);
            // SHA256.HashData / Convert.ToHexStringLower are both newer than
            // netstandard2.0's API surface - use the instance API + a manual
            // hex encode instead so Core keeps compiling for net48 too.
            using var sha256 = SHA256.Create();
            var digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(parts));
            var hex = new StringBuilder(digest.Length * 2);
            foreach (var b in digest)
            {
                hex.Append(b.ToString("x2"));
            }
            return hex.ToString(0, 12);
        }
    }
}

internal static class SeverityOrder
{
    private static readonly Dictionary<string, int> Order = new() { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };

    internal static int Rank(string severity) => Order.TryGetValue(severity, out var rank) ? rank : 99;
}

public static class IssueSorting
{
    /// <summary>
    /// Sheet-major, most severe first within each sheet - same ordering
    /// (and same reasoning: an engineer reviews a drawing set one sheet at
    /// a time) as Python's <c>sort_issues</c>. Findings with no sheet sort
    /// last rather than into the middle of the set.
    /// </summary>
    public static List<Issue> SortIssues(IEnumerable<Issue> issues) =>
        issues
            .OrderBy(i => i.SheetNo is null)
            .ThenBy(i => i.SheetNo ?? "", StringComparer.Ordinal)
            .ThenBy(i => SeverityOrder.Rank(i.Severity))
            .ThenBy(i => i.ViewName ?? "", StringComparer.Ordinal)
            .ThenBy(i => i.RuleId, StringComparer.Ordinal)
            .ToList();
}
