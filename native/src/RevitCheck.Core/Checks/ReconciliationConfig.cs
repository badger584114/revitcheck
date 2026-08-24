namespace RevitCheck.Core.Checks;

/// <summary>
/// Behavioural configuration for <see cref="MetadataReconciliationCheck"/>.
/// Deliberately holds only comparison behaviour, not file paths - resolving
/// a mapping file and a CSV into <c>ParameterMapping</c>/<c>CsvTable</c> is
/// the caller's job (the Addin command, the CLI, or a test fixture), so the
/// check itself stays a pure function over already-loaded data, the same
/// "adapter does I/O, everything below it doesn't" split the rest of this
/// codebase follows.
/// </summary>
/// <remarks>
/// This is a dedicated config, not an extension of a shared, not-yet-built
/// <c>RuleConfig</c> - inventing one prematurely for a feature that doesn't
/// need drafting-check-only knobs (tolerances, sheet-title keyword
/// exclusions, etc.) isn't worth the coupling.
/// </remarks>
public sealed class ReconciliationConfig
{
    /// <summary>
    /// Report an element that has the key parameter set but no matching CSV
    /// row. On by default: this is the "missing item" signal that's this
    /// tool's core job on a small, fully-expected-to-be-complete model - not
    /// an edge case, confirmed directly by the user over the "future client"
    /// framing this was first drafted against.
    /// </summary>
    public bool ReportUnmatchedModelElements { get; init; } = true;

    /// <summary>
    /// Key-parameter values (case-insensitive, trimmed) that mean "this
    /// element intentionally has no key", treated exactly like a blank key
    /// rather than looked up in the CSV. Found necessary from a real run
    /// 2026-08-24: elements whose key parameter literally held the text
    /// "N/A" (the same not-applicable convention already known from
    /// RequireModelValue's own history) were being searched for as a real
    /// key and reported as "no matching row found" - 30 false "missing
    /// item" issues on an already-audited model. "N/A" is the one
    /// convention actually seen so far; a client using a different sentinel
    /// (e.g. "-", "None") would need it added here rather than assumed.
    /// </summary>
    public HashSet<string> BlankKeySentinels { get; init; } = new(StringComparer.OrdinalIgnoreCase) { "N/A" };

    public string DefaultSeverity { get; init; } = "medium";

    public Dictionary<string, string> SeverityByField { get; init; } = new();

    public string SeverityFor(string fieldName) =>
        SeverityByField.TryGetValue(fieldName, out var severity) ? severity : DefaultSeverity;
}
