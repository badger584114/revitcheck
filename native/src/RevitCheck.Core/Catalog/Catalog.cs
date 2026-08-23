using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Catalog;

public delegate List<Issue> RuleFunc(RevitModel model);

/// <summary>
/// Rule registry and runner - the C# counterpart of Python's <c>catalog.py</c>.
/// </summary>
/// <remarks>
/// One deliberate deviation from the Python original, called out because it
/// matters: Python's <c>_CATALOG</c> is a single module-level dict, populated
/// by <c>@register</c> decorators as an import side effect. C# has no safe
/// equivalent to that (static constructors run lazily/non-deterministically,
/// which is not a safe substitute) - so registration here is explicit
/// (<see cref="Catalog.CheckRegistry.RegisterAll"/>) rather than import-time,
/// and <see cref="Catalog"/> itself is an instantiable class rather than a
/// static singleton, so tests can build an isolated registry per test instead
/// of sharing global mutable state across an entire test run.
///
/// Everything else mirrors the Python behaviour: duplicate registration
/// throws; a config naming a rule id the live registry doesn't carry is a
/// silent no-op, not an error (a rule not built yet); and a rule that throws
/// becomes a <c>category: "coverage"</c>, <c>severity: "high"</c> Issue
/// rather than aborting the whole run - PLANNING.md/CLAUDE.md's rule config
/// is meant to tolerate ids the live catalog doesn't (yet) carry, and one bad
/// rule taking down a button mid-review is exactly the failure mode this
/// isolation exists to avoid.
/// </remarks>
public sealed class Catalog
{
    private readonly Dictionary<string, RuleFunc> _registry = new();

    public void Register(string ruleId, RuleFunc func)
    {
        if (_registry.ContainsKey(ruleId))
        {
            throw new InvalidOperationException($"duplicate rule_id: {ruleId}");
        }

        _registry[ruleId] = func;
    }

    public IReadOnlyList<string> AllRuleIds() =>
        _registry.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Runs every enabled rule and returns the combined issues. Rules are
    /// isolated from one another - a rule that throws produces a coverage
    /// Issue and the run continues.
    /// </summary>
    /// <param name="enabledRuleIds">
    /// Null means "every registered rule" (resolved at run time, against
    /// whatever is registered right now - mirroring Python's
    /// <c>RuleConfig.resolved_rule_ids()</c> fix for the "config snapshot
    /// taken before a rule module was imported" bug).
    /// </param>
    public List<Issue> RunChecks(RevitModel model, IEnumerable<string>? enabledRuleIds = null)
    {
        var enabled = new HashSet<string>(enabledRuleIds ?? AllRuleIds());
        var issues = new List<Issue>();

        foreach (var ruleId in enabled.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!_registry.TryGetValue(ruleId, out var func))
            {
                // Not an error: a config may name a rule id the live
                // registry does not (yet) carry.
                continue;
            }

            try
            {
                issues.AddRange(func(model));
            }
            catch (Exception ex)
            {
                issues.Add(new Issue
                {
                    RuleId = ruleId,
                    Category = "coverage",
                    Severity = "high",
                    Description =
                        $"Rule '{ruleId}' failed to run ({ex.GetType().Name}: {ex.Message}) - " +
                        "anything it would have found is missing from these results.",
                });
            }
        }

        return issues;
    }
}
