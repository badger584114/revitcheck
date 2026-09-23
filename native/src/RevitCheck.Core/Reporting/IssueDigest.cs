using System.Collections.Generic;
using System.Globalization;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Reporting;

/// <summary>
/// One short line describing a finding, for a list a person reads at a
/// glance - the checklist window's details pane.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added 2026-09-23, per the user: the triage details were "way too long
/// and unable to be read".</b> The pane rendered the full
/// <see cref="Issue.Description"/> into a single-line column. On the
/// committed real capture the commonest finding's description runs to a
/// median of 244 characters, a view rollup's to 389 and the override
/// coverage note's to 523 - so every row read as a truncated fragment.
/// </para>
/// <para>
/// <b>Built from the finding's own structured data, never by shortening its
/// prose.</b> Scraping a summary back out of rendered text is the move this
/// project forbids everywhere else, and it would come apart the moment a
/// description was reworded. That choice is also what makes this safe:
/// <see cref="Issue.IssueId"/> hashes the description, so leaving every
/// description exactly as it is renames no finding and leaves the real-capture
/// parity fixture untouched. <see cref="Issue.SuggestedFix"/> is excluded
/// from that hash by design, so reading it costs nothing.
/// </para>
/// <para>
/// <b>The pane is already scoped to one view</b> and carries Section, Rule,
/// Severity and Element as their own columns - so a line here says only what
/// those do not. The view name and sheet number that open almost every
/// description are pure repetition in that context, and dropping them is
/// most of the saving.
/// </para>
/// <para>
/// <b>Where a finding keeps its prose.</b> The line is whether it is about an
/// element or about the run. "This dimension is 103mm out" has an exact
/// structured form; "121 overrides were not a number and were skipped" is
/// the explanation itself, and a coverage note carrying no structured counts
/// has no shorter honest equivalent - so it is returned whole. An
/// unrecognised rule falls back the same way, which means a future check
/// renders in full rather than blank.
/// </para>
/// </remarks>
public static class IssueDigest
{
    /// <summary>A one-line form of <paramref name="issue"/> for a narrow column.</summary>
    public static string ShortLine(Issue issue)
    {
        if (issue.SuggestedFix is null || issue.SuggestedFix.Count == 0)
        {
            return issue.Description;
        }

        return (issue.RuleId switch
        {
            DimensionProvenanceCheck.RuleId => Provenance(issue),
            DimensionOverrideConsistencyCheck.RuleId => Override(issue),
            _ => Comparison(issue),
        }) ?? issue.Description;
    }

    /// <summary>
    /// Triage: a whole-view rollup, or one dimension's own verdict. The
    /// rollup's <c>dimension_types</c> is the "which check settles this"
    /// answer <see cref="DimensionResolution.Describe"/> already composes, so
    /// it is used rather than recomputed.
    /// </summary>
    private static string? Provenance(Issue issue)
    {
        if (Text(issue, "scope") == "view")
        {
            var drafted = Text(issue, "drafted_dimensions");
            var total = Text(issue, "dimensions");
            if (drafted is null || total is null)
            {
                return null;
            }

            var types = Text(issue, "dimension_types");
            return $"{drafted} of {total} drafted" + (types is null ? "" : $" - {types}");
        }

        return Text(issue, "provenance") switch
        {
            "drafted" => "Drafted - measures detail linework",
            "mixed" => "Mixed - model one end, linework the other",
            "unknown" => "References unresolved - not checked",
            _ => null,
        };
    }

    /// <summary>
    /// An overridden value against the model: an exact restatement, a stated
    /// MIN/MAX limit, or the rule's own coverage note.
    /// </summary>
    private static string? Override(Issue issue)
    {
        var measured = Number(issue, "measured_mm");

        if (Number(issue, "stated_mm") is { } stated && measured is { } m)
        {
            var delta = Number(issue, "delta_mm");
            return Segment(issue) +
                   $"typed {Mm(stated)}mm, measures {Mm(m)}mm" +
                   (delta is null ? "" : $" ({Signed(delta.Value)}mm)");
        }

        if (Number(issue, "stated_limit_mm") is { } limit && measured is { } lm)
        {
            var wording = Text(issue, "comparator") == "<=" ? "at most" : "at least";
            return Segment(issue) + $"stated {wording} {Mm(limit)}mm, measures {Mm(lm)}mm";
        }

        // The run-level coverage note - the single longest line in a real
        // run, and entirely reconstructable from its own counts.
        if (Text(issue, "segments") is { } segments && Text(issue, "overridden") is { } overridden)
        {
            var line = $"{overridden} of {segments} segments overridden, {Text(issue, "checked") ?? "0"} compared";
            var unparsed = Number(issue, "unparsed");
            return unparsed is > 0 ? line + $", {Mm(unparsed.Value)} not a number" : line;
        }

        return null;
    }

    /// <summary>
    /// The shape every check that compares a stated value against the model
    /// shares - the drafted-dimension, pile-dimension and spot-elevation
    /// checks all report <c>stated_mm</c>/<c>model_mm</c>/<c>delta_mm</c>.
    /// </summary>
    private static string? Comparison(Issue issue)
    {
        if (Number(issue, "stated_mm") is not { } stated || Number(issue, "model_mm") is not { } model)
        {
            return null;
        }

        var delta = Number(issue, "delta_mm");
        return $"states {Mm(stated)}mm, model {Mm(model)}mm" + (delta is null ? "" : $" - {Mm(delta.Value)}mm out");
    }

    /// <summary>"Seg 2/2: " for a chain, nothing for a plain dimension - a chain's element id alone does not say which number is meant.</summary>
    private static string Segment(Issue issue)
    {
        var segments = Number(issue, "segments");
        var index = Text(issue, "segment");
        return segments is > 1 && index is not null ? $"Seg {index}/{Mm(segments.Value)}: " : "";
    }

    /// <summary>
    /// A field as text. Read via <c>ToString()</c> rather than a cast
    /// because a session reloaded from disk holds every value as a
    /// <c>JsonElement</c>, not its original CLR type - the round-trip that
    /// silently emptied two readers in PLANNING.md §16, and this pane reads
    /// a deserialized session every time one is resumed.
    /// </summary>
    private static string? Text(Issue issue, string field) =>
        issue.SuggestedFix is not null &&
        issue.SuggestedFix.TryGetValue(field, out var raw) &&
        raw?.ToString() is { Length: > 0 } value
            ? value
            : null;

    private static double? Number(Issue issue, string field) =>
        double.TryParse(Text(issue, field), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Signed(double value) => value.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);
}
