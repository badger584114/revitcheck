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
/// <b>Corrected 2026-09-07, after real runs on two further bridge models
/// failed for two different naming reasons.</b> One model's piles are
/// modelled as Generic Models, so the configured category matched nothing
/// and the check returned having compared nothing; the other's schedule
/// columns were headed differently, so no rows were captured to join
/// against. Both were the same mistake in two costumes - deriving, from
/// display text, a link the model already states. The join is now identity
/// (a schedule row's own backing element, <see cref="ScheduleRow.ElementId"/>),
/// scope includes anything a candidate schedule actually lists regardless
/// of category, and no id column or key parameter is required at all. A
/// key-text join survives only as a fallback for rows that genuinely have
/// no element behind them (the rendered-table read path), and never
/// overrides a row that names a different element. What still needs
/// recognising by name is exactly one thing: which two columns carry
/// Easting and Northing - a semantic mapping no amount of model
/// introspection can supply, and which belongs in per-project config.
/// </para>
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

    /// <summary>
    /// Every schedule with both setout headings resolvable, whether or not
    /// it turns out to state one position for the whole structure.
    /// </summary>
    private static List<ScheduleInfo> WithSetoutColumns(RevitModel model, RuleConfig config) =>
        model.Schedules
            .Where(s =>
                s.ResolveHeader(config.PileScheduleEastingHeaders) is not null &&
                s.ResolveHeader(config.PileScheduleNorthingHeaders) is not null)
            .ToList();

    /// <summary>
    /// The schedules this check actually compares a pile against - what a
    /// run summary should name as "compared against".
    /// </summary>
    /// <remarks>
    /// Public so a command can report the real answer instead of deriving
    /// its own. <c>PileModelScheduleConsistencyCommand</c> used to rebuild
    /// this list with a stricter rule - it also required an id column,
    /// which the identity join dropped in §19 - so its dialog could name no
    /// candidate schedules on a run where this check had compared against
    /// several. Two layers computing the same thing differently is a shape
    /// this project has been bitten by before; there is now one definition.
    /// </remarks>
    public static List<ScheduleInfo> ComparedSchedules(RevitModel model, RuleConfig config) =>
        WithSetoutColumns(model, config)
            .Where(s => !StatesOnePositionForEverything(s, config))
            .ToList();

    public static List<Issue> Run(RevitModel model, RuleConfig config)
    {
        var issues = new List<Issue>();

        // Easting/Northing are the only columns this check still has to
        // recognise by heading text. The id column is no longer required at
        // all: rows carry their own element (ScheduleRow.ElementId), so the
        // pile-to-row link is read from the model rather than reconstructed
        // by matching two rendered strings.
        var allCandidateSchedules = WithSetoutColumns(model, config);

        // A setout column states where each element is. One that gives every
        // row the same answer is not doing that, whatever its heading says.
        var candidateSchedules = ComparedSchedules(model, config);

        var wholeStructureSchedules = allCandidateSchedules.Except(candidateSchedules).ToList();
        if (wholeStructureSchedules.Count > 0)
        {
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "low",
                Description =
                    $"{wholeStructureSchedules.Count} schedule(s) have a configured setout column that states " +
                    "the same position for every row, so they describe the structure rather than each " +
                    "element and were not used as a stated pile position: " +
                    string.Join(", ", wholeStructureSchedules.Take(MaxListed).Select(s => $"'{s.Name}'")) +
                    (wholeStructureSchedules.Count > MaxListed ? ", ..." : "") +
                    ". If that is wrong, remove the column from pile_schedule_easting_headers / " +
                    "pile_schedule_northing_headers rather than leaving it to contradict the real one.",
            });
        }

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

        var categoryMatchedIds = new HashSet<long>(categoryElements.Select(e => e.ElementId));

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
        var outOfScopeElementIds = new List<long>();

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

            // Scope narrowing, added 2026-09-07 from real data: schedule
            // membership is a broader scope than "the piles". A real
            // candidate schedule on model 100302 also lists voids, a
            // conduit and a floor as backing elements, and those arrived
            // here alongside 28 genuine piles. An element that only got in
            // via a schedule row, and whose row carries no readable
            // coordinates, is simply another row in that schedule - not a
            // pile with a coverage problem. Counted, never silently
            // dropped.
            if (matches.Count > 0 &&
                !categoryMatchedIds.Contains(pile.ElementId) &&
                !matches.Any(m => TryReadRowPosition(m.Schedule, m.Row, config, out _, out _)))
            {
                outOfScopeElementIds.Add(pile.ElementId);
                continue;
            }

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

            // A row only takes part in the position comparison if it
            // actually states a position this check can read. See
            // PartitionByReadablePosition: silence is not contradiction.
            var readable = PartitionByReadablePosition(matches, config, out var unreadable);

            if (readable.Count == 0)
            {
                issues.Add(NoStatedPositionIssue(pile, label, matches, config));
                continue;
            }

            if (readable.Count > 1 && !RowsAgree(readable, config, out var disagreementMm))
            {
                issues.Add(new Issue
                {
                    RuleId = RuleId,
                    Category = "geometry",
                    Severity = "high",
                    ElementId = pile.ElementId,
                    UniqueId = pile.UniqueId,
                    Description =
                        $"{label} appears in {readable.Count} schedule rows that state a position, and they " +
                        $"disagree with each other by up to {FormatMm(disagreementMm)}mm (" +
                        string.Join(", ", readable.Select(m => $"'{m.Schedule.Name}'")) +
                        ") - the schedules themselves are inconsistent about where this pile is, so there is no " +
                        "single stated position to check the model against." +
                        (unreadable.Count > 0
                            ? $" ({unreadable.Count} further row(s) state no readable position and were not " +
                              "counted either way.)"
                            : string.Empty),
                });
                continue;
            }

            ComparePosition(pile, label, readable[0].Schedule, readable[0].Row, config, issues);
        }

        if (blankKeyElementIds.Count > 0)
        {
            issues.Add(BuildBlankKeyIssue(blankKeyElementIds, config));
        }

        if (outOfScopeElementIds.Count > 0)
        {
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "low",
                Description =
                    $"{outOfScopeElementIds.Count} element(s) appear in a captured setout schedule but are not " +
                    $"in category '{config.PileCategoryName}' and their rows carry no readable coordinates, so " +
                    "they were treated as other content in that schedule rather than as piles: " +
                    string.Join(", ", outOfScopeElementIds.Take(MaxListed)) +
                    (outOfScopeElementIds.Count > MaxListed ? ", ..." : "") + ".",
            });
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

    /// <summary>
    /// True when the schedule's configured setout column gives every row it
    /// can read the same position - so it states something about the whole
    /// structure, not about each element.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-09-09, from the first real negative control.</b> A
    /// deliberately planted 50mm move on pile 5506399 went undetected. The
    /// cause was <c>DIT_StartEasting</c>/<c>DIT_StartNorthing</c> being
    /// configured as setout columns: on this client's projects they are
    /// maintenance metadata carrying the <i>bridge's</i> centrepoint, one
    /// value for the entire structure. Every pile therefore appeared to sit
    /// 4.3m to 20m from where "a schedule" said it was, the check reported
    /// the schedules as contradicting each other, and it refused to compare
    /// anything at all - masking the 50mm with a fabricated 4.3m, while the
    /// correct position sat in 'ABUTMENT A PILE SCHEDULE' unread.
    /// </para>
    /// <para>
    /// The heading cannot distinguish these - <c>DIT_StartEasting</c> reads
    /// exactly like a coordinate, which is why it was adopted - but the data
    /// can, and unambiguously: a column that answers "where is this
    /// element?" with one answer for every element is not that column. This
    /// is a search over real values rather than a judgement about names,
    /// which is the distinction this project keeps having to relearn.
    /// </para>
    /// <para>
    /// Fewer than two readable rows means there is nothing to compare, so
    /// the schedule is left alone - absence of variation is only evidence
    /// when there was a chance to vary.
    /// </para>
    /// </remarks>
    private static bool StatesOnePositionForEverything(ScheduleInfo schedule, RuleConfig config)
    {
        var positions = new List<(double E, double N)>();
        foreach (var row in schedule.Rows)
        {
            if (TryReadRowPosition(schedule, row, config, out var e, out var n))
            {
                positions.Add((e, n));
            }
        }

        if (positions.Count < 2)
        {
            return false;
        }

        return positions.All(p =>
            Math.Abs(p.E - positions[0].E) <= config.PileSetoutToleranceMm &&
            Math.Abs(p.N - positions[0].N) <= config.PileSetoutToleranceMm);
    }

    /// <summary>
    /// A row's stated Easting/Northing in millimetres, or false when either
    /// column is missing or unreadable.
    /// </summary>
    private static bool TryReadRowPosition(
        ScheduleInfo schedule, ScheduleRow row, RuleConfig config, out double eastingMm, out double northingMm)
    {
        eastingMm = 0;
        northingMm = 0;

        var eastingHeader = schedule.ResolveHeader(config.PileScheduleEastingHeaders);
        var northingHeader = schedule.ResolveHeader(config.PileScheduleNorthingHeaders);
        if (eastingHeader is null || northingHeader is null)
        {
            return false;
        }

        return TryParseMetresToMm(row, eastingHeader, config, out eastingMm) &&
               TryParseMetresToMm(row, northingHeader, config, out northingMm);
    }

    /// <summary>
    /// True when every row that states a position states the same one,
    /// within <see cref="RuleConfig.PileSetoutToleranceMm"/>. Rows stating
    /// no readable position never reach here - see
    /// <see cref="PartitionByReadablePosition"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-09-07, from a real run that checked nothing.</b> Model
    /// 100302 has five schedules carrying setout columns - two per-abutment,
    /// one for off-structure barrier piles, one for setout points, and a
    /// general one - so a pile legitimately appears in more than one of
    /// them. Every one of its 33 piles matched several rows and was
    /// reported "genuinely ambiguous, so this pile was not checked": the
    /// check refused to do its job on every single element, because it read
    /// duplication as conflict.
    /// </para>
    /// <para>
    /// Rows that agree are one answer stated twice, and the check proceeds.
    /// Rows that genuinely disagree are a real finding in their own right -
    /// the schedules contradict each other about where a pile belongs -
    /// which is a stronger result than the coverage note it replaces, and
    /// is reported as such rather than as an excuse not to look.
    /// </para>
    /// </remarks>
    private static bool RowsAgree(
        List<ReadableRow> readable, RuleConfig config, out double worstDisagreementMm)
    {
        worstDisagreementMm = 0;

        for (var i = 0; i < readable.Count; i++)
        {
            for (var j = i + 1; j < readable.Count; j++)
            {
                var dE = readable[i].EastingMm - readable[j].EastingMm;
                var dN = readable[i].NorthingMm - readable[j].NorthingMm;
                worstDisagreementMm = Math.Max(worstDisagreementMm, Math.Sqrt((dE * dE) + (dN * dN)));
            }
        }

        return worstDisagreementMm <= config.PileSetoutToleranceMm;
    }

    /// <summary>One matched row that does state a position, with it already read.</summary>
    private readonly struct ReadableRow
    {
        public ReadableRow(ScheduleInfo schedule, ScheduleRow row, double eastingMm, double northingMm)
        {
            Schedule = schedule;
            Row = row;
            EastingMm = eastingMm;
            NorthingMm = northingMm;
        }

        public ScheduleInfo Schedule { get; }

        public ScheduleRow Row { get; }

        public double EastingMm { get; }

        public double NorthingMm { get; }
    }

    /// <summary>
    /// Splits the matched rows into the ones that state a readable position
    /// and the ones that do not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists, added 2026-09-09 from a real run.</b> The previous
    /// code asked "do all the matched rows agree?" and treated an unreadable
    /// row as a row that disagreed. On model 100302 every pile matches seven
    /// rows, and only one of those schedules ('ABUTMENT A PILE SCHEDULE')
    /// carries a heading the config recognises - the rest are
    /// <c>XYZ_Easting</c>, <c>DIT_Easting</c>, <c>DIT_StartEasting</c>. So
    /// 32 of 33 piles were reported high severity as "schedules that
    /// disagree with each other by up to 0mm", the message quoting the
    /// untouched initial value of a disagreement that had never been
    /// measured, and <see cref="ComparePosition"/> - the check's actual job -
    /// never ran at all.
    /// </para>
    /// <para>
    /// <b>A row that states nothing is not a row that contradicts.</b> This
    /// is the same error as the 2026-09-07 one directly above (duplication
    /// read as conflict) one step over: unreadability read as conflict. The
    /// rows that state a position are compared with each other; the rows
    /// that state none are counted and reported, never allowed to veto a
    /// comparison they take no part in.
    /// </para>
    /// </remarks>
    private static List<ReadableRow> PartitionByReadablePosition(
        List<(ScheduleInfo Schedule, ScheduleRow Row)> matches,
        RuleConfig config,
        out List<(ScheduleInfo Schedule, ScheduleRow Row)> unreadable)
    {
        var readable = new List<ReadableRow>();
        unreadable = new List<(ScheduleInfo Schedule, ScheduleRow Row)>();

        foreach (var match in matches)
        {
            if (TryReadRowPosition(match.Schedule, match.Row, config, out var e, out var n))
            {
                readable.Add(new ReadableRow(match.Schedule, match.Row, e, n));
            }
            else
            {
                unreadable.Add(match);
            }
        }

        return readable;
    }

    /// <summary>
    /// Coverage, not a verdict: this pile is in the schedules but none of
    /// its rows state a position this check can read, so nothing was
    /// compared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every matched row comes from a candidate schedule, and a schedule is
    /// only a candidate if both setout headings resolve - so reaching here
    /// means the columns exist and the cells hold nothing this check can
    /// parse (blank, or not a number), not that the column was missing.
    /// The finding says which columns it read, since that is the difference
    /// between "the schedule doesn't say" and "the tool can't tell".
    /// </para>
    /// <para>
    /// It also names any other coordinate-looking heading in those
    /// schedules, because where the configured column is blank a populated
    /// one alongside it is usually the right one, and the fix is then a
    /// config edit rather than a code change. Naming candidates is a search
    /// and is safe to automate; choosing which column actually carries the
    /// pile's setout position is a judgement - a linear asset's
    /// <c>DIT_StartEasting</c> is a start point, not a centre - and is left
    /// to a person, the same split <c>RuleConfigStarter</c> already draws.
    /// </para>
    /// </remarks>
    private static Issue NoStatedPositionIssue(
        ElementMetadata pile,
        string label,
        List<(ScheduleInfo Schedule, ScheduleRow Row)> matches,
        RuleConfig config)
    {
        var consulted = matches
            .SelectMany(m => new[]
            {
                m.Schedule.ResolveHeader(config.PileScheduleEastingHeaders),
                m.Schedule.ResolveHeader(config.PileScheduleNorthingHeaders),
            })
            .Where(h => h is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var alternatives = matches
            .SelectMany(m => m.Schedule.Headers)
            .Where(LooksLikeCoordinateHeader)
            .Where(h => !consulted.Contains(h, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxListed)
            .ToList();

        return new Issue
        {
            RuleId = RuleId,
            Category = "coverage",
            Severity = "low",
            ElementId = pile.ElementId,
            UniqueId = pile.UniqueId,
            Description =
                $"{label} appears in {matches.Count} schedule row(s), none of which state a position this check " +
                "can read, so its model position was not compared against anything (" +
                string.Join(", ", matches.Select(m => $"'{m.Schedule.Name}'")) +
                "). The setout column(s) read were " +
                string.Join(", ", consulted.Select(h => $"'{h}'")) +
                " - present, but blank or not a number for this element." +
                (alternatives.Count > 0
                    ? " These schedules also carry coordinate-looking columns that are not configured: " +
                      string.Join(", ", alternatives.Select(c => $"'{c}'")) +
                      " - add the right one to pile_schedule_easting_headers / " +
                      "pile_schedule_northing_headers if it is the pile's setout position."
                    : string.Empty),
        };
    }

    /// <summary>A heading a human would recognise as a coordinate - used only to suggest, never to read a value.</summary>
    private static bool LooksLikeCoordinateHeader(string header) =>
        header.IndexOf("easting", StringComparison.OrdinalIgnoreCase) >= 0 ||
        header.IndexOf("northing", StringComparison.OrdinalIgnoreCase) >= 0;

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
                // The mark a person reads off the drawing, carried as data
                // rather than left to be scraped back out of Description -
                // a run summary needs it, and this project's standing rule
                // is never to reconstruct from rendered text.
                ["pile_key"] = ResolveKeyValue(pile, config),
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
