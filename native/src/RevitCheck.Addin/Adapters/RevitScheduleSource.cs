using Autodesk.Revit.DB;
using RevitCheck.Core.Ir;

namespace RevitCheck.Addin.Adapters;

/// <summary>
/// Reads every <see cref="ViewSchedule"/> in a document into
/// <see cref="ScheduleInfo"/> - the live reference data
/// <c>PileModelScheduleConsistencyCheck</c> joins against (see that check's
/// own remarks: unlike <c>MetadataReconciliationCheck</c>'s uploaded CSV,
/// the pile schedule already lives in the document being captured, so there
/// is nothing to prompt for).
/// </summary>
/// <remarks>
/// <para>
/// Schedules aren't view-scoped the way piles/dimensions are collected
/// (<see cref="RevitDimensionSource"/>'s per-view scoping exists to work
/// around a real <c>Dimension.OwnerViewId</c> bug that has no schedule
/// equivalent) - a plain document-wide <see cref="FilteredElementCollector"/>
/// sweep is correct here.
/// </para>
/// <para>
/// <b>Body-row reading is narrowed to header-plausible schedules only -
/// added 2026-08-28, after a real run found 0 captured schedules where 2
/// real ones exist, alongside 62 extraction errors with no visible text
/// (PLANNING.md §16).</b> The first version of this class read every
/// schedule's full body text unconditionally, on the theory that filtering
/// by name (the way <c>InspectPileSetout.pushbutton</c>'s diagnostic did)
/// is exactly the kind of classification/judgement the adapter-boundary
/// rule reserves for <c>Checks/</c>. That theory was right about naming,
/// but missed a real cost/robustness problem: <c>GetTableData()</c>/
/// <c>GetCellText</c> is attempted against every <see cref="ViewSchedule"/>
/// in the document - revision schedules, sheet lists, quantity takeoffs,
/// key schedules, ~60 of them on the real model this project develops
/// against - when <c>PileModelScheduleConsistencyCheck</c> only ever uses
/// the handful whose headers resolve <em>all three</em> of an id/Easting/
/// Northing candidate (its own <c>candidateSchedules</c> filter). The
/// caller now passes that exact same header-candidate check in here via
/// <paramref name="idHeaderCandidates"/>/<paramref name="eastingHeaderCandidates"/>/
/// <paramref name="northingHeaderCandidates"/> - not a new judgement, the
/// identical one the check already makes, just made before the expensive
/// per-cell read instead of after it, so a schedule this join could never
/// use anyway never pays (or risks failing) that cost. Headers are still
/// read for every schedule regardless (cheap, and confirmed to work across
/// every real schedule kind by the diagnostic's own unconditional field
/// dump) - a schedule that doesn't match still appears in the result with
/// its real headers and an empty <see cref="ScheduleInfo.Rows"/>, a fact
/// worth keeping, not a reason to drop it. Passing no candidates at all
/// (the default) preserves the original unconditional behaviour, for any
/// future caller that isn't the pile check.
/// </para>
/// <para>
/// <b>Skips leading header/blank "body" rows.</b> Confirmed real and
/// necessary 2026-08-26 (PLANNING.md line 695):
/// <c>GetCellText(SectionType.Body, ...)</c> on this project's real pile
/// schedules returns two such rows before real data starts - a merged
/// multi-row header artifact, not <c>row 0 == data</c>. Rather than
/// hardcoding "skip exactly 2" (which would silently misbehave on a
/// schedule with a different artifact count, or none), a row is skipped
/// when it is either entirely blank or textually identical to the header
/// row itself (case-insensitive) - self-verifying against whatever
/// <see cref="ScheduleInfo.Headers"/> this same schedule actually resolved,
/// so it generalizes to zero-artifact and multi-artifact schedules alike
/// without a magic constant.
/// </para>
/// </remarks>
public static class RevitScheduleSource
{
    /// <summary>
    /// Every ViewSchedule in <paramref name="doc"/>, headers always
    /// included, body rows only for schedules whose headers resolve every
    /// one of <paramref name="idHeaderCandidates"/>/
    /// <paramref name="eastingHeaderCandidates"/>/<paramref name="northingHeaderCandidates"/>
    /// - see the class remarks on why. Leave all three null/empty to read
    /// body rows for every schedule unconditionally (the original,
    /// name-filter-free behaviour). Per-schedule extraction failures are
    /// isolated - see <paramref name="errors"/>.
    /// </summary>
    public static List<ScheduleInfo> Collect(
        Document doc,
        List<string> errors,
        IEnumerable<string>? idHeaderCandidates = null,
        IEnumerable<string>? eastingHeaderCandidates = null,
        IEnumerable<string>? northingHeaderCandidates = null)
    {
        var schedules = new List<ScheduleInfo>();
        var idCandidates = idHeaderCandidates?.ToList();
        var eastingCandidates = eastingHeaderCandidates?.ToList();
        var northingCandidates = northingHeaderCandidates?.ToList();
        // Null means "no filter was given" (read every schedule's body
        // unconditionally) - an empty list is a real, if odd, "match
        // nothing" filter, kept distinct rather than folded into the same
        // meaning as null.
        var filtered = idCandidates is not null || eastingCandidates is not null || northingCandidates is not null;

        FilteredElementCollector collector;
        try
        {
            collector = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule));
        }
        catch (Exception ex)
        {
            errors.Add($"collecting schedules: {ex.Message}");
            return schedules;
        }

        foreach (ViewSchedule schedule in collector)
        {
            try
            {
                var headers = ReadHeaders(schedule);
                var readBody = !filtered ||
                    (ResolvesHeader(headers, idCandidates) &&
                     ResolvesHeader(headers, eastingCandidates) &&
                     ResolvesHeader(headers, northingCandidates));

                schedules.Add(new ScheduleInfo
                {
                    Name = schedule.Name,
                    Headers = headers,
                    Rows = readBody ? ReadDataRows(schedule, headers) : new List<IReadOnlyDictionary<string, string>>(),
                });
            }
            catch (Exception ex)
            {
                errors.Add($"schedule {schedule.Id.Value} ({TryName(schedule)}): {ex.Message}");
            }
        }

        return schedules;
    }

    /// <summary>True if <paramref name="candidates"/> is null (no requirement given for this column) or at least one candidate matches a header.</summary>
    private static bool ResolvesHeader(List<string> headers, List<string>? candidates) =>
        candidates is null || candidates.Any(c => headers.Contains(c, StringComparer.OrdinalIgnoreCase));

    private static List<string> ReadHeaders(ViewSchedule schedule)
    {
        var headers = new List<string>();
        var definition = schedule.Definition;
        var fieldCount = definition.GetFieldCount();
        for (var i = 0; i < fieldCount; i++)
        {
            var field = definition.GetField(i);
            // ColumnHeading is the real displayed header text (what a
            // reader, and RuleConfig.PileScheduleIdHeaders/EastingHeaders/
            // NorthingHeaders, match against); GetName() is the schedulable
            // field's own internal name, used only when a field has no
            // heading text of its own (blank is a real, valid heading choice).
            var heading = field.ColumnHeading;
            headers.Add(string.IsNullOrWhiteSpace(heading) ? field.GetName() : heading);
        }

        return headers;
    }

    private static List<IReadOnlyDictionary<string, string>> ReadDataRows(ViewSchedule schedule, List<string> headers)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        var section = schedule.GetTableData().GetSectionData(SectionType.Body);
        var columnCount = Math.Min(headers.Count, section.NumberOfColumns);

        for (var r = 0; r < section.NumberOfRows; r++)
        {
            var cells = new List<string>(columnCount);
            for (var c = 0; c < columnCount; c++)
            {
                cells.Add(schedule.GetCellText(SectionType.Body, r, c));
            }

            if (IsHeaderArtifactRow(cells, headers))
            {
                continue;
            }

            var row = new Dictionary<string, string>();
            for (var c = 0; c < columnCount; c++)
            {
                row[headers[c]] = cells[c];
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>See the class remarks on why this replaces a hardcoded "skip N rows" constant.</summary>
    private static bool IsHeaderArtifactRow(List<string> cells, List<string> headers)
    {
        if (cells.All(string.IsNullOrWhiteSpace))
        {
            return true;
        }

        return cells.Count == headers.Count &&
            cells.Zip(headers, (cell, header) => string.Equals(cell.Trim(), header.Trim(), StringComparison.OrdinalIgnoreCase))
                .All(match => match);
    }

    private static string TryName(ViewSchedule schedule)
    {
        try
        {
            return schedule.Name;
        }
        catch
        {
            return "?";
        }
    }
}
