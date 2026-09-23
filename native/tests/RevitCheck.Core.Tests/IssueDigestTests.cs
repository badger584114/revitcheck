using System.Collections.Generic;
using System.Text.Json;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// The one-line form the checklist's details pane shows (PLANNING.md §28,
/// 2026-09-23). Every fixture here is built from a real finding on the
/// committed capture, so the lengths being asserted are the real ones.
/// </summary>
public class IssueDigestTests
{
    private static Issue Issue(
        string ruleId, string description, Dictionary<string, object?>? fix, long? elementId = 100) => new()
    {
        RuleId = ruleId,
        Category = "geometry",
        Severity = "high",
        ElementId = elementId,
        Description = description,
        SuggestedFix = fix,
    };

    /// <summary>The real rollup from sheet 2873010, and the reason this exists: 421 characters into a 340px column.</summary>
    [Fact]
    public void A_view_rollup_states_its_counts_and_which_check_can_settle_them()
    {
        var issue = Issue(
            DimensionProvenanceCheck.RuleId,
            "Every dimension in section view 'DRG-2873010 - ELEVATION' (sheet 2873010) (430 of them) is taken " +
            "from detail linework. Nothing in this view tracks the model, and nothing in the file can show " +
            "whether it has drifted — it can only be verified against the model itself. No automated check can " +
            "reach any of them, so this view is a reviewer's by construction rather than work waiting on a tool.",
            new Dictionary<string, object?>
            {
                ["scope"] = "view",
                ["dimensions"] = 430,
                ["drafted_dimensions"] = 430,
                ["dimension_types"] = "430 unreachable",
            });

        var line = IssueDigest.ShortLine(issue);

        Assert.Equal("430 of 430 drafted - 430 unreachable", line);
        Assert.True(line.Length < issue.Description.Length / 5, "the whole point is that it is far shorter");
    }

    [Fact]
    public void A_drafted_dimension_says_what_it_measures_without_repeating_the_view()
    {
        var issue = Issue(
            DimensionProvenanceCheck.RuleId,
            "Spot dimension in engineering plan view 'DRG-2873251 - SERVICE SUPPORT GENERAL ARRANGEMENT PLAN' " +
            "(sheet 2873251) measures detail linework, not model geometry — it will not update when the model " +
            "changes, and will keep agreeing with the line it measures while doing so.",
            new Dictionary<string, object?> { ["provenance"] = "drafted", ["references"] = 2 });

        var line = IssueDigest.ShortLine(issue);

        Assert.Equal("Drafted - measures detail linework", line);
        Assert.DoesNotContain("SERVICE SUPPORT", line);
    }

    [Fact]
    public void A_mixed_dimension_keeps_the_distinction_that_matters()
    {
        var issue = Issue(
            DimensionProvenanceCheck.RuleId,
            "Dimension in engineering plan view '...' measures model geometry at one end and detail linework at " +
            "the other, so part of it tracks the model and part of it does not.",
            new Dictionary<string, object?> { ["provenance"] = "mixed", ["drafted_references"] = 1 });

        Assert.Equal("Mixed - model one end, linework the other", IssueDigest.ShortLine(issue));
    }

    /// <summary>The real finding on DRG - 2873175 - SECTION 3, which is the number a reviewer actually acts on.</summary>
    [Fact]
    public void An_overridden_value_leads_with_stated_against_measured()
    {
        var issue = Issue(
            DimensionOverrideConsistencyCheck.RuleId,
            "Segment 2 of 2 in a dimension chain in detail view 'DRG - 2873175 - SECTION 3' (sheet 2873175) is " +
            "typed as 1200mm but measures 1096.9mm (+103.1mm, more than rounding to the default grid explains: " +
            "±3.0mm).",
            new Dictionary<string, object?>
            {
                ["stated_mm"] = 1200.0,
                ["measured_mm"] = 1096.9,
                ["delta_mm"] = 103.1,
                ["segment"] = 2,
                ["segments"] = 2,
            });

        Assert.Equal("Seg 2/2: typed 1200mm, measures 1096.9mm (+103.1mm)", IssueDigest.ShortLine(issue));
    }

    [Fact]
    public void A_stated_limit_reads_as_a_limit_not_as_a_measurement()
    {
        var issue = Issue(
            DimensionOverrideConsistencyCheck.RuleId,
            "Dimension in engineering plan view '...' is annotated as at most 500mm, but the model measures " +
            "500.8mm — the stated limit is not met.",
            new Dictionary<string, object?>
            {
                ["stated_limit_mm"] = 500.0,
                ["comparator"] = "<=",
                ["measured_mm"] = 500.8,
                ["segment"] = 1,
                ["segments"] = 1,
            });

        Assert.Equal("stated at most 500mm, measures 500.8mm", IssueDigest.ShortLine(issue));
    }

    /// <summary>The longest line in a real run at 523 characters, and fully rebuildable from its own counts.</summary>
    [Fact]
    public void The_override_coverage_note_becomes_its_counts()
    {
        var issue = Issue(
            DimensionOverrideConsistencyCheck.RuleId,
            "183 of 10671 dimension segments carry a typed override, and 62 of those were compared against the " +
            "model. 2 of those stated a MIN/MAX limit rather than an exact value, and were checked against the " +
            "limit. 121 override(s) were not a number and were skipped rather than guessed at: ...",
            new Dictionary<string, object?>
            {
                ["segments"] = 10671,
                ["overridden"] = 183,
                ["checked"] = 62,
                ["bounds"] = 2,
                ["unparsed"] = 121,
            },
            elementId: null);

        Assert.Equal(
            "183 of 10671 segments overridden, 62 compared, 121 not a number",
            IssueDigest.ShortLine(issue));
    }

    /// <summary>The shape the drafted-dimension, pile-dimension and spot-elevation checks share.</summary>
    [Fact]
    public void A_stated_against_model_comparison_leads_with_the_disagreement()
    {
        var issue = Issue(
            DrawnDimensionConsistencyCheck.RuleId,
            "Dimension 100 states 1550mm, but the model geometry it sits against measures 1600mm as this view " +
            "sees it - 50mm out, beyond the 10mm tolerance. Measured between elements 900 and 901.",
            new Dictionary<string, object?>
            {
                ["stated_mm"] = 1550.0,
                ["model_mm"] = 1600.0,
                ["delta_mm"] = 50.0,
            });

        Assert.Equal("states 1550mm, model 1600mm - 50mm out", IssueDigest.ShortLine(issue));
    }

    /// <summary>
    /// A coverage note carrying no structured counts is the explanation
    /// itself - there is no shorter honest form, so it is returned whole
    /// rather than cut off mid-sentence.
    /// </summary>
    [Fact]
    public void A_note_with_no_structured_data_keeps_its_prose()
    {
        var description =
            "3 dimension(s) measure between two features of a single element, which nothing here can resolve.";
        var issue = Issue(DrawnDimensionConsistencyCheck.RuleId, description, fix: null, elementId: null);

        Assert.Equal(description, IssueDigest.ShortLine(issue));
    }

    /// <summary>A rule this helper has never heard of renders in full, never blank - the failure this project repeats most.</summary>
    [Fact]
    public void An_unrecognised_rule_falls_back_to_its_own_description()
    {
        var description = "Something a future check reports, in its own words.";
        var issue = Issue(
            "revitcheck.some_future_check",
            description,
            new Dictionary<string, object?> { ["an_unknown_key"] = 12 });

        Assert.Equal(description, IssueDigest.ShortLine(issue));
    }

    /// <summary>
    /// The regression that matters most here. A resumed session's
    /// SuggestedFix values come back as JsonElement, not their original CLR
    /// types - the round-trip that silently emptied two readers in
    /// PLANNING.md §16 - and this pane reads a deserialized session every
    /// time someone resumes one.
    /// </summary>
    [Fact]
    public void Values_reloaded_from_a_session_file_still_read()
    {
        var fix = new Dictionary<string, object?>
        {
            ["scope"] = "view",
            ["dimensions"] = 430,
            ["drafted_dimensions"] = 430,
            ["dimension_types"] = "430 unreachable",
        };

        var reloaded = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(fix))!;
        Assert.IsType<JsonElement>(reloaded["dimensions"]);

        Assert.Equal(
            "430 of 430 drafted - 430 unreachable",
            IssueDigest.ShortLine(Issue(DimensionProvenanceCheck.RuleId, "the long form", reloaded)));
    }
}
