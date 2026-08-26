using RevitCheck.Core.Checks;
using RevitCheck.Core.Csv;
using RevitCheck.Core.Mapping;

namespace RevitCheck.Core.Catalog;

/// <summary>
/// The explicit, load-bearing list of what's registered into a
/// <see cref="Catalog"/> - deliberately not import-time side effects (see
/// <see cref="Catalog"/>'s remarks). Callers (the Addin commands, the
/// MappingBuilder CLI, tests) build one <see cref="Catalog"/> and register
/// into it via this method rather than relying on any implicit wiring.
/// </summary>
public static class CheckRegistry
{
    /// <summary>Registers every rule that needs no per-run configuration.</summary>
    public static void RegisterAll(Catalog catalog)
    {
        catalog.Register(CaptureCoverageCheck.RuleId, CaptureCoverageCheck.Run);
    }

    /// <summary>
    /// Registers every rule from <see cref="RegisterAll(Catalog)"/> plus
    /// <c>revitcheck.metadata_reconciliation</c>, bound to the mapping/CSV
    /// data and behavioural config for this run. Resolving the mapping file
    /// and CSV into <see cref="ParameterMapping"/>/<see cref="CsvTable"/> is
    /// the caller's job - this method only wires already-loaded data into
    /// the catalog.
    /// </summary>
    public static void RegisterAll(Catalog catalog, ParameterMapping mapping, CsvTable csv, ReconciliationConfig? reconciliationConfig = null)
    {
        RegisterAll(catalog);
        var config = reconciliationConfig ?? new ReconciliationConfig();
        catalog.Register(MetadataReconciliationCheck.RuleId, model => MetadataReconciliationCheck.Run(model, mapping, csv, config));
    }

    /// <summary>Registers the ported dimension checks (revit.dimension_provenance, revit.dimension_override_consistency) plus revitcheck.pile_model_schedule_consistency, bound to a RuleConfig for this run.</summary>
    public static void RegisterAll(Catalog catalog, RuleConfig ruleConfig)
    {
        RegisterAll(catalog);
        catalog.Register(DimensionProvenanceCheck.RuleId, model => DimensionProvenanceCheck.Run(model, ruleConfig));
        catalog.Register(DimensionOverrideConsistencyCheck.RuleId, model => DimensionOverrideConsistencyCheck.Run(model, ruleConfig));
        catalog.Register(PileModelScheduleConsistencyCheck.RuleId, model => PileModelScheduleConsistencyCheck.Run(model, ruleConfig));
    }
}
