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

    /// <summary>
    /// The real 2026-09-09 failure. Dumps used to serialize every property,
    /// so the starter config Capture Model wrote froze that day's defaults
    /// permanently - and since the starter never overwrites an existing
    /// file, model 100302 ran on 09-09 still using the 09-07 morning's
    /// 60-arcsecond collinearity tolerance and 2-pile minimum. §20's retune
    /// to 0.2° and 3, calibrated against that very model, never reached it.
    /// A setting nobody chose must track the compiled default.
    /// </summary>
    [Fact]
    public void An_unremarked_setting_is_not_written_so_it_tracks_later_recalibration()
    {
        var json = RuleConfigSerializer.Dumps(new RuleConfig { PileCategoryName = "Generic Models" });

        Assert.Contains("pile_category_name", json);
        Assert.DoesNotContain("pile_chain_collinearity_tolerance_degrees", json);
        Assert.DoesNotContain("pile_chain_minimum_piles", json);
    }

    [Fact]
    public void A_config_differing_on_nothing_writes_only_its_schema_version()
    {
        var json = RuleConfigSerializer.Dumps(new RuleConfig());

        var loaded = RuleConfigSerializer.Loads(json);

        Assert.Equal(new RuleConfig().PileChainMinimumPiles, loaded.PileChainMinimumPiles);
        Assert.Equal(
            new RuleConfig().PileChainCollinearityToleranceDegrees,
            loaded.PileChainCollinearityToleranceDegrees);
        Assert.Contains("schema_version", json);
    }

    /// <summary>
    /// A value equal to the current default is dropped, but a project can
    /// still pin one deliberately by naming it in the file by hand - the
    /// read path honours any field present.
    /// </summary>
    [Fact]
    public void A_hand_pinned_field_is_still_honoured_on_read()
    {
        var loaded = RuleConfigSerializer.Loads("{\"pile_chain_minimum_piles\": 2}");

        Assert.Equal(2, loaded.PileChainMinimumPiles);
    }

    /// <summary>
    /// The stale file already on the machine still pins everything, and
    /// nothing in the build can see that - only the run can say it. These
    /// are the two real values model 100302's config was pinning.
    /// </summary>
    [Fact]
    public void Settings_pinned_away_from_the_current_defaults_are_described_for_the_run_summary()
    {
        var described = RuleConfigSerializer.DescribeOverrides(
            "{\"schema_version\": 1, \"pile_chain_minimum_piles\": 2, " +
            "\"pile_chain_collinearity_tolerance_degrees\": 0.016666666666666666, " +
            $"\"pile_category_name\": \"{new RuleConfig().PileCategoryName}\"}}");

        Assert.Equal(2, described.Count);
        Assert.Contains(described, d => d.StartsWith("pile_chain_minimum_piles = 2") && d.Contains("default is 3"));
        Assert.Contains(described, d => d.StartsWith("pile_chain_collinearity_tolerance_degrees"));
        // Recorded at the same value as the default, so it changes nothing
        // and is not worth a reviewer's attention.
        Assert.DoesNotContain(described, d => d.StartsWith("pile_category_name"));
    }

    /// <summary>
    /// A config is now an artefact that travels with its model to Forma
    /// rather than a file on one machine's C: drive (2026-09-09, the
    /// user's direction), so it has to say when it was written and for
    /// what. A run that cannot tell a config written this morning from one
    /// written before three recalibrations is the situation §22 and §23
    /// were both spent in.
    /// </summary>
    [Fact]
    public void A_written_config_records_when_and_what_it_was_written_for()
    {
        var json = RuleConfigSerializer.Dumps(
            new RuleConfig { PileCategoryName = "Generic Models" }, "T2DPAA-BR-M3D-100302_Peter.Griggs");

        var provenance = RuleConfigSerializer.DescribeProvenance(json);

        Assert.NotNull(provenance);
        Assert.Contains("T2DPAA-BR-M3D-100302_Peter.Griggs", provenance!);
        // And it must not become a phantom setting on the way back in.
        Assert.Equal("Generic Models", RuleConfigSerializer.Loads(json).PileCategoryName);
        Assert.DoesNotContain(RuleConfigSerializer.DescribeOverrides(json), d => d.Contains("written_"));
    }

    /// <summary>
    /// The real 2026-09-07 file, which carries no provenance. Its absence
    /// is the signal that it predates diff-writing and therefore pins
    /// everything - so it must be reported, not silently treated as fine.
    /// </summary>
    [Fact]
    public void A_config_with_no_provenance_is_reported_as_such_rather_than_assumed_current()
    {
        Assert.Null(RuleConfigSerializer.DescribeProvenance(
            "{\"schema_version\": 1, \"pile_chain_minimum_piles\": 2}"));
    }

    [Fact]
    public void A_newer_schema_version_is_refused_rather_than_misread()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RuleConfigSerializer.Loads("{\"schema_version\": 999}"));

        Assert.Contains("refusing to misread", ex.Message);
    }

    /// <summary>
    /// Rewritten 2026-09-09. This used to assert that the starter adopted
    /// every coordinate-looking heading it found, on the reasoning that
    /// widening a candidate list is harmless. It is not: headings resolve
    /// per schedule, so adopting one promotes a schedule carrying no setout
    /// data into a candidate setout schedule. Adopting
    /// DIT_StartEasting/DIT_StartNorthing - this client's maintenance
    /// metadata for the bridge's own centrepoint - is what masked a real
    /// planted 50mm error (PLANNING.md §23). Discovery reports; a person
    /// adopts.
    /// </summary>
    [Fact]
    public void Starter_reports_coordinate_headings_it_finds_but_never_adopts_them()
    {
        var model = RevitCheckTestBuilders.Model(schedules: new[]
        {
            new ScheduleInfo
            {
                Name = "ATM_Design_Automation - Added_New_DIT_Parameters",
                Headers = new List<string> { "DIT_LocationHierarchyCode", "DIT_StartEasting", "DIT_StartNorthing" },
                Rows = new List<ScheduleRow>(),
            },
        });

        var result = RuleConfigStarter.Build(model);

        Assert.DoesNotContain("DIT_StartEasting", result.Config.PileScheduleEastingHeaders);
        Assert.DoesNotContain("DIT_StartNorthing", result.Config.PileScheduleNorthingHeaders);
        // The defaults are left exactly as they were.
        Assert.Equal(new RuleConfig().PileScheduleEastingHeaders, result.Config.PileScheduleEastingHeaders);
        // But a reviewer is told the heading exists, so adopting it stays a
        // one-line config edit rather than a discovery problem.
        Assert.Contains(result.Diagnostics, d => d.Contains("DIT_StartEasting"));
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
    /// The collection categories run upstream of everything else: an
    /// element in no swept category never reaches the check, so the
    /// identity join cannot rescue it. Empty means "every model category",
    /// which is the default - a narrow default is right most of the time
    /// and silently examines nothing the rest of it, and it needs a person
    /// to already know the answer before the tool can find it.
    /// </summary>
    [Fact]
    public void Pile_collection_categories_default_to_sweeping_everything()
    {
        Assert.Empty(new RuleConfig().PileCollectionCategoryNames);

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

    /// <summary>
    /// The starter's category diagnostic has to name the *collection*
    /// categories too, not just pile_category_name: an element in no
    /// collected category never reaches the check, so listing what is in
    /// the model without saying what is actually swept would tell a reader
    /// half the story - the same half-fix that shipped earlier the same
    /// day.
    /// </summary>
    [Fact]
    public void Starter_diagnostic_names_the_collection_categories_not_just_the_expected_one()
    {
        var model = RevitCheckTestBuilders.Model(elements: new[]
        {
            RevitCheckTestBuilders.Element(1, category: "Structural Columns"),
        });

        var result = RuleConfigStarter.Build(model);

        var line = Assert.Single(result.Diagnostics, d => d.Contains("pile_collection_category_names"));
        Assert.Contains("Structural Columns (1)", line);
        Assert.Contains("never reach the check", line);
    }
}
