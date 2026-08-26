using System.Globalization;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Model-vs-schedule pile setout: for each captured pile element, compares
/// its own real, LIVE Easting/Northing (<see cref="ElementMetadata.ProjectPositionEastingMm"/>/
/// <see cref="ElementMetadata.ProjectPositionNorthingMm"/> - computed fresh
/// every capture, see that field's own remarks) against the live pile
/// schedule's row for that same pile, joined by
/// <see cref="RuleConfig.PileKeyParameterName"/>. This is the "new, doesn't
/// exist in the old PDF/DWG pipeline" half of the two pile checks named in
/// PLANNING.md §14: the old pipeline had no live model to compare against,
/// only a DXF export, so it could only check drawing-vs-schedule
/// (<c>geometry.setout_reconstruction</c>'s bearing/dimension-chain
/// reconstruction - still unbuilt here, see PLANNING.md §14). This rule
/// catches a different, real failure mode the user named directly: a pile
/// moves in the model, nobody reruns the Dynamo script that (re)writes the
/// schedule, and the schedule silently drifts from the model with nothing
/// in the drawing itself to catch it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately structured like <see cref="MetadataReconciliationCheck"/>'s
/// join (key parameter -&gt; matching row, ambiguity reported rather than
/// silently resolved, a missing match is its own finding) even though the
/// reference data source differs - a live <see cref="ScheduleInfo"/> here,
/// an uploaded CSV there. Two things intentionally do NOT reuse that
/// check's machinery: field mapping (this rule only ever compares two fixed
/// numeric fields, not an arbitrary mapped set) and its "CSV excess is
/// expected noise" asymmetry (a schedule with a pile row that has no
/// matching model element is exactly the useful signal
/// <c>geometry.ifc_setout_consistency</c>'s own "no candidate" case already
/// established mattered - not implemented in this first slice, since no
/// real case has surfaced yet needing it; see the module's own coverage
/// handling below for what is reported today).
/// </para>
/// <para>
/// Correction, 2026-08-26: an earlier version of this rule compared the
/// schedule against this project's own <c>XYZ_Easting</c>/<c>XYZ_Northing</c>
/// parameters instead of a live-computed position. The user caught this
/// directly - those parameters are themselves written by the same Dynamo
/// script that (re)writes the schedule, reading the insertion point at the
/// time it last ran. Comparing one against the other is comparing the same
/// stale value to itself: move a pile without rerunning Dynamo and both
/// sides stay frozen in agreement, exactly the failure this rule exists to
/// catch. <see cref="ElementMetadata.ProjectPositionEastingMm"/>/
/// <see cref="ElementMetadata.ProjectPositionNorthingMm"/> exist specifically
/// to be the side of this comparison that can't go stale that way - see
/// their own remarks.
/// </para>
/// </remarks>
public static class PileModelScheduleConsistencyCheck
{
    public const string RuleId = "revitcheck.pile_model_schedule_consistency";

    private const int MaxListed = 5;

    public static List<Issue> Run(RevitModel model, RuleConfig config)
    {
        var issues = new List<Issue>();

        var pileElements = model.Elements
            .Where(e => string.Equals(e.Category, config.PileCategoryName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pileElements.Count == 0)
        {
            // Mirrors the old pipeline's own fix for this exact silent-empty
            // case (ARCHIVE-pdf-dwg.md, geometry.ifc_setout_consistency
            // review point 2): zero elements to check must not look
            // identical to "checked every pile, all fine."
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "low",
                Description =
                    $"No captured elements have category '{config.PileCategoryName}' - nothing was checked " +
                    "against the pile schedule. Confirm the category name matches this project's convention " +
                    "if piles were expected.",
            });
            return issues;
        }

        var candidateSchedules = model.Schedules
            .Where(s =>
                s.ResolveHeader(config.PileScheduleIdHeaders) is not null &&
                s.ResolveHeader(config.PileScheduleEastingHeaders) is not null &&
                s.ResolveHeader(config.PileScheduleNorthingHeaders) is not null)
            .ToList();

        if (candidateSchedules.Count == 0)
        {
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "medium",
                Description =
                    $"{pileElements.Count} pile element(s) were captured, but no schedule with the expected " +
                    "setout columns (id/Easting/Northing) was found - nothing could be checked against a schedule.",
            });
            return issues;
        }

        var blankKeyElementIds = new List<long>();

        foreach (var pile in pileElements)
        {
            var keyValue = ResolveKeyValue(pile, config);
            if (keyValue is null)
            {
                blankKeyElementIds.Add(pile.ElementId);
                continue;
            }

            var matches = candidateSchedules
                .SelectMany(s => s.RowsForKey(s.ResolveHeader(config.PileScheduleIdHeaders)!, keyValue)
                    .Select(row => (Schedule: s, Row: row)))
                .ToList();

            if (matches.Count == 0)
            {
                issues.Add(new Issue
                {
                    RuleId = RuleId,
                    Category = "geometry",
                    Severity = "medium",
                    ElementId = pile.ElementId,
                    UniqueId = pile.UniqueId,
                    Description =
                        $"Pile has key '{keyValue}' but no matching row was found in any captured pile schedule.",
                });
                continue;
            }

            if (matches.Count > 1)
            {
                issues.Add(new Issue
                {
                    RuleId = RuleId,
                    Category = "coverage",
                    Severity = "medium",
                    ElementId = pile.ElementId,
                    UniqueId = pile.UniqueId,
                    Description =
                        $"Pile has key '{keyValue}', matching {matches.Count} schedule rows across the captured " +
                        "schedules - genuinely ambiguous, so this pile was not checked rather than compared " +
                        "against an arbitrarily chosen row.",
                });
                continue;
            }

            ComparePosition(pile, keyValue, matches[0].Schedule, matches[0].Row, config, issues);
        }

        if (blankKeyElementIds.Count > 0)
        {
            issues.Add(BuildBlankKeyIssue(blankKeyElementIds, config));
        }

        return issues;
    }

    private static string? ResolveKeyValue(ElementMetadata pile, RuleConfig config)
    {
        if (!pile.Parameters.TryGetValue(config.PileKeyParameterName, out var value))
        {
            return null;
        }

        var raw = (value.RawString ?? value.DisplayString)?.Trim();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static void ComparePosition(
        ElementMetadata pile,
        string keyValue,
        ScheduleInfo schedule,
        IReadOnlyDictionary<string, string> row,
        RuleConfig config,
        List<Issue> issues)
    {
        if (pile.ProjectPositionEastingMm is not { } pileEastingMm)
        {
            issues.Add(CoverageIssue(pile,
                $"Pile has key '{keyValue}' but no live position was captured for it " +
                "(ElementMetadata.ProjectPositionEastingMm is null) - Easting could not be checked."));
            return;
        }

        if (pile.ProjectPositionNorthingMm is not { } pileNorthingMm)
        {
            issues.Add(CoverageIssue(pile,
                $"Pile has key '{keyValue}' but no live position was captured for it " +
                "(ElementMetadata.ProjectPositionNorthingMm is null) - Northing could not be checked."));
            return;
        }

        var eastingHeader = schedule.ResolveHeader(config.PileScheduleEastingHeaders)!;
        var northingHeader = schedule.ResolveHeader(config.PileScheduleNorthingHeaders)!;

        if (!TryParseMetresToMm(row, eastingHeader, config, out var scheduleEastingMm))
        {
            issues.Add(CoverageIssue(pile,
                $"Pile has key '{keyValue}' but its schedule row's '{eastingHeader}' value could not be read " +
                "as a number - Easting could not be checked."));
            return;
        }

        if (!TryParseMetresToMm(row, northingHeader, config, out var scheduleNorthingMm))
        {
            issues.Add(CoverageIssue(pile,
                $"Pile has key '{keyValue}' but its schedule row's '{northingHeader}' value could not be read " +
                "as a number - Northing could not be checked."));
            return;
        }

        var deltaEastingMm = pileEastingMm - scheduleEastingMm;
        var deltaNorthingMm = pileNorthingMm - scheduleNorthingMm;
        // Planar ground distance, not axis-wise - an E-only or N-only
        // tolerance would be arbitrary; the real-world question is "how far
        // apart are these two points," matching §5b's own survey-tolerance
        // framing (PLANNING.md §5).
        var deltaMm = Math.Sqrt(deltaEastingMm * deltaEastingMm + deltaNorthingMm * deltaNorthingMm);

        if (deltaMm <= config.PileSetoutToleranceMm)
        {
            return;
        }

        issues.Add(new Issue
        {
            RuleId = RuleId,
            Category = "geometry",
            Severity = "high",
            ElementId = pile.ElementId,
            UniqueId = pile.UniqueId,
            Description =
                $"Pile '{keyValue}': live model position is {FormatMm(deltaMm)}mm from the schedule's " +
                $"'{schedule.Name}' row (live model E/N {FormatMm(pileEastingMm)}/{FormatMm(pileNorthingMm)}mm, schedule " +
                $"{FormatMm(scheduleEastingMm)}/{FormatMm(scheduleNorthingMm)}mm) - beyond the " +
                $"{FormatMm(config.PileSetoutToleranceMm)}mm tolerance. Either the pile moved after the " +
                "schedule was last generated, or the schedule was edited independently of the model.",
            SuggestedFix = new Dictionary<string, object?>
            {
                ["model_easting_mm"] = pileEastingMm,
                ["model_northing_mm"] = pileNorthingMm,
                ["schedule_easting_mm"] = scheduleEastingMm,
                ["schedule_northing_mm"] = scheduleNorthingMm,
                ["delta_mm"] = deltaMm,
            },
        });
    }

    private static bool TryParseMetresToMm(
        IReadOnlyDictionary<string, string> row, string header, RuleConfig config, out double mm)
    {
        mm = 0;
        if (!row.TryGetValue(header, out var raw))
        {
            return false;
        }

        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var metres))
        {
            return false;
        }

        mm = metres * RuleConfig.ScheduleMetresToMm;
        return true;
    }

    private static Issue CoverageIssue(ElementMetadata pile, string description) => new()
    {
        RuleId = RuleId,
        Category = "coverage",
        Severity = "medium",
        ElementId = pile.ElementId,
        UniqueId = pile.UniqueId,
        Description = description,
    };

    private static Issue BuildBlankKeyIssue(List<long> elementIds, RuleConfig config)
    {
        var listed = elementIds.Take(MaxListed).ToList();
        var remainder = elementIds.Count - listed.Count;
        var ids = string.Join(", ", listed);
        if (remainder > 0)
        {
            ids += $" (+{remainder} more)";
        }

        return new Issue
        {
            RuleId = RuleId,
            Category = "coverage",
            Severity = "medium",
            Description =
                $"{elementIds.Count} pile element(s) have no value for '{config.PileKeyParameterName}' and could " +
                $"not be matched to a schedule row at all: {ids}",
        };
    }

    private static string FormatMm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
