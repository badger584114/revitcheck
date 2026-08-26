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

    // --- revitcheck.pile_model_schedule_consistency ---
    //
    // Model-vs-schedule pile setout: compares each pile element's own real,
    // LIVE Easting/Northing (ElementMetadata.ProjectPositionEastingMm/
    // NorthingMm - computed fresh every capture via GetProjectPosition, see
    // that field's own remarks) against the pile schedule's row for that
    // same pile, catching the case the user named directly - someone moves
    // a pile in the model, nobody reruns the Dynamo script that (re)writes
    // the schedule, the schedule silently goes stale relative to the model.
    // Deliberately does NOT compare against this project's own XYZ_Easting/
    // XYZ_Northing parameters - confirmed by the user 2026-08-26 that those
    // are themselves Dynamo-written from the insertion point and are what
    // the schedule reads, so comparing one against the other would be
    // comparing the same stale value to itself: both sides freeze together
    // the moment a pile moves without a Dynamo rerun, which is exactly the
    // failure this check exists to catch. See PLANNING.md §14 for the full
    // correction and the real diagnostic run this is otherwise built from.

    /// <summary>Category name identifying a pile element (Revit's Category.Name, e.g. "Structural Foundations" on this project). Confirmed real 2026-08-26; kept configurable since a category name is display text, not a stable enum, and can vary by locale/template.</summary>
    public string PileCategoryName { get; init; } = "Structural Foundations";

    /// <summary>
    /// Instance parameter holding a pile's own site/tag id - the join key
    /// against the schedule's own id column. Confirmed real on this project
    /// as <c>DIT_SiteID</c> - per-project naming, same as a `Mark`
    /// convention elsewhere, hence configurable rather than hardcoded
    /// (mirrors <c>ParameterMapping.KeyParameterName</c>'s own reasoning).
    /// </summary>
    public string PileKeyParameterName { get; init; } = "DIT_SiteID";

    /// <summary>
    /// Candidate schedule header names for the pile schedule's own id
    /// column, first match wins - mirrors the old PDF/DWG pipeline's
    /// <c>ID_HEADER_CANDIDATES</c> (ARCHIVE-pdf-dwg.md), deliberately a
    /// candidate list with no bare catch-all rather than a cleverer
    /// heuristic, for the same reason that pipeline settled on one: only
    /// one real naming convention has been seen so far.
    /// </summary>
    public List<string> PileScheduleIdHeaders { get; init; } = new() { "SITE ID" };

    public List<string> PileScheduleEastingHeaders { get; init; } = new() { "EASTING (m)", "EASTING" };

    public List<string> PileScheduleNorthingHeaders { get; init; } = new() { "NORTHING (m)", "NORTHING" };

    /// <summary>
    /// Schedule text is real-world metres ("EASTING (m)"); the model
    /// parameter is already mm (IR convention). This is the only place
    /// converting the schedule's units in, so the mm-per-metre factor lives
    /// here rather than being repeated.
    /// </summary>
    public const double ScheduleMetresToMm = 1000.0;

    /// <summary>
    /// Flag beyond this delta between the pile's own LIVE position
    /// (GetProjectPosition, computed fresh every capture) and the
    /// schedule's row for it. All four real piles sampled 2026-08-26 agreed
    /// to well under 1mm against their schedule row (PLANNING.md §14) -
    /// that run compared GetProjectPosition to the schedule via the
    /// (Dynamo-written) XYZ_Easting/XYZ_Northing parameters, which were
    /// still correct because nobody had moved a pile since Dynamo last ran,
    /// not because that comparison shape is the right one going forward
    /// (see this class's remarks above - it isn't). So this default is a
    /// generous placeholder against real drift/staleness, not a tight
    /// figure calibrated against a known-bad case - no real stale example
    /// has been seen yet. Confirm against a real drifted case before
    /// tightening it.
    /// </summary>
    public double PileSetoutToleranceMm { get; init; } = 10.0;
}
