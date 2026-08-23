using System.Globalization;
using System.Text.RegularExpressions;
using RevitCheck.Core.Csv;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Mapping;

namespace RevitCheck.MappingBuilder;

/// <summary>
/// Builds a starter <see cref="ParameterMapping"/> from a real capture and a
/// real CSV's headers - the "removes the typing and the searching, never the
/// judgement" tool described in the plan. Auto-matches identical
/// (case-insensitive) column/parameter names; for everything else, prints a
/// diagnostic listing the parameter names actually present per distinct
/// (category, family) pair so a human resolves the hard, family-varying
/// cases against a curated shortlist instead of a raw 30-40-parameter
/// firehose. It never writes an <c>overrides</c> entry itself, and every
/// auto-matched numeric field is left with no tolerance - deliberately: a
/// numeric field with no tolerance fails <see cref="ParameterMappingSerializer"/>'s
/// load-time validation, so the output cannot be used by the real
/// reconciliation check until a human has actually looked at it and filled
/// in an engineering judgement call this tool cannot make.
/// </summary>
public static class MappingAutoBuilder
{
    public sealed class Result
    {
        public required ParameterMapping Mapping { get; init; }
        public required List<string> Diagnostics { get; init; }
    }

    public static Result Build(RevitModel model, CsvTable csv, string keyParameterName, string? keyCsvColumn = null)
    {
        var resolvedKeyCsvColumn = keyCsvColumn ?? keyParameterName;

        var parameterNamesByLower = model.Elements
            .SelectMany(e => e.Parameters.Keys)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var fields = new Dictionary<string, FieldMapping>();
        var diagnostics = new List<string>();

        foreach (var header in csv.Headers)
        {
            if (string.Equals(header, resolvedKeyCsvColumn, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var canonicalField = ToCanonicalFieldName(header);

            if (parameterNamesByLower.TryGetValue(header, out var matchedParameter))
            {
                var comparison = InferComparisonType(csv, header);
                fields[canonicalField] = new FieldMapping
                {
                    Comparison = comparison,
                    CsvColumn = header,
                    DefaultParameter = matchedParameter,
                };

                diagnostics.Add(comparison == ComparisonType.Numeric
                    ? $"'{header}' auto-matched to parameter '{matchedParameter}' as numeric - set tolerance_mm on '{canonicalField}' before use."
                    : $"'{header}' auto-matched to parameter '{matchedParameter}' as exact_string.");
            }
            else
            {
                diagnostics.Add($"'{header}' has no exact parameter-name match anywhere in the capture.");
                foreach (var group in DistinctFamilies(model))
                {
                    diagnostics.Add($"    ({group.Category ?? "?"} / {group.FamilyName ?? "?"}): {group.ParameterNames}");
                }
            }
        }

        var mapping = new ParameterMapping
        {
            KeyParameterName = keyParameterName,
            KeyCsvColumn = resolvedKeyCsvColumn,
            Fields = fields,
            Note =
            {
                "Auto-generated starter mapping - UNREVIEWED. Every auto-matched numeric field has no " +
                "tolerance_mm yet on purpose; the mapping cannot be loaded by the real check until a " +
                "human fills those in and confirms the rest against real data.",
            },
        };

        return new Result { Mapping = mapping, Diagnostics = diagnostics };
    }

    private static IEnumerable<(string? Category, string? FamilyName, string ParameterNames)> DistinctFamilies(RevitModel model) =>
        model.Elements
            .GroupBy(e => (e.Category, e.FamilyName))
            .Select(g => (
                g.Key.Category,
                g.Key.FamilyName,
                ParameterNames: string.Join(", ", g.SelectMany(e => e.Parameters.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))));

    private static ComparisonType InferComparisonType(CsvTable csv, string header)
    {
        var samples = csv.Rows
            .Select(row => row.TryGetValue(header, out var value) ? value : "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(20)
            .ToList();

        return samples.Count > 0 && samples.All(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            ? ComparisonType.Numeric
            : ComparisonType.ExactString;
    }

    private static string ToCanonicalFieldName(string header)
    {
        var chars = header.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var collapsed = Regex.Replace(new string(chars), "_+", "_").Trim('_');
        return collapsed.Length > 0 ? collapsed : "field";
    }
}
