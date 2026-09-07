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

        if (easting.Added.Count > 0 || northing.Added.Count > 0)
        {
            diagnostics.Add(
                "Added setout column heading(s) found in this model: " +
                string.Join(", ", easting.Added.Concat(northing.Added).Select(h => $"'{h}'")) +
                " - confirm these are the columns carrying real coordinates.");
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
                $"pile_category_name is '{config.PileCategoryName}'. Categories actually present in this " +
                "capture, by element count: " + string.Join(", ", categories));
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
            Config = config with
            {
                PileScheduleEastingHeaders = easting.Candidates,
                PileScheduleNorthingHeaders = northing.Candidates,
            },
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Every captured schedule heading containing <paramref name="token"/>
    /// that isn't already a candidate. Widening a candidate list is safe -
    /// the check tries them all and uses the first that resolves - which is
    /// what makes this a search rather than a judgement.
    /// </summary>
    private static (List<string> Candidates, List<string> Added) DiscoverHeaders(
        RevitModel model, List<string> existing, string token)
    {
        var candidates = new List<string>(existing);
        var added = new List<string>();

        foreach (var header in model.Schedules.SelectMany(s => s.Headers).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (header.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (candidates.Contains(header, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.Add(header);
            added.Add(header);
        }

        return (candidates, added);
    }
}
