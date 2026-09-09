using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Builds a starter <see cref="RuleConfig"/> for a model that has none,
/// from what a real capture of that model actually contains.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same discipline as
/// <c>RevitCheck.MappingBuilder.MappingAutoBuilder</c>, whose own summary
/// states it best: <em>removes the typing and the searching, never the
/// judgement</em>. So this widens a candidate list when a real schedule
/// column literally reads "EASTING"/"NORTHING" - that is a search, and
/// getting it wrong costs nothing because the check already tries every
/// candidate - but it never picks a category name, a key parameter or a
/// tolerance, because those are judgements a person has to make against
/// the real model. Everything it could not decide comes back in
/// <see cref="Result.Diagnostics"/> as the real, observed shortlist to
/// decide from, rather than as a blank field and a guess.
/// </para>
/// <para>
/// Added 2026-09-07. The mechanism it restores is the one metadata
/// reconciliation has had since the beginning (capture the model, build a
/// per-model mapping from it, let a human finish it) and which none of the
/// checks built afterwards adopted - which is why the same real
/// correction had to be made separately in each of them.
/// </para>
/// </remarks>
public static class RuleConfigStarter
{
    public sealed class Result
    {
        public required RuleConfig Config { get; init; }
        public required List<string> Diagnostics { get; init; }
    }

    public static Result Build(RevitModel model, RuleConfig? defaults = null)
    {
        var config = defaults ?? new RuleConfig();
        var diagnostics = new List<string>();

        var easting = DiscoverHeaders(model, config.PileScheduleEastingHeaders, "EASTING");
        var northing = DiscoverHeaders(model, config.PileScheduleNorthingHeaders, "NORTHING");

        if (easting.Count > 0 || northing.Count > 0)
        {
            diagnostics.Add(
                "Coordinate-looking schedule heading(s) found in this model that are NOT configured: " +
                string.Join(", ", easting.Concat(northing).Select(h => $"'{h}'")) +
                ". They are listed, not adopted - add any that genuinely carry each element's own setout " +
                "position to pile_schedule_easting_headers / pile_schedule_northing_headers. Not every " +
                "heading containing EASTING is one: on this project DIT_StartEasting/DIT_StartNorthing are " +
                "maintenance metadata giving the bridge's centrepoint, the same value for every element on " +
                "the structure.");
        }

        // Categories are a judgement, not a search: report the real
        // shortlist and leave the configured value alone. A project whose
        // piles are Generic Models (real, 2026-09-07) has to say so - but
        // it can now say so in a file instead of a rebuild.
        var categories = model.Elements
            .Select(e => e.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .GroupBy(c => c!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        if (categories.Count > 0)
        {
            diagnostics.Add(
                $"pile_category_name is '{config.PileCategoryName}' and pile_collection_category_names is " +
                $"[{string.Join(", ", config.PileCollectionCategoryNames)}]. Every model category present in " +
                "this capture, by element count: " + string.Join(", ", categories) +
                ". If this project's piles are not in a listed collection category they will never reach the " +
                "check at all - set both fields to match what is really there.");
        }

        var schedules = model.Schedules
            .Where(s => s.Headers.Count > 0)
            .Select(s => $"'{s.Name}' [{string.Join(" | ", s.Headers)}]")
            .ToList();

        if (schedules.Count > 0)
        {
            diagnostics.Add("Schedules captured: " + string.Join("; ", schedules));
        }

        diagnostics.Add(
            "Tolerances are left at their compiled defaults. Every one of them is a placeholder rather " +
            "than a figure calibrated against a known-bad case - see RuleConfig's own remarks per field.");

        return new Result
        {
            Config = config,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Captured schedule headings containing <paramref name="token"/> that
    /// are not already configured - reported for a person to choose from,
    /// never adopted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Changed 2026-09-09, from a real negative control.</b> This used to
    /// add every match straight into the candidate list, on the reasoning
    /// that "widening a candidate list is safe - the check tries them all
    /// and uses the first that resolves". <b>That reasoning was wrong.</b>
    /// Headings resolve <i>per schedule</i>, so widening does not merely add
    /// a fallback: it promotes a schedule that carries no setout data at all
    /// into a candidate setout schedule, which then states a position that
    /// contradicts the real one.
    /// </para>
    /// <para>
    /// The real case: <c>DIT_StartEasting</c>/<c>DIT_StartNorthing</c>
    /// matched on the substring and were adopted. On this client's projects
    /// they are maintenance metadata carrying the <i>bridge's</i>
    /// centrepoint - one value for the whole structure, not per element -
    /// so every pile appeared to be 4.3m to 20m from where "a schedule"
    /// said it was. <c>PileModelScheduleConsistencyCheck</c> then reported
    /// the schedules as contradicting each other and refused to compare
    /// anything, which masked a deliberately planted 50mm error on pile
    /// 5506399 (PLANNING.md §23). The correct answer was present in
    /// 'ABUTMENT A PILE SCHEDULE' the whole time.
    /// </para>
    /// <para>
    /// So this is a search that <i>reports</i>, and adopting a heading is a
    /// judgement left to a person - the same split the category shortlist
    /// below already draws, and which this method was the sole exception to.
    /// </para>
    /// </remarks>
    private static List<string> DiscoverHeaders(
        RevitModel model, List<string> existing, string token)
    {
        var found = new List<string>();

        foreach (var header in model.Schedules.SelectMany(s => s.Headers).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (header.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (existing.Contains(header, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            found.Add(header);
        }

        return found;
    }
}
