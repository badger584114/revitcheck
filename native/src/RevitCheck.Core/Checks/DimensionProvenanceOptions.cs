namespace RevitCheck.Core.Checks;

/// <summary>
/// The two ad hoc knobs Python's <c>RuleConfig.params["dimension_provenance"]</c>
/// dict carries. Given a small typed object instead of replicating Python's
/// untyped <c>Dict[str, dict]</c> escape hatch, which exists there only
/// because nothing else in that codebase needed a per-rule options bag badly
/// enough to justify a real field.
/// </summary>
public sealed class DimensionProvenanceOptions
{
    /// <summary>Roll a view's dimensions up into one issue once RollupThreshold of them are drafted, instead of reporting each individually.</summary>
    public bool RollUpFullyDraftedViews { get; init; } = true;

    /// <summary>
    /// Fraction of a view's dimensions that must classify as Drafted before
    /// it rolls up. Calibrated against the first real capture
    /// (T2DPAA-T2D-C3S-BR-M3D-100304, 2026-08-21): requiring literally every
    /// dimension to be Drafted let one elevation view with 946 drafted and 4
    /// model-backed dimensions fall through to 946 individual issues for a
    /// view whose story is one sentence long. At 0.9 the same run's volume
    /// dropped from 4434 to 1042 issues with no finding actually lost.
    /// </summary>
    public double RollupThreshold { get; init; } = 0.9;
}
