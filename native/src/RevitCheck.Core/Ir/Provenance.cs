namespace RevitCheck.Core.Ir;

/// <summary>Where a dimension takes its measurement from.</summary>
/// <remarks>
/// Python's <c>ir.py</c> keeps this as plain string constants rather than
/// an enum specifically so a value survives a JSON round trip unchanged
/// under IronPython too - a CPython/IronPython compatibility concern that
/// doesn't apply here. A real C# enum round-trips fine through
/// <c>CaptureSerializer</c>'s existing <c>JsonStringEnumConverter</c>
/// (snake_case, matching the Python string values exactly), so this is a
/// deliberate, justified deviation from the literal port.
/// </remarks>
public enum Provenance
{
    /// <summary>Real model geometry - updates when the model does.</summary>
    Model,

    /// <summary>Grid, level or reference plane - also live.</summary>
    Datum,

    /// <summary>View-specific linework - will silently go stale.</summary>
    Drafted,

    /// <summary>Some of each; suspicious in its own right.</summary>
    Mixed,

    /// <summary>Reference could not be resolved.</summary>
    Unknown,
}
