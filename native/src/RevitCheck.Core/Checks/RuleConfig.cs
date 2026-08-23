namespace RevitCheck.Core.Checks;

/// <summary>
/// Tolerances and scoping for the dimension checks - the config-not-code
/// object, ported from Python's <c>catalog.RuleConfig</c>. Deliberately
/// narrower than the Python original: "which rule ids are enabled" is
/// already <see cref="Catalog.Catalog.RunChecks"/>'s own parameter in this
/// port, so it doesn't need to live here too. Every field below is a
/// project-specific choice, never a fact the API hands you - see each
/// field's own remarks for the reasoning and, where one exists, the real
/// data it was calibrated against.
/// </summary>
public sealed class RuleConfig
{
    /// <summary>Only check views placed on a sheet. A dimension in an unplaced working view is never issued to anyone, and flagging it is how a check earns a reputation for noise.</summary>
    public bool SheetedViewsOnly { get; init; } = true;

    /// <summary>Skip a Drafting View's dimensions entirely unless it's linked to a section cut through the model (ViewInfo.LinkedToModelSection) - a standard detail was never going to track the model regardless of how many of its dimensions get individually flagged.</summary>
    public bool SkipUnlinkedDraftingViews { get; init; } = true;

    /// <summary>
    /// Skip every view on a sheet whose title contains one of these words
    /// (case-insensitive substring match). Confirmed against a real capture
    /// 2026-08-22: reinforcement/detailing sheets dimension bar spacing and
    /// cover to static linework as normal drafting practice, never intended
    /// to be live - flagging it "high" is technically correct and
    /// practically wrong, since it was never setout. Empty list checks
    /// every sheet regardless; add a keyword once a second convention turns
    /// up rather than guessing ahead of one.
    /// </summary>
    public List<string> ExcludedSheetTitleKeywords { get; init; } = new() { "reinforcement" };

    /// <summary>Severity for a drafted dimension in a view that could have been live (a section/plan) - the real drift risk.</summary>
    public string DraftedInModelViewSeverity { get; init; } = "high";

    /// <summary>Severity for a drafted dimension in a drafting view, where there was never a model behind it at all - a different, lesser finding, and must not read like the model-view case.</summary>
    public string DraftedInDraftingViewSeverity { get; init; } = "low";

    /// <summary>Severity for a dimension mixing model geometry and detail linework across its own witness points. Rarer and usually accidental.</summary>
    public string MixedProvenanceSeverity { get; init; } = "medium";

    // --- revit.dimension_override_consistency tolerance model ---
    //
    // Inherited from the parked PDF/DWG pipeline's rounding-grid design
    // (PLANNING.md §5), not calibrated against a real Revit model yet - the
    // coverage Issue the rule emits is what will make the first real run
    // say how much it actually checked.

    public double RoundingGridDefaultMm { get; init; } = 5.0;

    public double RoundingGridSetoutCriticalMm { get; init; } = 1.0;

    public double MeasurementEpsilonMm { get; init; } = 0.5;

    /// <summary>Dimension type names (Revit's DimensionType.Name) whose values are setout-critical and get the tighter grid. Empty by default - an unlisted type gets the default grid rather than a guess, and no client's naming is assumed.</summary>
    public List<string> SetoutCriticalTypeNames { get; init; } = new();

    public DimensionProvenanceOptions DimensionProvenance { get; init; } = new();
}
