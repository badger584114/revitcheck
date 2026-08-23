namespace RevitCheck.Core.Csv;

/// <summary>
/// A parsed CSV, indexed by a chosen key column. Rows are plain
/// header-name -&gt; cell-value dictionaries - no typing or judgement here,
/// matching the rest of the IR's "extract facts, judge nothing" split.
/// </summary>
public sealed class CsvTable
{
    public required IReadOnlyList<string> Headers { get; init; }
    public required IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; init; }

    /// <summary>
    /// Looks up every row whose value in <paramref name="keyColumn"/> equals
    /// <paramref name="keyValue"/> (trimmed, ordinal comparison - a CSV key
    /// column is exported data, not something to fuzz-match). More than one
    /// row for the same key is a real, reportable ambiguity, not silently
    /// resolved here - the caller decides what to do with more than one hit.
    /// </summary>
    public List<IReadOnlyDictionary<string, string>> RowsForKey(string keyColumn, string keyValue) =>
        Rows.Where(row =>
                row.TryGetValue(keyColumn, out var value) &&
                string.Equals(value?.Trim(), keyValue.Trim(), StringComparison.Ordinal))
            .ToList();
}
