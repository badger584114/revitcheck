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
    public void Dit_start_easting_style_constant_parameter_is_not_what_gets_compared()
    {
        // Guards the real correction from the user, 2026-08-26: a
        // bridge-centre-style constant parameter must never be read as a
        // per-pile position just because RuleConfig's parameter names were
        // misconfigured to point at it - two piles sharing one constant
        // value should never silently "agree" with two different schedule
        // rows. Using the real (correct) XYZ_Easting/XYZ_Northing names,
        // confirm two distinct piles are compared independently, not
        // conflated.
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
        var model = RevitCheckTestBuilders.Model(
            elements: new[] { RevitCheckTestBuilders.Element(1, category: "Structural Framing") });

        var issues = PileModelScheduleConsistencyCheck.Run(model, new RuleConfig());

        var issue = Assert.Single(issues);
        Assert.Equal("low", issue.Severity);
        Assert.Equal("coverage", issue.Category);
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
                    Rows = new List<IReadOnlyDictionary<string, string>>(),
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
