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

    public string DefaultSeverity { get; init; } = "medium";

    public Dictionary<string, string> SeverityByField { get; init; } = new();

    public string SeverityFor(string fieldName) =>
        SeverityByField.TryGetValue(fieldName, out var severity) ? severity : DefaultSeverity;
}
