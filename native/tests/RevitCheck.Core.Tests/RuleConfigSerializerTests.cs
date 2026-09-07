using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// Covers the per-model config loop restored 2026-09-07: every command
/// built after metadata reconciliation used `new RuleConfig()`, so the
/// project's own "tolerances must be configurable, never hardcoded" rule
/// held only in the type system - a category name could not be changed for
/// a new model without rebuilding the add-in.
/// </summary>
public class RuleConfigSerializerTests
{
    [Fact]
    public void Round_trips_a_configured_value()
    {
        var config = new RuleConfig
        {
            PileCategoryName = "Generic Models",
            PileSetoutToleranceMm = 25.0,
        };

        var loaded = RuleConfigSerializer.Loads(RuleConfigSerializer.Dumps(config));

        Assert.Equal("Generic Models", loaded.PileCategoryName);
        Assert.Equal(25.0, loaded.PileSetoutToleranceMm);
    }

    [Fact]
    public void An_omitted_field_keeps_its_compiled_default()
    {
        // A project records only what it actually differs on, and a field
        // added in a later build doesn't invalidate an existing file.
        var loaded = RuleConfigSerializer.Loads("{\"pile_category_name\": \"Generic Models\"}");

        Assert.Equal("Generic Models", loaded.PileCategoryName);
        Assert.Equal(new RuleConfig().PileSetoutToleranceMm, loaded.PileSetoutToleranceMm);
    }

    [Fact]
    public void A_newer_schema_version_is_refused_rather_than_misread()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RuleConfigSerializer.Loads("{\"schema_version\": 999}"));

        Assert.Contains("refusing to misread", ex.Message);
    }

    [Fact]
    public void Starter_widens_setout_header_candidates_from_real_schedule_headings()
    {
        var model = RevitCheckTestBuilders.Model(schedules: new[]
        {
            new ScheduleInfo
            {
                Name = "PILE SETOUT",
                Headers = new List<string> { "PILE REF", "EASTING COORD", "NORTHING COORD" },
                Rows = new List<ScheduleRow>(),
            },
        });

        var result = RuleConfigStarter.Build(model);

        Assert.Contains("EASTING COORD", result.Config.PileScheduleEastingHeaders);
        Assert.Contains("NORTHING COORD", result.Config.PileScheduleNorthingHeaders);
        // Widening, never replacing - the defaults still apply to a model
        // that uses them.
        Assert.Contains("EASTING (m)", result.Config.PileScheduleEastingHeaders);
    }

    [Fact]
    public void Starter_reports_the_real_categories_rather_than_guessing_one()
    {
        // Choosing a category is a judgement (a project's piles could be
        // Generic Models, a two-point adaptive family, anything), so the
        // starter lists what's actually there and leaves the value alone.
        var model = RevitCheckTestBuilders.Model(elements: new[]
        {
            RevitCheckTestBuilders.Element(1, category: "Generic Models"),
            RevitCheckTestBuilders.Element(2, category: "Generic Models"),
            RevitCheckTestBuilders.Element(3, category: "Structural Framing"),
        });

        var result = RuleConfigStarter.Build(model);

        Assert.Equal(new RuleConfig().PileCategoryName, result.Config.PileCategoryName);
        Assert.Contains(result.Diagnostics, d => d.Contains("Generic Models (2)"));
    }

    /// <summary>
    /// The collection categories must be configurable, because they run
    /// upstream of everything else: an element in no swept category never
    /// reaches the check, so the identity join cannot rescue it. A real gap
    /// found 2026-09-07 when both pile commands passed a hardcoded
    /// OST_StructuralFoundation while the check had just been made
    /// category-agnostic.
    /// </summary>
    [Fact]
    public void Pile_collection_categories_default_to_both_real_cases_and_round_trip()
    {
        var defaults = new RuleConfig();

        Assert.Contains("OST_StructuralFoundation", defaults.PileCollectionCategoryNames);
        Assert.Contains("OST_GenericModel", defaults.PileCollectionCategoryNames);

        var loaded = RuleConfigSerializer.Loads(RuleConfigSerializer.Dumps(new RuleConfig
        {
            PileCollectionCategoryNames = new List<string> { "OST_StructuralFraming" },
        }));

        Assert.Equal(new[] { "OST_StructuralFraming" }, loaded.PileCollectionCategoryNames);
    }

    /// <summary>
    /// The Spot Elevation shelf-search radius decides whether that check
    /// finds any geometry at all - beyond it every spot reports "no nearby
    /// geometry" regardless of how correct the drawing is. It was a
    /// hardcoded adapter constant until 2026-09-07, documented there as
    /// "generous but not calibrated", which is the exact phrase that
    /// preceded all three real cross-model failures.
    /// </summary>
    [Fact]
    public void Spot_elevation_shelf_search_radius_is_configurable()
    {
        Assert.Equal(1500.0, new RuleConfig().SpotElevationShelfSearchRadiusMm);

        var loaded = RuleConfigSerializer.Loads(
            "{\"spot_elevation_shelf_search_radius_mm\": 3000.0}");

        Assert.Equal(3000.0, loaded.SpotElevationShelfSearchRadiusMm);
    }
}
