using System.Globalization;
using RevitCheck.Core.Csv;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Mapping;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Joins captured model elements to an external CSV of reference metadata
/// via a key parameter and a <see cref="ParameterMapping"/>, and flags
/// mismatches. This is the tool's actual job on a small (~20-30 element)
/// model checked against a whole-of-project CSV: catch the handful of items
/// that are missing or have one wrong/blank field among 30-40, which is easy
/// to miss by eye in a schedule and currently only caught by an external
/// audit after the fact.
/// </summary>
/// <remarks>
/// Deliberately asymmetric by design, confirmed with the user:
/// <list type="bullet">
/// <item>The CSV is whole-of-project (1000+ rows) against a 20-30 element
/// model, so an unmatched CSV row is expected noise, not a finding - this
/// check never reports on the CSV's excess. See
/// <see cref="MaxListed"/>-truncated coverage issues below for the only
/// aggregate reporting it does.</item>
/// <item>A model element that has its key parameter set but no matching CSV
/// row <b>is</b> reported (<see cref="ReconciliationConfig.ReportUnmatchedModelElements"/>,
/// on by default) - the "missing item" case.</item>
/// <item>A model value that is genuinely blank while the CSV has data for
/// that field is a first-class mismatch (category "metadata", normal
/// severity), not a coverage note - the "incorrectly filled" case named
/// directly by the user. The reverse (model has a value, CSV cell is blank)
/// is not reported - the CSV not tracking a field for one item isn't
/// evidence of anything wrong.</item>
/// </list>
/// A nested sub-component (<c>ElementMetadata.HostElementId</c> set) is
/// reconciled completely independently of its host - its own key value, own
/// CSV row, own field mapping resolved by its own family/category. The host
/// reference never participates in the join.
/// </remarks>
public static class MetadataReconciliationCheck
{
    public const string RuleId = "revitcheck.metadata_reconciliation";

    private const int MaxListed = 5;

    public static List<Issue> Run(RevitModel model, ParameterMapping mapping, CsvTable csv, ReconciliationConfig config)
    {
        var issues = new List<Issue>();

        var keyCsvColumn = mapping.ResolvedKeyCsvColumn;
        if (mapping.DisambiguationField is null)
        {
            // With a DisambiguationField configured, more than one row per
            // key is expected structure (one per discipline package, etc.),
            // not an anomaly - real remaining ambiguity is reported per
            // element in ResolveMatchingRow instead, only when picking a row
            // actually stayed ambiguous after disambiguating.
            ReportDuplicateCsvKeys(csv, keyCsvColumn, issues);
        }

        var skippedFields = ReportUnresolvableCsvColumns(mapping, csv, issues);

        var blankKeyElementIds = new List<long>();

        foreach (var element in model.Elements)
        {
            var keyValue = ResolveKeyValue(element, mapping, config);
            if (keyValue is null)
            {
                blankKeyElementIds.Add(element.ElementId);
                continue;
            }

            var matches = csv.RowsForKey(keyCsvColumn, keyValue);
            if (matches.Count == 0)
            {
                if (config.ReportUnmatchedModelElements)
                {
                    issues.Add(new Issue
                    {
                        RuleId = RuleId,
                        Category = "metadata",
                        Severity = config.DefaultSeverity,
                        ElementId = element.ElementId,
                        UniqueId = element.UniqueId,
                        Description =
                            $"Element has key '{keyValue}' but no matching row was found in the " +
                            "reference CSV.",
                    });
                }

                continue;
            }

            var row = ResolveMatchingRow(element, keyValue, mapping, matches, config, issues);
            if (row is null)
            {
                // Disambiguation found no row for this element's own
                // discipline (or whichever field disambiguates) - already
                // reported inside ResolveMatchingRow. Comparing fields
                // against a wrong-package row would only produce false
                // mismatches, so skip it entirely rather than guess.
                continue;
            }

            foreach (var entry in mapping.Fields)
            {
                var fieldName = entry.Key;
                var field = entry.Value;
                if (skippedFields.Contains(fieldName))
                {
                    continue;
                }

                CompareField(element, keyValue, fieldName, field, row, config, issues);
            }
        }

        if (blankKeyElementIds.Count > 0)
        {
            issues.Add(BuildBlankKeyIssue(blankKeyElementIds));
        }

        return issues;
    }

    private static void ReportDuplicateCsvKeys(CsvTable csv, string keyCsvColumn, List<Issue> issues)
    {
        var duplicateCount = csv.Rows
            .Where(row => row.ContainsKey(keyCsvColumn) && !string.IsNullOrWhiteSpace(row[keyCsvColumn]))
            .GroupBy(row => row[keyCsvColumn].Trim(), StringComparer.Ordinal)
            .Count(group => group.Count() > 1);

        if (duplicateCount == 0)
        {
            return;
        }

        issues.Add(new Issue
        {
            RuleId = RuleId,
            Category = "coverage",
            Severity = "medium",
            Description =
                $"{duplicateCount} key value(s) appear on more than one row of the reference CSV - " +
                "the first row found for each was used, not a chosen one.",
        });
    }

    /// <summary>
    /// Picks which of possibly several key-matched CSV rows is the right
    /// one for <paramref name="element"/>, using
    /// <see cref="ParameterMapping.DisambiguationField"/> when configured -
    /// see its docstring for why (Asset Classification's real multi-
    /// discipline-package rows). Returns null when disambiguation
    /// determined none of the candidate rows is actually a match for this
    /// element (an issue is added explaining why); the caller should treat
    /// that the same as "no matching row", not fall through to comparing
    /// fields against a row that was deliberately ruled out.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ResolveMatchingRow(
        ElementMetadata element, string keyValue, ParameterMapping mapping,
        List<IReadOnlyDictionary<string, string>> matches, ReconciliationConfig config, List<Issue> issues)
    {
        if (matches.Count == 1 ||
            mapping.DisambiguationField is not { } disambigFieldName ||
            !mapping.Fields.TryGetValue(disambigFieldName, out var disambigField))
        {
            // No disambiguation configured, or only one candidate anyway -
            // first (only) match, same as before this existed.
            return matches[0];
        }

        var disambigParamName = disambigField.ResolveParameterName(element);
        if (disambigParamName is null || !element.Parameters.TryGetValue(disambigParamName, out var disambigValue))
        {
            // Can't tell which package this element belongs to - don't
            // guess which row is right, fall back to the original
            // first-match behaviour.
            return matches[0];
        }

        var modelValue = (disambigValue.DisplayString ?? disambigValue.RawString ?? "").Trim();
        if (modelValue.Length == 0)
        {
            return matches[0];
        }

        var disambigCsvColumn = disambigField.CsvColumn ?? disambigFieldName;
        var comparison = disambigField.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var filtered = matches
            .Where(r => r.TryGetValue(disambigCsvColumn, out var v) && string.Equals(v.Trim(), modelValue, comparison))
            .ToList();

        if (filtered.Count == 1)
        {
            return filtered[0];
        }

        if (filtered.Count == 0)
        {
            var available = string.Join(", ", matches
                .Select(r => r.TryGetValue(disambigCsvColumn, out var v) ? v.Trim() : "")
                .Where(v => v.Length > 0)
                .Distinct());
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "metadata",
                Severity = config.DefaultSeverity,
                ElementId = element.ElementId,
                UniqueId = element.UniqueId,
                Description =
                    $"Element has key '{keyValue}' with {disambigFieldName}='{modelValue}', but the " +
                    $"reference CSV has no row for this key under that value - only: {available} " +
                    $"(key={keyValue}).",
            });
            return null;
        }

        // filtered.Count > 1: genuinely ambiguous even after disambiguation
        // - the same key AND the same disambiguation value on more than one
        // row. First one used deterministically, flagged rather than
        // silently picked.
        issues.Add(new Issue
        {
            RuleId = RuleId,
            Category = "coverage",
            Severity = "medium",
            ElementId = element.ElementId,
            UniqueId = element.UniqueId,
            Description =
                $"Element has key '{keyValue}' with {disambigFieldName}='{modelValue}', matching " +
                $"{filtered.Count} rows in the reference CSV - the first was used, not a chosen one " +
                $"(key={keyValue}).",
        });
        return filtered[0];
    }

    /// <summary>
    /// A field's CSV column is either present in this CSV or it isn't -
    /// checking that once up front (rather than once per element) avoids
    /// reporting the same missing column dozens of times.
    /// </summary>
    private static HashSet<string> ReportUnresolvableCsvColumns(ParameterMapping mapping, CsvTable csv, List<Issue> issues)
    {
        var skipped = new HashSet<string>();
        foreach (var entry in mapping.Fields)
        {
            var column = entry.Value.CsvColumn ?? entry.Key;
            if (csv.Headers.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            skipped.Add(entry.Key);
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "medium",
                Description =
                    $"Field '{entry.Key}' maps to CSV column '{column}', which is not present in this " +
                    "CSV - this field could not be checked for any element.",
            });
        }

        return skipped;
    }

    private static string? ResolveKeyValue(ElementMetadata element, ParameterMapping mapping, ReconciliationConfig config)
    {
        if (!element.Parameters.TryGetValue(mapping.KeyParameterName, out var value))
        {
            return null;
        }

        var raw = value.RawString ?? value.DisplayString;
        // netstandard2.0's string surface predates nullable-analysis
        // attributes, so IsNullOrWhiteSpace can't narrow `raw` here even
        // though this is a plain null/blank check.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw!.Trim();
        return config.BlankKeySentinels.Contains(trimmed) ? null : trimmed;
    }

    private static void CompareField(
        ElementMetadata element,
        string keyValue,
        string fieldName,
        FieldMapping field,
        IReadOnlyDictionary<string, string> csvRow,
        ReconciliationConfig config,
        List<Issue> issues)
    {
        var parameterName = field.ResolveParameterName(element);
        if (parameterName is null)
        {
            issues.Add(CoverageIssue(element,
                $"Field '{fieldName}' has no resolvable Revit parameter for family " +
                $"'{element.FamilyName ?? "(none)"}' - no override matched and no default parameter is set."));
            return;
        }

        if (!element.Parameters.TryGetValue(parameterName, out var paramValue))
        {
            issues.Add(CoverageIssue(element,
                $"Field '{fieldName}' resolves to Revit parameter '{parameterName}', but this element has " +
                "no such parameter."));
            return;
        }

        var csvColumn = field.CsvColumn ?? fieldName;
        var csvRaw = csvRow.TryGetValue(csvColumn, out var v) ? v : "";
        var csvIsBlank = string.IsNullOrWhiteSpace(csvRaw);
        var modelIsBlank = IsModelValueBlank(paramValue, field.Comparison);

        if (modelIsBlank && csvIsBlank)
        {
            if (field.RequireModelValue)
            {
                // A CSV data gap doesn't excuse the model from still
                // needing its own explicit value (e.g. "N/A") - this field's
                // convention is never a truly unset parameter.
                issues.Add(MismatchIssue(element, keyValue, fieldName, config,
                    $"{fieldName}: model value is blank - this field is expected to always have an " +
                    $"explicit value, even if that's just 'N/A' (key={keyValue})",
                    modelValue: null, csvValue: csvRaw));
            }

            // Otherwise: nothing to compare on either side - not this
            // tool's job to guess whether that's expected.
            return;
        }

        if (modelIsBlank)
        {
            // The "incorrectly filled" case named directly by the user: a
            // real finding, not a coverage note.
            issues.Add(MismatchIssue(element, keyValue, fieldName, config,
                $"{fieldName}: model value is blank, spreadsheet says '{csvRaw}' (key={keyValue})",
                modelValue: null, csvValue: csvRaw));
            return;
        }

        if (csvIsBlank)
        {
            // The CSV not tracking this field for this item isn't evidence
            // of anything wrong - no CSV-side coverage reporting, by design.
            return;
        }

        switch (field.Comparison)
        {
            case ComparisonType.Numeric:
                CompareNumeric(element, keyValue, fieldName, field, paramValue, csvRaw, config, issues);
                break;
            case ComparisonType.ContainsCsvValue:
                CompareContainsCsvValue(element, keyValue, fieldName, field, paramValue, csvRaw, config, issues);
                break;
            default:
                CompareExactString(element, keyValue, fieldName, field, paramValue, csvRaw, config, issues);
                break;
        }
    }

    private static void CompareNumeric(
        ElementMetadata element, string keyValue, string fieldName, FieldMapping field,
        ParameterValue paramValue, string csvRaw, ReconciliationConfig config, List<Issue> issues)
    {
        if (paramValue.NumericValue is not { } modelValue)
        {
            issues.Add(CoverageIssue(element,
                $"Field '{fieldName}' is mapped as numeric, but the model value could not be read as a " +
                "number."));
            return;
        }

        if (!double.TryParse(csvRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var csvValue))
        {
            issues.Add(CoverageIssue(element,
                $"Field '{fieldName}': spreadsheet value '{csvRaw}' could not be read as a number."));
            return;
        }

        var tolerance = field.ToleranceMm ?? 0.0;
        if (Math.Abs(modelValue - csvValue) > tolerance)
        {
            issues.Add(MismatchIssue(element, keyValue, fieldName, config,
                $"{fieldName}: model says '{FormatNumber(modelValue)}', spreadsheet says " +
                $"'{FormatNumber(csvValue)}' (key={keyValue})",
                modelValue: FormatNumber(modelValue), csvValue: FormatNumber(csvValue)));
        }
    }

    private static void CompareExactString(
        ElementMetadata element, string keyValue, string fieldName, FieldMapping field,
        ParameterValue paramValue, string csvRaw, ReconciliationConfig config, List<Issue> issues)
    {
        var modelText = (paramValue.DisplayString ?? paramValue.RawString ?? "").Trim();
        var csvText = csvRaw.Trim();
        var comparison = field.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!string.Equals(modelText, csvText, comparison))
        {
            issues.Add(MismatchIssue(element, keyValue, fieldName, config,
                $"{fieldName}: model says '{modelText}', spreadsheet says '{csvText}' (key={keyValue})",
                modelValue: modelText, csvValue: csvText));
        }
    }

    /// <summary>
    /// ComparisonType.ContainsCsvValue - see its own docstring. Splits the
    /// model's semicolon-separated list and checks whether the CSV's single
    /// value matches any entry, not whether the two strings are identical.
    /// </summary>
    private static void CompareContainsCsvValue(
        ElementMetadata element, string keyValue, string fieldName, FieldMapping field,
        ParameterValue paramValue, string csvRaw, ReconciliationConfig config, List<Issue> issues)
    {
        var modelText = paramValue.DisplayString ?? paramValue.RawString ?? "";
        var csvText = csvRaw.Trim();
        var comparison = field.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var modelEntries = modelText
            .Split(';')
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .ToList();

        if (modelEntries.Any(entry => string.Equals(entry, csvText, comparison)))
        {
            return;
        }

        issues.Add(MismatchIssue(element, keyValue, fieldName, config,
            $"{fieldName}: model list '{modelText.Trim()}' does not contain '{csvText}' from the spreadsheet " +
            $"(key={keyValue})",
            modelValue: modelText.Trim(), csvValue: csvText));
    }

    private static Issue MismatchIssue(
        ElementMetadata element, string keyValue, string fieldName, ReconciliationConfig config,
        string description, string? modelValue, string csvValue) =>
        new()
        {
            RuleId = RuleId,
            Category = "metadata",
            Severity = config.SeverityFor(fieldName),
            ElementId = element.ElementId,
            UniqueId = element.UniqueId,
            Description = description,
            SuggestedFix = new Dictionary<string, object?> { ["csv_value"] = csvValue, ["model_value"] = modelValue },
        };

    private static Issue CoverageIssue(ElementMetadata element, string description) => new()
    {
        RuleId = RuleId,
        Category = "coverage",
        Severity = "medium",
        ElementId = element.ElementId,
        UniqueId = element.UniqueId,
        Description = description,
    };

    private static Issue BuildBlankKeyIssue(List<long> elementIds)
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
                $"{elementIds.Count} element(s) have no value for the key parameter and could not be " +
                $"matched to the reference CSV at all: {ids}",
        };
    }

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool IsModelValueBlank(ParameterValue value, ComparisonType comparison) =>
        comparison == ComparisonType.Numeric
            ? value.NumericValue is null
            : string.IsNullOrWhiteSpace(value.DisplayString) && string.IsNullOrWhiteSpace(value.RawString);
}
