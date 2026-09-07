using RevitCheck.Core.Checks;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// Real-data-shaped scenarios for PileModelScheduleConsistencyCheck - values
/// mirror the actual numbers from InspectPileSetout.pushbutton's real
/// 2026-08-26 run (PLANNING.md §14), not invented figures, so a passing
/// suite reflects the real precision the check needs to handle.
/// </summary>
public class PileModelScheduleConsistencyCheckTests
{
    [Fact]
    public void Clean_run_within_tolerance_reports_nothing()
    {
        // Real numbers, pile PIL232132 - schedule 278238.811/6130224.281m,
        // model (XYZ_Easting/Northing) 278238810.671/6130224280.728mm -
        // sub-millimetre apart, well inside the 10mm default.
        var model = RevitCheckTestBuilders.Model(
            elements: new[]
            {
                RevitCheckTestBuilders.Pile(5009495, "PIL232132", 278238810.671, 6130224280.728),
            },
            schedules: new[]
            {
                RevitCheckTestBuilders.PileSchedule(
                    "ABUTMENT B, B1 AND B2 PILE SCHEDULE",
                    new[] { ("PIL232132", "278238.811", "6130224.281") }),
            });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void Pile_moved_after_the_schedule_was_generated_is_flagged_high()
    {
        // Same schedule row as above, but the pile's own model position has
        // moved 500mm east - the real staleness scenario the user named
        // (moved in the model, Dynamo script not rerun).
        var model = RevitCheckTestBuilders.Model(
            elements: new[]
            {
                RevitCheckTestBuilders.Pile(5009495, "PIL232132", 278239310.671, 6130224280.728),
            },
            schedules: new[]
            {
                RevitCheckTestBuilders.PileSchedule(
                    "ABUTMENT B, B1 AND B2 PILE SCHEDULE",
                    new[] { ("PIL232132", "278238.811", "6130224.281") }),
            });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("high", issue.Severity);
        Assert.Equal("geometry", issue.Category);
        Assert.Equal(5009495, issue.ElementId);
        Assert.Contains("PIL232132", issue.Description);
    }

    [Fact]
    public void Pile_moved_while_dynamo_written_parameters_and_schedule_stayed_frozen_is_still_flagged()
    {
        // The real bug this rule had until 2026-08-26, confirmed by the
        // user: XYZ_Easting/XYZ_Northing are themselves written by the same
        // Dynamo script that (re)writes the schedule, from the insertion
        // point at the time it last ran - so a version of this check that
        // compared XYZ_Easting/XYZ_Northing against the schedule would
        // compare the same stale value to itself, and would report this
        // exact scenario as clean. This test proves the fix: the pile's
        // live position (ProjectPositionEastingMm/NorthingMm) has moved
        // 500mm east, but its XYZ_Easting/XYZ_Northing parameters - and the
        // schedule, which agrees with them, exactly as it would for real -
        // are still frozen at the old position. The check must flag this
        // using the live position, not be fooled by the frozen pair
        // agreeing with each other.
        var model = RevitCheckTestBuilders.Model(
            elements: new[]
            {
                RevitCheckTestBuilders.Pile(
                    5009495, "PIL232132",
                    eastingMm: 278239310.671, // live position: moved 500mm east
                    northingMm: 6130224280.728,
                    frozenXyzEastingMm: 278238810.671, // still the OLD position -
                    frozenXyzNorthingMm: 6130224280.728), // matches the schedule below exactly
            },
            schedules: new[]
            {
                RevitCheckTestBuilders.PileSchedule(
                    "ABUTMENT B, B1 AND B2 PILE SCHEDULE",
                    new[] { ("PIL232132", "278238.811", "6130224.281") }), // the old position, unchanged
            });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("high", issue.Severity);
        Assert.Contains("PIL232132", issue.Description);
    }

    [Fact]
    public void Two_piles_are_compared_independently_not_conflated()
    {
        var model = RevitCheckTestBuilders.Model(
            elements: new[]
            {
                RevitCheckTestBuilders.Pile(1, "PIL232132", 278238810.671, 6130224280.728),
                RevitCheckTestBuilders.Pile(2, "PIL232133", 278239916.211, 6130220127.579),
            },
            schedules: new[]
            {
                RevitCheckTestBuilders.PileSchedule(
                    "ABUTMENT B, B1 AND B2 PILE SCHEDULE",
                    new[]
                    {
                        ("PIL232132", "278238.811", "6130224.281"),
                        ("PIL232133", "278239.916", "6130220.128"),
                    }),
            });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        Assert.Empty(issues);
    }

    [Fact]
    public void No_pile_category_elements_reports_low_severity_coverage()
    {
        // A real candidate schedule is present, so "no schedule to compare
        // against" is not the blocker here - the scope is genuinely empty.
        // (Before the 2026-09-07 identity join this model needed no
        // schedule at all to reach this branch; scope now depends on
        // schedule membership too, so an empty-schedule model reports the
        // missing schedule instead, which is the more useful answer.)
        var model = RevitCheckTestBuilders.Model(
            elements: new[] { RevitCheckTestBuilders.Element(1, category: "Structural Framing") },
            schedules: new[]
            {
                RevitCheckTestBuilders.PileSchedule("PILE SETOUT", new[] { ("PIL000001", "278198.0", "6130233.0") }),
            });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("low", issue.Severity);
        Assert.Equal("coverage", issue.Category);
    }

    /// <summary>
    /// The real 2026-09-07 failure: a second bridge model whose piles are
    /// modelled as Generic Models, not Structural Foundations. The
    /// configured category matched nothing and the whole check returned
    /// without comparing anything. Scope now comes from schedule membership
    /// as well, so the pile is checked on its identity regardless of what
    /// category it happens to be modelled in.
    /// </summary>
    [Fact]
    public void A_pile_modelled_in_an_unexpected_category_is_still_checked_via_schedule_membership()
    {
        var pile = RevitCheckTestBuilders.Pile(
            5009495, "PIL232132", 278198410.59, 6130233357.011, category: "Generic Models");

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pile },
            schedules: new[]
            {
                RevitCheckTestBuilders.PileScheduleForElements(
                    "PILE SETOUT", new[] { (5009495L, "278198.410590", "6130233.357011") }),
            });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        // One low-severity note that the configured category matched
        // nothing - and crucially no "nothing was checked", and no false
        // mismatch: the pile agrees with its own row.
        var issue = Assert.Single(issues);
        Assert.Equal("low", issue.Severity);
        Assert.Contains("scope came from schedule membership", issue.Description);
    }

    /// <summary>
    /// The other half of the same real failure: the join no longer needs an
    /// id column or a key parameter to agree textually. Here the schedule
    /// carries no id column at all and the pile carries no key parameter,
    /// and the moved pile is still caught.
    /// </summary>
    [Fact]
    public void A_moved_pile_is_caught_with_no_id_column_and_no_key_parameter()
    {
        var pile = RevitCheckTestBuilders.Element(
            5009495,
            category: "Structural Foundations",
            projectPositionEastingMm: 278198410.59 + 250.0,
            projectPositionNorthingMm: 6130233357.011);

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pile },
            schedules: new[]
            {
                RevitCheckTestBuilders.PileScheduleForElements(
                    "PILE SETOUT", new[] { (5009495L, "278198.410590", "6130233.357011") }),
            });

        var issue = Assert.Single(PileModelScheduleConsistencyCheck.Run(model, new RuleConfig()));

        Assert.Equal("high", issue.Severity);
        Assert.Equal("geometry", issue.Category);
        Assert.Equal(5009495, issue.ElementId);
    }

    /// <summary>
    /// Identity must win over text: a row that names a different element is
    /// never re-joined by key, even when the key would match. Otherwise the
    /// fragile path could still silently override the reliable one.
    /// </summary>
    [Fact]
    public void A_row_naming_a_different_element_is_not_re_joined_by_key()
    {
        var pile = RevitCheckTestBuilders.Pile(111, "PIL232132", 278198410.59, 6130233357.011);

        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pile },
            schedules: new[]
            {
                // Same key text, but the row belongs to element 999.
                RevitCheckTestBuilders.PileScheduleForElements(
                    "PILE SETOUT", new[] { (999L, "278198.410590", "6130233.357011") }, siteId: "PIL232132"),
            });

        var issue = Assert.Single(PileModelScheduleConsistencyCheck.Run(model, new RuleConfig()));

        Assert.Equal("geometry", issue.Category);
        Assert.Contains("no matching row", issue.Description);
    }

    [Fact]
    public void Pile_elements_present_but_no_matching_schedule_reports_medium_coverage()
    {
        var model = RevitCheckTestBuilders.Model(
            elements: new[] { RevitCheckTestBuilders.Pile(1, "PIL232132", 278238810.671, 6130224280.728) },
            schedules: new[]
            {
                // Real headers but missing Northing - not a candidate.
                new Ir.ScheduleInfo
                {
                    Name = "UNRELATED SCHEDULE",
                    Headers = new List<string> { "SITE ID", "EASTING (m)" },
                    Rows = new List<Ir.ScheduleRow>(),
                },
            });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("medium", issue.Severity);
        Assert.Equal("coverage", issue.Category);
    }

    [Fact]
    public void Pile_with_no_matching_schedule_row_is_flagged()
    {
        var model = RevitCheckTestBuilders.Model(
            elements: new[] { RevitCheckTestBuilders.Pile(1, "PIL999999", 278238810.671, 6130224280.728) },
            schedules: new[]
            {
                RevitCheckTestBuilders.PileSchedule(
                    "ABUTMENT B, B1 AND B2 PILE SCHEDULE",
                    new[] { ("PIL232132", "278238.811", "6130224.281") }),
            });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("medium", issue.Severity);
        Assert.Contains("PIL999999", issue.Description);
    }

    [Fact]
    public void Pile_matching_rows_in_two_schedules_is_flagged_as_ambiguous_not_guessed()
    {
        var model = RevitCheckTestBuilders.Model(
            elements: new[] { RevitCheckTestBuilders.Pile(1, "PIL232132", 278238810.671, 6130224280.728) },
            schedules: new[]
            {
                RevitCheckTestBuilders.PileSchedule(
                    "ABUTMENT A AND A1 PILE SCHEDULE",
                    new[] { ("PIL232132", "278238.811", "6130224.281") }),
                RevitCheckTestBuilders.PileSchedule(
                    "ABUTMENT B, B1 AND B2 PILE SCHEDULE",
                    new[] { ("PIL232132", "278238.811", "6130224.281") }),
            });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("2 schedule rows", issue.Description);
    }

    [Fact]
    public void Blank_key_piles_are_aggregated_into_one_issue()
    {
        var model = RevitCheckTestBuilders.Model(
            elements: new[]
            {
                RevitCheckTestBuilders.Element(1, category: "Structural Foundations", parameters: new()),
                RevitCheckTestBuilders.Element(2, category: "Structural Foundations", parameters: new()),
            },
            schedules: new[]
            {
                RevitCheckTestBuilders.PileSchedule(
                    "ABUTMENT B, B1 AND B2 PILE SCHEDULE",
                    new[] { ("PIL232132", "278238.811", "6130224.281") }),
            });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("2 pile element(s)", issue.Description);
    }
}
