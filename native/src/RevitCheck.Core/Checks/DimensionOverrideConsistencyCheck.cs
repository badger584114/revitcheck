using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Compares a typed-over dimension value against what the model measures -
/// direct port of <c>checks/dimensions.py</c>'s
/// <c>check_dimension_override_consistency</c>. Kept as
/// <c>revit.dimension_override_consistency</c> for the same real-capture
/// parity-testing reason <see cref="DimensionProvenanceCheck"/> gives.
/// </summary>
public static class DimensionOverrideConsistencyCheck
{
    public const string RuleId = "revit.dimension_override_consistency";

    // Zero-width and bidi format characters. The DXF export carried a
    // literal trailing U+200E on some override text - invisible in a
    // terminal and an editor, so a valid override failed to parse for a
    // reason with no visible cause. Not yet observed coming out of the
    // Revit API, and cheap enough that finding out the hard way isn't worth it.
    private static readonly string[] FormatChars =
    {
        "​", "‌", "‍", "‎", "‏", // ZWSP, ZWNJ, ZWJ, LRM, RLM
        "‪", "‫", "‬", "‭", "‮", // LRE, RLE, PDF, LRO, RLO
    };

    // Overrides that end in a unit. Revit shows units according to the
    // dimension's own format, and a drafter retyping a value often retypes
    // the unit with it.
    private static readonly string[] UnitSuffixes = { "mm", "MM" };

    // `500 MIN.`, `MIN 500`, `1200 MAX`. A real override form: the drafter
    // is stating a limit the built work must respect, not a value they measured.
    private static readonly Regex BoundRegex = new(
        @"^(?:(?<lead>MIN|MAX)\.?\s*(?<lead_value>-?[\d.,]+)|(?<value>-?[\d.,]+)\s*(?<trail>MIN|MAX)\.?)$",
        RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> BoundComparator = new() { ["MIN"] = ">=", ["MAX"] = "<=" };

    // How many distinct unparseable override forms to name in the coverage
    // Issue. Enough to recognise a convention, not enough to bury it.
    private const int MaxListedForms = 10;

    /// <summary>
    /// A dimension's override text as a millimetre value, or null. Null
    /// means **not checkable**, never "assume it is fine" - real overrides
    /// that land here: `EQ`, `VARIES`, `TYP`, a bar-mark letter, a range, a
    /// value with a qualifier. The rule counts and reports these rather
    /// than dropping them silently.
    /// </summary>
    public static double? ParseOverrideMm(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = StripFormatCharsAndUnit(text);

        // A thousands separator is presentation, not content. A decimal
        // comma is a different convention and would need real evidence
        // before being guessed at, so only the separator form is handled:
        // "1,200" -> 1200, while "1,2" is left to fail.
        if (cleaned.Contains(","))
        {
            var lastComma = cleaned.LastIndexOf(',');
            var head = cleaned.Substring(0, lastComma);
            var tail = cleaned.Substring(lastComma + 1);
            if (head.Length > 0 && tail.Length == 3 && tail.All(char.IsDigit))
            {
                cleaned = head.Replace(",", "") + tail;
            }
        }

        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>
    /// A limit-style override as (valueMm, ">=" | "&lt;="), or null. Separate
    /// from <see cref="ParseOverrideMm"/> because the two are different
    /// claims, compared differently: an exact override says "this measures
    /// 1200" and is checked against the rounding grid; `500 MIN.` states a
    /// limit, so only measurement noise applies, never rounding slack.
    /// </summary>
    public static (double Value, string Comparator)? ParseOverrideBound(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = StripFormatCharsAndUnit(text);
        var match = BoundRegex.Match(cleaned);
        if (!match.Success)
        {
            return null;
        }

        var keyword = (match.Groups["lead"].Success ? match.Groups["lead"].Value : match.Groups["trail"].Value).ToUpperInvariant();
        var rawValue = match.Groups["lead_value"].Success ? match.Groups["lead_value"].Value : match.Groups["value"].Value;
        var value = ParseOverrideMm(rawValue);
        return value is null ? null : (value.Value, BoundComparator[keyword]);
    }

    private static string StripFormatCharsAndUnit(string text)
    {
        var cleaned = text;
        foreach (var ch in FormatChars)
        {
            cleaned = cleaned.Replace(ch, "");
        }

        cleaned = cleaned.Trim();

        foreach (var suffix in UnitSuffixes)
        {
            if (cleaned.EndsWith(suffix, StringComparison.Ordinal))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - suffix.Length).Trim();
            }
        }

        return cleaned;
    }

    private static string DimensionTier(DimensionInfo dim, RuleConfig config) =>
        !string.IsNullOrEmpty(dim.TypeName) && config.SetoutCriticalTypeNames.Contains(dim.TypeName!) ? "setout_critical" : "default";

    private static double RoundingToleranceMm(string tier, RuleConfig config)
    {
        var grid = tier == "setout_critical" ? config.RoundingGridSetoutCriticalMm : config.RoundingGridDefaultMm;
        return grid / 2.0 + config.MeasurementEpsilonMm;
    }

    public static List<Issue> Run(RevitModel model, RuleConfig config)
    {
        var issues = new List<Issue>();
        var byView = model.DimensionsByView();

        var segmentsSeen = 0;
        var overridden = 0;
        var checkedCount = 0;
        var boundsChecked = 0;
        var unparsedForms = new Dictionary<string, int>();

        foreach (var view in ViewScoping.ViewsInScope(model, config))
        {
            if (!byView.TryGetValue(view.ElementId, out var dims))
            {
                continue;
            }

            foreach (var dim in dims)
            {
                var provenance = DimensionClassification.ClassifyDimension(dim);

                for (var index = 0; index < dim.Segments.Count; index++)
                {
                    var segment = dim.Segments[index];
                    segmentsSeen++;
                    if (!segment.IsOverridden)
                    {
                        continue;
                    }

                    overridden++;

                    var statedMm = ParseOverrideMm(segment.ValueOverride);
                    var bound = statedMm is null ? ParseOverrideBound(segment.ValueOverride) : null;
                    if (statedMm is null && bound is null)
                    {
                        var form = (segment.ValueOverride ?? "").Trim();
                        unparsedForms[form] = unparsedForms.TryGetValue(form, out var count) ? count + 1 : 1;
                        continue;
                    }

                    if (segment.ValueMm is null)
                    {
                        // Revit reports no value for some spot dimension
                        // types. Nothing to compare against; not an error.
                        continue;
                    }

                    if (bound is not null)
                    {
                        checkedCount++;
                        boundsChecked++;
                        var boundIssue = BoundIssue(dim, index, view, segment, bound.Value, provenance, config);
                        if (boundIssue is not null)
                        {
                            issues.Add(boundIssue);
                        }

                        continue;
                    }

                    checkedCount++;
                    var delta = statedMm!.Value - segment.ValueMm.Value;
                    var tier = DimensionTier(dim, config);
                    var tolerance = RoundingToleranceMm(tier, config);
                    if (Math.Abs(delta) <= tolerance)
                    {
                        continue;
                    }

                    issues.Add(new Issue
                    {
                        RuleId = RuleId,
                        Category = "geometry",
                        ElementId = dim.ElementId,
                        ViewId = dim.ViewId,
                        ViewName = view.Name,
                        SheetNo = view.SheetNo,
                        UniqueId = view.SheetUniqueId ?? dim.UniqueId,
                        Severity = "high",
                        Description =
                            $"{DimensionDescriptions.SegmentLabel(dim, index)} in {DimensionDescriptions.DescribeView(view)} " +
                            $"is typed as {FormatG(statedMm.Value)}mm but measures {segment.ValueMm.Value.ToString("F1", CultureInfo.InvariantCulture)}mm " +
                            $"({delta.ToString("+0.0;-0.0;+0.0", CultureInfo.InvariantCulture)}mm, more than rounding to the " +
                            $"{tier.Replace("_", " ")} grid explains: ±{tolerance.ToString("F1", CultureInfo.InvariantCulture)}mm).",
                        SuggestedFix = new Dictionary<string, object?>
                        {
                            ["stated_mm"] = statedMm.Value,
                            ["measured_mm"] = Math.Round(segment.ValueMm.Value, 3),
                            ["delta_mm"] = Math.Round(delta, 3),
                            ["tolerance_mm"] = Math.Round(tolerance, 3),
                            ["tier"] = tier,
                            // What the measured value actually is. On a
                            // Drafted dimension it's the length of a detail
                            // line, not model geometry - so agreement here
                            // means the drafter agrees with their own
                            // linework, not the same reassurance.
                            ["provenance"] = provenance.ToString().ToLowerInvariant(),
                            ["segment"] = index + 1,
                            ["segments"] = dim.Segments.Count,
                        },
                    });
                }
            }
        }

        issues.Add(OverrideCoverageIssue(segmentsSeen, overridden, checkedCount, boundsChecked, unparsedForms));
        return issues;
    }

    /// <summary>
    /// Checks a measured value against a stated MIN/MAX limit. The rounding
    /// grid deliberately does not apply here - an exact override is a
    /// rounded restatement of a measurement, `500 MIN.` is a limit the
    /// built work has to respect, so only measurement_epsilon_mm applies.
    /// </summary>
    private static Issue? BoundIssue(
        DimensionInfo dim, int index, ViewInfo view, DimensionSegmentInfo segment,
        (double Value, string Comparator) bound, Provenance provenance, RuleConfig config)
    {
        var (limit, comparator) = bound;
        var measured = segment.ValueMm!.Value;
        var epsilon = config.MeasurementEpsilonMm;

        bool violated;
        string wording;
        if (comparator == ">=")
        {
            violated = measured < limit - epsilon;
            wording = "at least";
        }
        else
        {
            violated = measured > limit + epsilon;
            wording = "at most";
        }

        if (!violated)
        {
            return null;
        }

        return new Issue
        {
            RuleId = RuleId,
            Category = "geometry",
            ElementId = dim.ElementId,
            ViewId = dim.ViewId,
            ViewName = view.Name,
            SheetNo = view.SheetNo,
            UniqueId = view.SheetUniqueId ?? dim.UniqueId,
            Severity = "high",
            Description =
                $"{DimensionDescriptions.SegmentLabel(dim, index)} in {DimensionDescriptions.DescribeView(view)} is annotated " +
                $"as {wording} {FormatG(limit)}mm, but the model measures {measured.ToString("F1", CultureInfo.InvariantCulture)}mm " +
                "— the stated limit is not met.",
            SuggestedFix = new Dictionary<string, object?>
            {
                ["stated_limit_mm"] = limit,
                ["comparator"] = comparator,
                ["measured_mm"] = Math.Round(measured, 3),
                ["epsilon_mm"] = epsilon,
                ["provenance"] = provenance.ToString().ToLowerInvariant(),
                ["segment"] = index + 1,
                ["segments"] = dim.Segments.Count,
            },
        };
    }

    /// <summary>
    /// States how much of the model this rule could actually check.
    /// Reported unconditionally: a run with no findings and four checked
    /// segments out of nine thousand looks identical to a clean model
    /// unless this says so.
    /// </summary>
    private static Issue OverrideCoverageIssue(int segmentsSeen, int overridden, int checkedCount, int boundsChecked, Dictionary<string, int> unparsedForms)
    {
        string detail;
        if (segmentsSeen == 0)
        {
            detail = "No dimensions were found in any view in scope.";
        }
        else
        {
            detail = $"{overridden} of {segmentsSeen} dimension segments carry a typed override, and {checkedCount} of those were compared against the model.";
            if (boundsChecked > 0)
            {
                detail += $" {boundsChecked} of those stated a MIN/MAX limit rather than an exact value, and were checked against the limit.";
            }
        }

        if (unparsedForms.Count > 0)
        {
            var ordered = unparsedForms.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            var listed = string.Join(", ", ordered.Take(MaxListedForms).Select(kv => $"{PythonRepr(kv.Key)} x{kv.Value}"));
            var remainder = ordered.Count - Math.Min(ordered.Count, MaxListedForms);
            if (remainder > 0)
            {
                listed += $" (+{remainder} more distinct)";
            }

            detail += $" {unparsedForms.Values.Sum()} override(s) were not a number and were skipped rather than guessed at: {listed}.";
        }

        if (checkedCount == 0)
        {
            detail += " Nothing was compared, so this rule finding nothing means it had nothing to check — not that the dimensions are right.";
        }

        return new Issue
        {
            RuleId = RuleId,
            Category = "coverage",
            Severity = "low",
            Description = detail,
            SuggestedFix = new Dictionary<string, object?>
            {
                ["segments"] = segmentsSeen,
                ["overridden"] = overridden,
                ["checked"] = checkedCount,
                ["bounds"] = boundsChecked,
                ["unparsed"] = unparsedForms.Values.Sum(),
            },
        };
    }

    // Approximates Python's `{:g}` (general format, 6 significant digits) -
    // an exact match isn't required since this only feeds a human-readable
    // message, never a comparison (which always uses the raw double). See
    // the real-capture parity check for whether this ever actually diverges.
    private static string FormatG(double value) => value.ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>
    /// Python's <c>repr()</c> for a string, used for the unparsed-override-
    /// forms listing (Python's <c>"{0!r}"</c>). Matters for real parity, not
    /// just cosmetics: a real override on a real capture was a single
    /// invisible U+200E character, and Python's repr renders that as the
    /// visible escape <c>'‎'</c> rather than embedding the raw
    /// character - naive quoting would silently carry the invisible
    /// character into the message instead, changing the Issue's identity
    /// hash even though the underlying finding is identical. Covers the
    /// common cases (quote selection, backslash/control/format-character
    /// escaping via Unicode category, matching Python's `str.isprintable()`
    /// rule) - not a byte-perfect CPython repr port (astral-plane
    /// characters would need `\U########` and aren't handled), which
    /// realistic Revit override text never exercises.
    /// </summary>
    private static string PythonRepr(string text)
    {
        var hasSingleQuote = text.Contains('\'');
        var hasDoubleQuote = text.Contains('"');
        var quote = hasSingleQuote && !hasDoubleQuote ? '"' : '\'';

        var sb = new StringBuilder();
        sb.Append(quote);
        foreach (var ch in text)
        {
            if (ch == quote || ch == '\\')
            {
                sb.Append('\\').Append(ch);
            }
            else if (ch == '\n')
            {
                sb.Append("\\n");
            }
            else if (ch == '\r')
            {
                sb.Append("\\r");
            }
            else if (ch == '\t')
            {
                sb.Append("\\t");
            }
            else if (IsPythonPrintable(ch))
            {
                sb.Append(ch);
            }
            else if (ch <= 0xFF)
            {
                sb.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
            }
        }

        sb.Append(quote);
        return sb.ToString();
    }

    // Mirrors Python's str.isprintable(): everything is printable except
    // Unicode category Other (control/format/surrogate/private-use/
    // unassigned) and Separator (space/line/paragraph), with the ASCII
    // space (0x20) specifically exempted back in as printable.
    private static bool IsPythonPrintable(char ch)
    {
        if (ch == ' ')
        {
            return true;
        }

        return CharUnicodeInfo.GetUnicodeCategory(ch) switch
        {
            UnicodeCategory.Control => false,
            UnicodeCategory.Format => false,
            UnicodeCategory.Surrogate => false,
            UnicodeCategory.PrivateUse => false,
            UnicodeCategory.OtherNotAssigned => false,
            UnicodeCategory.SpaceSeparator => false,
            UnicodeCategory.LineSeparator => false,
            UnicodeCategory.ParagraphSeparator => false,
            _ => true,
        };
    }
}
