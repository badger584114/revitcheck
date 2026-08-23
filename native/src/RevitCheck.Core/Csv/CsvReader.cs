using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace RevitCheck.Core.Csv;

/// <summary>
/// CSV-only for v1 (confirmed) - reads a whole-of-project export into a
/// <see cref="CsvTable"/>. Backed by CsvHelper for real-world quoting/
/// embedded-comma robustness on human-maintained spreadsheets, rather than a
/// hand-rolled split(','); this is a compile-time dependency, not the kind
/// of external runtime interpreter PLANNING.md §12's C# port was built to
/// eliminate.
/// </summary>
public static class CsvReader
{
    public static CsvTable ReadText(string csvText)
    {
        using var stringReader = new StringReader(csvText);
        return ReadFrom(stringReader);
    }

    public static CsvTable ReadFile(string path)
    {
        using var streamReader = new StreamReader(path);
        return ReadFrom(streamReader);
    }

    private static CsvTable ReadFrom(TextReader textReader)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // A 1000+ row whole-of-project export is exactly the kind of
            // human-maintained file that has an occasional ragged row or an
            // unexpected blank column - lenient here, not a parse failure.
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
        };

        using var csv = new CsvHelper.CsvReader(textReader, config);
        csv.Read();
        csv.ReadHeader();
        var headers = (csv.HeaderRecord ?? Array.Empty<string>()).ToList();

        var rows = new List<IReadOnlyDictionary<string, string>>();
        while (csv.Read())
        {
            // Case-insensitive: a CSV header is structural, not data, and a
            // mapping file's csv_column (or its default - the field's own
            // key, which is conventionally lowercase snake_case) shouldn't
            // have to match a spreadsheet's header casing exactly.
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                row[header] = csv.GetField(header) ?? "";
            }

            rows.Add(row);
        }

        return new CsvTable { Headers = headers, Rows = rows };
    }
}
