namespace RevitCheck.Core.Ir;

/// <summary>
/// A captured Revit <c>ViewSchedule</c>'s body rows, reduced to plain
/// header-&gt;cell-text dictionaries - no typing or judgement here, matching
/// <c>Csv.CsvTable</c>'s own "extract facts, judge nothing" shape. Exists
/// because the pile setout schedule is itself the live reference data for
/// <see cref="Checks.PileModelScheduleConsistencyCheck"/> - unlike
/// <c>MetadataReconciliationCheck</c>'s external CSV, there is no file to
/// upload; the schedule already lives in the document being captured.
/// </summary>
/// <remarks>
/// A real schedule read via <c>Table.GetCellText(SectionType.Body, r, c)</c>
/// includes leading header/blank rows as part of the Body section on this
/// project's real pile schedules (confirmed 2026-08-26,
/// <c>InspectPileSetout.pushbutton</c>'s real output against
/// <c>DRG-2873041 - PILE LAYOUT</c> - two such rows before real data
/// starts) - the adapter that builds this type is responsible for skipping
/// them, not this type or its consumers. <see cref="Rows"/> should contain
/// only real data rows by the time a <see cref="RevitModel"/> carries one.
/// </remarks>
public sealed class ScheduleInfo
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> Headers { get; init; }

    public required IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; }

    /// <summary>
    /// Every row whose value in <paramref name="keyColumn"/> equals
    /// <paramref name="keyValue"/> (trimmed, ordinal comparison - a schedule
    /// key like SITE ID is exported/typed data, not something to fuzz-match).
    /// More than one row for the same key is a real, reportable ambiguity,
    /// left to the caller - same split <c>CsvTable.RowsForKey</c> uses.
    /// </summary>
    public List<IReadOnlyDictionary<string, string>> RowsForKey(string keyColumn, string keyValue) =>
        Rows.Where(row =>
                row.TryGetValue(keyColumn, out var value) &&
                string.Equals(value?.Trim(), keyValue.Trim(), StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// First header matching one of <paramref name="candidates"/>
    /// (case-insensitive), or null if none is present - mirrors the old
    /// PDF/DWG pipeline's <c>ID_HEADER_CANDIDATES</c> pattern
    /// (ARCHIVE-pdf-dwg.md, <c>extraction/setout_reconstruction.py</c>):
    /// a candidate list rather than a single hardcoded name, since a
    /// schedule's column naming is a per-project convention, and no bare
    /// substring/catch-all match, since that has already been shown to
    /// false-positive on real data (a bare "ID" matching inside
    /// "WIDGET REFERENCE").
    /// </summary>
    public string? ResolveHeader(IEnumerable<string> candidates) =>
        candidates.FirstOrDefault(c => Headers.Contains(c, StringComparer.OrdinalIgnoreCase));
}
