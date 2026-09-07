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

        // Easting/Northing are the only columns this check still has to
        // recognise by heading text. The id column is no longer required at
        // all: rows carry their own element (ScheduleRow.ElementId), so the
        // pile-to-row link is read from the model rather than reconstructed
        // by matching two rendered strings.
        var candidateSchedules = model.Schedules
            .Where(s =>
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
                    "No schedule with the expected setout columns (Easting/Northing) was found - nothing " +
                    "could be checked against a schedule.",
            });
            return issues;
        }

        var elementsById = model.Elements
            .GroupBy(e => e.ElementId)
            .ToDictionary(g => g.Key, g => g.First());

        var categoryElements = model.Elements
            .Where(e => string.Equals(e.Category, config.PileCategoryName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Anything a candidate schedule actually lists is in scope whatever
        // category it was modelled in - this is what stops a project that
        // models its piles as Generic Models (or a two-point adaptive
        // family, or anything else) from being silently skipped.
        var scheduledElements = candidateSchedules
            .SelectMany(s => s.Rows)
            .Select(r => r.ElementId)
            .Where(id => id is not null && elementsById.ContainsKey(id.Value))
            .Select(id => elementsById[id!.Value])
            .ToList();

        var toCheck = new List<ElementMetadata>();
        var seen = new HashSet<long>();
        foreach (var element in categoryElements.Concat(scheduledElements))
        {
            if (seen.Add(element.ElementId))
            {
                toCheck.Add(element);
            }
        }

        if (toCheck.Count == 0)
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
                    $"No captured elements have category '{config.PileCategoryName}', and no captured schedule " +
                    "lists an element in this capture - nothing was checked against the pile schedule.",
            });
            return issues;
        }

        if (categoryElements.Count == 0)
        {
            // Real 2026-09-07 case: piles modelled as Generic Models. The
            // check carried on via schedule membership rather than
            // returning nothing, but a reviewer still needs to know the
            // configured category matched nothing, since it's what the
            // model-side completeness check below depends on.
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "low",
                Description =
                    $"No captured elements have category '{config.PileCategoryName}', so scope came from " +
                    $"schedule membership instead ({toCheck.Count} element(s)). Any pile missing from the " +
                    "schedule entirely cannot be detected this way - set the category for this project if " +
                    "that matters.",
            });
        }

        var blankKeyElementIds = new List<long>();

        foreach (var pile in toCheck)
        {
            // Identity first: the model already states which row belongs to
            // which element (ScheduleRow.ElementId).
            var matches = candidateSchedules
                .SelectMany(s => s.RowsForElement(pile.ElementId).Select(row => (Schedule: s, Row: row)))
                .ToList();

            if (matches.Count == 0)
            {
                // Only rows with no element of their own can be joined by
                // key - a row that names a different element has already
                // answered the question, and overriding that with a text
                // match would be exactly the fragility this replaced.
                var keyValue = ResolveKeyValue(pile, config);
                if (keyValue is null)
                {
                    blankKeyElementIds.Add(pile.ElementId);
                    continue;
                }

                foreach (var schedule in candidateSchedules)
                {
                    var idHeader = schedule.ResolveHeader(config.PileScheduleIdHeaders);
                    if (idHeader is null)
                    {
                        continue;
                    }

                    matches.AddRange(schedule.RowsForKey(idHeader, keyValue)
                        .Where(row => row.ElementId is null)
                        .Select(row => (Schedule: schedule, Row: row)));
                }
            }

            var label = PileLabel(pile, config);

            if (matches.Count == 0)
            {
                issues.Add(new Issue
                {
                    RuleId = RuleId,
                    Category = "geometry",
                    Severity = "medium",
                    ElementId = pile.ElementId,
                    UniqueId = pile.UniqueId,
                    Description = $"{label} has no matching row in any captured pile schedule.",
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
                        $"{label} matches {matches.Count} schedule rows across the captured " +
                        "schedules - genuinely ambiguous, so this pile was not checked rather than compared " +
                        "against an arbitrarily chosen row.",
                });
                continue;
            }

            ComparePosition(pile, label, matches[0].Schedule, matches[0].Row, config, issues);
        }

        if (blankKeyElementIds.Count > 0)
        {
            issues.Add(BuildBlankKeyIssue(blankKeyElementIds, config));
        }

        return issues;
    }

    /// <summary>
    /// How a finding names an element: its ElementId always (the thing a
    /// reviewer types into Select by ID), plus the project's own key
    /// parameter when it has one, since that is what a person reads off the
    /// drawing. The key is descriptive here - it is no longer what the join
    /// depends on.
    /// </summary>
    private static string PileLabel(ElementMetadata pile, RuleConfig config)
    {
        var key = ResolveKeyValue(pile, config);
        return key is null
            ? $"Pile {pile.ElementId}"
            : $"Pile {pile.ElementId} ('{key}')";
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
        string label,
        ScheduleInfo schedule,
        ScheduleRow row,
        RuleConfig config,
        List<Issue> issues)
    {
        if (pile.ProjectPositionEastingMm is not { } pileEastingMm)
        {
            issues.Add(CoverageIssue(pile,
                $"{label} has no live position captured " +
                "(ElementMetadata.ProjectPositionEastingMm is null) - Easting could not be checked."));
            return;
        }

        if (pile.ProjectPositionNorthingMm is not { } pileNorthingMm)
        {
            issues.Add(CoverageIssue(pile,
                $"{label} has no live position captured " +
                "(ElementMetadata.ProjectPositionNorthingMm is null) - Northing could not be checked."));
            return;
        }

        var eastingHeader = schedule.ResolveHeader(config.PileScheduleEastingHeaders)!;
        var northingHeader = schedule.ResolveHeader(config.PileScheduleNorthingHeaders)!;

        if (!TryParseMetresToMm(row, eastingHeader, config, out var scheduleEastingMm))
        {
            issues.Add(CoverageIssue(pile,
                $"{label}'s schedule row has an '{eastingHeader}' value that could not be read " +
                "as a number - Easting could not be checked."));
            return;
        }

        if (!TryParseMetresToMm(row, northingHeader, config, out var scheduleNorthingMm))
        {
            issues.Add(CoverageIssue(pile,
                $"{label}'s schedule row has a '{northingHeader}' value that could not be read " +
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
                $"{label}: live model position is {FormatMm(deltaMm)}mm from the schedule's " +
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
        ScheduleRow row, string header, RuleConfig config, out double mm)
    {
        mm = 0;
        if (row.Value(header) is not { } raw)
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
