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
/// <b>Deliberately reads every schedule's full body text, not just ones
/// whose name looks pile-related.</b> <c>InspectPileSetout.pushbutton</c>'s
/// diagnostic only read full cell data for name-filtered candidates
/// (PLANNING.md §14) - reasonable for a one-off report a human reads, but
/// filtering by name is exactly the kind of classification/judgement the
/// adapter-boundary rule reserves for <c>Checks/</c>
/// (<c>PileModelScheduleConsistencyCheck</c> already does its own filtering,
/// by which schedules actually resolve the expected id/Easting/Northing
/// headers - not by name). The real cost tradeoff is honest: on a schedule-heavy
/// document this reads more cell text than strictly needed. No real case has
/// shown this to matter yet; worth revisiting with real timing data before
/// adding an adapter-level name filter back in as an opt-in, not a default.
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
    /// <summary>Every ViewSchedule in <paramref name="doc"/>, headers plus real data rows. Per-schedule extraction failures are isolated - see <paramref name="errors"/>.</summary>
    public static List<ScheduleInfo> Collect(Document doc, List<string> errors)
    {
        var schedules = new List<ScheduleInfo>();

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
                schedules.Add(ReadSchedule(schedule));
            }
            catch (Exception ex)
            {
                errors.Add($"schedule {schedule.Id.Value} ({TryName(schedule)}): {ex.Message}");
            }
        }

        return schedules;
    }

    private static ScheduleInfo ReadSchedule(ViewSchedule schedule)
    {
        var headers = ReadHeaders(schedule);
        var rows = ReadDataRows(schedule, headers);

        return new ScheduleInfo
        {
            Name = schedule.Name,
            Headers = headers,
            Rows = rows,
        };
    }

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
