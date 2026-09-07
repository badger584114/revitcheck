namespace RevitCheck.Core.Ir;

/// <summary>
/// One captured schedule row: the element it came from, plus its cell
/// values by header. Extracts facts and judges nothing, matching
/// <c>Csv.CsvTable</c>'s own shape.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="ElementId"/> is the point of this type.</b> A normal
/// Revit schedule row <em>is</em> an element - the adapter reaches the
/// rows through <c>FilteredElementCollector(doc, schedule.Id)</c> and
/// therefore already holds each row's real element. Before 2026-09-07 it
/// discarded that and the check re-derived the same link by matching a key
/// parameter's text against an id column's text, which is a strictly worse
/// way to know something the model already stated: it needs the schedule to
/// carry an id column at all, needs that column's heading to be recognised,
/// needs the key parameter's name to be recognised, and needs both sides to
/// render their text identically. Every one of those has failed on real
/// data - the last of them four separate times inside one day
/// (PLANNING.md §16). Carrying the ElementId makes the join what the model
/// already knows rather than something reconstructed from display text,
/// the same principle as CLAUDE.md's "raw internal data over rendered
/// text", applied to identity rather than to values.
/// </para>
/// <para>
/// Null when a row genuinely has no backing element - the rendered-table
/// fallback path (<c>RevitScheduleSource.ReadDataRowsFromCellText</c>)
/// reads text with no element behind it, and a key/calculated schedule may
/// have none either. A null here means "join by identity is not available
/// for this row", never "this row belongs to no element".
/// </para>
/// </remarks>
public sealed class ScheduleRow
{
    /// <summary>The real element this row describes, or null when the row came from a path that has no element behind it.</summary>
    public long? ElementId { get; init; }

    public required IReadOnlyDictionary<string, string> Values { get; init; }

    public string? Value(string header) =>
        Values.TryGetValue(header, out var value) ? value : null;
}
