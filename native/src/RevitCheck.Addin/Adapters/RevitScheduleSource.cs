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
/// <b>Body rows are read off the schedule's own backing elements, not the
/// rendered table text - added 2026-08-28, after a real run of the
/// header-filtered version above found every one of 43 real piles failing
/// its schedule join with "no matching row was found."</b> The rendered
/// table (<see cref="ScheduleField.ColumnHeading"/>/<c>GetCellText</c>) is
/// Revit's own *display* of the data, formatted, sorted, and grouped for a
/// human reader - matching it back to a raw parameter's own text is exactly
/// the kind of format-fragile extraction this project's own history
/// (ARCHIVE-pdf-dwg.md) already learned not to trust. The real fix, per the
/// user's own suggestion: a normal schedule's rows correspond to real
/// elements, retrievable directly via <c>FilteredElementCollector(doc,
/// schedule.Id)</c> - a genuine, confirmed Revit API pattern (every member
/// used here was checked against the real <c>RevitAPI.dll</c> this project
/// builds against before writing this, not assumed). Each candidate
/// column's real bound parameter is resolved via
/// <see cref="ScheduleField.ParameterId"/> and read directly off each
/// backing element - the same kind of pure parameter read
/// <c>RevitMetadataElementSource</c> already does for piles - sidestepping
/// rendered-text formatting, the row-skip heuristic below, and row
/// ordering entirely. See <see cref="TryReadDataRowsFromElements"/> for the
/// mechanics and <see cref="ReadDataRowsFromCellText"/> (kept as a
/// fallback for a calculated/combined column, or the unfiltered mode) for
/// what this replaces as the default path.
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
                var isCandidate = !filtered ||
                    (ResolvesHeader(headers, idCandidates) &&
                     ResolvesHeader(headers, eastingCandidates) &&
                     ResolvesHeader(headers, northingCandidates));

                List<IReadOnlyDictionary<string, string>> rows;
                if (!isCandidate)
                {
                    rows = new List<IReadOnlyDictionary<string, string>>();
                }
                else
                {
                    // Element-parameter-based read first (see class remarks:
                    // a real 2026-08-28 bug found the rendered-table text
                    // path producing a 100% id-join failure) - falls back to
                    // the rendered-table read only when a column can't be
                    // resolved to a simple parameter (a calculated/combined
                    // field, or FilteredElementCollector coming back empty).
                    rows = (filtered
                        ? TryReadDataRowsFromElements(doc, schedule, headers, idCandidates!, eastingCandidates!, northingCandidates!)
                        : null)
                        ?? ReadDataRowsFromCellText(schedule, headers);
                }

                schedules.Add(new ScheduleInfo
                {
                    Name = schedule.Name,
                    Headers = headers,
                    Rows = rows,
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

    /// <summary>
    /// Reads each schedule column's real, resolved value directly off the
    /// schedule's own backing elements - <see cref="FilteredElementCollector(Document, ElementId)"/>
    /// scoped to the schedule's own <c>ElementId</c>, a genuine, confirmed
    /// Revit API pattern (verified against the real <c>RevitAPI.dll</c>
    /// this project builds against, not guessed) for "which real elements
    /// does this schedule include". Added 2026-08-28 after a real run
    /// showed the rendered-table read (<see cref="ReadDataRowsFromCellText"/>)
    /// producing a 100% id-join failure for reasons the check's own issue
    /// text alone couldn't diagnose - reading each element's own parameter
    /// directly (<see cref="Element.get_Parameter(Definition)"/>/
    /// <see cref="Element.get_Parameter(BuiltInParameter)"/>) sidesteps
    /// rendered-text formatting, the header-artifact-row heuristic, and row
    /// ordering entirely - there is no "row index" here, one element is one
    /// row.
    /// </summary>
    /// <remarks>
    /// Returns null - not an empty list - when this path genuinely can't be
    /// used (a column is calculated/combined rather than a simple
    /// parameter, or the schedule's backing-element sweep comes back
    /// empty), so the caller falls back to
    /// <see cref="ReadDataRowsFromCellText"/> rather than silently reporting
    /// "zero rows" for a schedule that may well have real data the
    /// rendered-table path could still find.
    /// </remarks>
    private static List<IReadOnlyDictionary<string, string>>? TryReadDataRowsFromElements(
        Document doc,
        ViewSchedule schedule,
        List<string> headers,
        List<string> idCandidates,
        List<string> eastingCandidates,
        List<string> northingCandidates)
    {
        var idHeader = ResolveHeaderText(headers, idCandidates);
        var eastingHeader = ResolveHeaderText(headers, eastingCandidates);
        var northingHeader = ResolveHeaderText(headers, northingCandidates);
        if (idHeader is null || eastingHeader is null || northingHeader is null)
        {
            return null;
        }

        var definition = schedule.Definition;
        var idParam = ResolveFieldParameter(doc, definition, idHeader);
        var eastingParam = ResolveFieldParameter(doc, definition, eastingHeader);
        var northingParam = ResolveFieldParameter(doc, definition, northingHeader);
        if (idParam is null || eastingParam is null || northingParam is null)
        {
            return null;
        }

        List<Element> elements;
        try
        {
            elements = new FilteredElementCollector(doc, schedule.Id).WhereElementIsNotElementType().ToList();
        }
        catch
        {
            return null;
        }

        if (elements.Count == 0)
        {
            return null;
        }

        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var element in elements)
        {
            var idValue = ReadParameterText(element, idParam.Value);
            if (idValue is null)
            {
                // No value for the id column on this element - nothing to
                // join it against, skip rather than emit a blank key.
                continue;
            }

            rows.Add(new Dictionary<string, string>
            {
                [idHeader] = idValue,
                [eastingHeader] = ReadParameterText(element, eastingParam.Value) ?? "",
                [northingHeader] = ReadParameterText(element, northingParam.Value) ?? "",
            });
        }

        return rows;
    }

    private static string? ResolveHeaderText(List<string> headers, List<string> candidates) =>
        headers.FirstOrDefault(h => candidates.Contains(h, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the real <see cref="Definition"/>/<see cref="BuiltInParameter"/>
    /// a schedule column is bound to, or null if it isn't a simple
    /// parameter-bound field at all (a calculated or combined-parameter
    /// field - <see cref="ScheduleField.IsCalculatedField"/>/
    /// <see cref="ScheduleField.IsCombinedParameterField"/>) or its
    /// <see cref="ScheduleField.ParameterId"/> doesn't resolve to a real
    /// <see cref="ParameterElement"/> for a project/shared parameter.
    /// Deliberately does not assume a fixed real-world parameter name (e.g.
    /// this project's own <c>DIT_SiteID</c>) - resolves whatever parameter
    /// the schedule's own column is actually bound to, so this works
    /// regardless of naming convention and never silently reads the wrong
    /// parameter for a differently-configured schedule.
    /// </summary>
    private static ResolvedScheduleParameter? ResolveFieldParameter(Document doc, ScheduleDefinition definition, string headerText)
    {
        var fieldCount = definition.GetFieldCount();
        for (var i = 0; i < fieldCount; i++)
        {
            var field = definition.GetField(i);
            var heading = field.ColumnHeading;
            var name = string.IsNullOrWhiteSpace(heading) ? field.GetName() : heading;
            if (!string.Equals(name, headerText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (field.IsCalculatedField || field.IsCombinedParameterField || !field.HasSchedulableField)
            {
                return null;
            }

            var parameterId = field.ParameterId;
            if (parameterId is null || parameterId == ElementId.InvalidElementId)
            {
                return null;
            }

            if (parameterId.Value < 0)
            {
                return new ResolvedScheduleParameter { BuiltIn = (BuiltInParameter)(int)parameterId.Value };
            }

            return doc.GetElement(parameterId) is ParameterElement paramElement
                ? new ResolvedScheduleParameter { RealDefinition = paramElement.GetDefinition() }
                : null;
        }

        return null;
    }

    private static string? ReadParameterText(Element element, ResolvedScheduleParameter identity)
    {
        var parameter = identity.RealDefinition is not null
            ? element.get_Parameter(identity.RealDefinition)
            : element.get_Parameter(identity.BuiltIn);

        if (parameter is null || !parameter.HasValue)
        {
            return null;
        }

        // AsValueString - the same formatted text (units/rounding applied)
        // a schedule cell displays for this parameter - falling back to
        // AsString for a plain text parameter that has no display-string
        // formatting of its own.
        return parameter.AsValueString() ?? parameter.AsString();
    }

    /// <summary>Which of the two shapes a resolved schedule column's real parameter takes - exactly one of the two fields is set.</summary>
    private readonly struct ResolvedScheduleParameter
    {
        public BuiltInParameter BuiltIn { get; init; }
        public Definition? RealDefinition { get; init; }
    }

    /// <summary>The original, rendered-table-text read - kept as the fallback for a column <see cref="TryReadDataRowsFromElements"/> can't resolve to a simple parameter, and for the unfiltered "read every schedule unconditionally" mode.</summary>
    private static List<IReadOnlyDictionary<string, string>> ReadDataRowsFromCellText(ViewSchedule schedule, List<string> headers)
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
