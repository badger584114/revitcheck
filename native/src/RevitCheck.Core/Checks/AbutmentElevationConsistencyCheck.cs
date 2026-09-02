using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Compares a Spot Elevation's own drafted value against real solid
/// geometry found near it - the second element-type check in the
/// per-element-type pattern the two pile checks established (PLANNING.md
/// §16/§18), and the first to verify against raw geometry rather than a
/// schedule or a live parameter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <see cref="DimensionSegmentInfo.ValueMm"/>/a parameter, the
/// way the ported dimension checks and the pile checks both do:</b> real
/// data confirmed a Spot Elevation's own <c>Value</c>/<c>ValueOverride</c>
/// are unconditionally null for this family (347/347 on the committed
/// real capture, PLANNING.md §17's investigation) - the drafted value
/// instead lives in <see cref="DimensionInfo.Origin"/>'s own Z. And real
/// data separately confirmed neither the spot's own <see cref="ReferenceInfo"/>
/// nor a plausible-looking parameter on nearby model geometry reliably
/// represents the real shelf a girder sits on: reference resolution is
/// mixed (a real model element once, a view-specific annotation twice on
/// the same real sample), and even where it did resolve, an available
/// parameter (a profile's own "Start/End Level Offset") turned out to
/// describe a different real feature (the crest) than the shelf itself -
/// confirmed directly by the user. Real solid geometry, read directly via
/// <c>Face.Project</c> (<see cref="NearbyFaceInfo"/>, populated by the
/// adapter), is the one thing that can't misrepresent where a horizontal
/// surface actually is.
/// </para>
/// <para>
/// <b>Deliberately not scoped by category anywhere in this check</b> - the
/// user's own correction, 2026-09-02: a category-filtered search "will
/// fall over as soon as we put another model into it" (Structural Framing,
/// this project's own current convention, is "an old workflow... being
/// phased out"). <see cref="RuleConfig"/> has no
/// <c>AbutmentCategoryName</c>-style field for that reason - the category
/// piles use (<see cref="RuleConfig.PileCategoryName"/>) is stable enough
/// to be worth naming; this one, confirmed not even stable within one
/// client's own project history, is not.
/// </para>
/// <para>
/// <b>The nearest real face by 2D (plan) distance is always the one
/// judged against</b> - never the nearest by Z agreement. Picking whichever
/// candidate happens to agree would be circular: the entire point is
/// whether the drafted value matches what is physically underneath it,
/// not whether some face somewhere nearby happens to match.
/// </para>
/// </remarks>
public static class AbutmentElevationConsistencyCheck
{
    public const string RuleId = "revitcheck.abutment_elevation_consistency";

    public static List<Issue> Run(RevitModel model, RuleConfig config)
    {
        var issues = new List<Issue>();
        var spotDimensions = model.Dimensions.Where(d => d.IsSpot).ToList();

        if (spotDimensions.Count == 0)
        {
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "low",
                Description = "No Spot Elevations were found in the captured scope, so this rule reported nothing because there was nothing to check.",
            });
            return issues;
        }

        var searched = spotDimensions.Where(d => d.ShelfSearchPerformed).ToList();
        var notSearched = spotDimensions.Count - searched.Count;

        var confirmed = 0;
        var mismatched = 0;
        var noCandidate = 0;
        var noValue = 0;

        foreach (var dim in searched)
        {
            var view = model.ViewById(dim.ViewId);
            var uniqueId = view?.SheetUniqueId ?? dim.UniqueId;

            if (dim.Origin is not { } origin)
            {
                noValue++;
                issues.Add(new Issue
                {
                    RuleId = RuleId,
                    Category = "coverage",
                    Severity = "low",
                    ElementId = dim.ElementId,
                    ViewId = dim.ViewId,
                    ViewName = view?.Name,
                    SheetNo = view?.SheetNo,
                    UniqueId = uniqueId,
                    Description = $"Spot Elevation in {DimensionDescriptions.DescribeView(view)} has no Origin captured - its own drafted value could not be read, so it was not checked.",
                });
                continue;
            }

            if (dim.NearbyHorizontalFaces.Count == 0)
            {
                noCandidate++;
                issues.Add(new Issue
                {
                    RuleId = RuleId,
                    Category = "geometry",
                    Severity = "medium",
                    ElementId = dim.ElementId,
                    ViewId = dim.ViewId,
                    ViewName = view?.Name,
                    SheetNo = view?.SheetNo,
                    UniqueId = uniqueId,
                    Description = $"Spot Elevation in {DimensionDescriptions.DescribeView(view)} is {FormatMm(origin.Z)}mm, but no real geometry was found nearby to verify it against - not checked, not assumed correct.",
                });
                continue;
            }

            // Nearest by plan (2D) distance - see class remarks for why
            // this is never chosen by Z agreement instead.
            var nearest = dim.NearbyHorizontalFaces
                .OrderBy(f => f.Distance2DMm ?? double.MaxValue)
                .First();

            var deltaMm = origin.Z - nearest.ZMm;
            if (Math.Abs(deltaMm) <= config.AbutmentElevationToleranceMm)
            {
                confirmed++;
                continue;
            }

            mismatched++;
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "geometry",
                Severity = "high",
                ElementId = dim.ElementId,
                ViewId = dim.ViewId,
                ViewName = view?.Name,
                SheetNo = view?.SheetNo,
                UniqueId = uniqueId,
                Description =
                    $"Spot Elevation in {DimensionDescriptions.DescribeView(view)} is {FormatMm(origin.Z)}mm, but the " +
                    $"nearest real geometry ({FormatMm(nearest.Distance2DMm ?? 0)}mm away in plan) is {FormatMm(nearest.ZMm)}mm " +
                    $"({FormatSigned(deltaMm)}mm) - beyond the {FormatMm(config.AbutmentElevationToleranceMm)}mm tolerance.",
                SuggestedFix = new Dictionary<string, object?>
                {
                    ["drafted_mm"] = Math.Round(origin.Z, 3),
                    ["nearest_face_mm"] = Math.Round(nearest.ZMm, 3),
                    ["delta_mm"] = Math.Round(deltaMm, 3),
                    ["distance_2d_mm"] = nearest.Distance2DMm is { } d ? Math.Round(d, 3) : null,
                    ["source_element_id"] = nearest.SourceElementId,
                    ["tolerance_mm"] = config.AbutmentElevationToleranceMm,
                },
            });
        }

        issues.Add(CoverageIssue(spotDimensions.Count, notSearched, confirmed, mismatched, noCandidate, noValue));
        return issues;
    }

    private static Issue CoverageIssue(int total, int notSearched, int confirmed, int mismatched, int noCandidate, int noValue)
    {
        var detail =
            $"{total} Spot Elevation(s) found; {total - notSearched} had a geometry search performed " +
            $"({confirmed} confirmed, {mismatched} mismatched, {noCandidate} with no nearby geometry, {noValue} with no drafted value to check).";
        if (notSearched > 0)
        {
            detail += $" {notSearched} were not searched at all (outside the capture's scope for this check) - " +
                "not the same as being confirmed clean.";
        }

        return new Issue
        {
            RuleId = RuleId,
            Category = "coverage",
            Severity = "low",
            Description = detail,
            SuggestedFix = new Dictionary<string, object?>
            {
                ["total_spot_elevations"] = total,
                ["not_searched"] = notSearched,
                ["confirmed"] = confirmed,
                ["mismatched"] = mismatched,
                ["no_candidate"] = noCandidate,
                ["no_value"] = noValue,
            },
        };
    }

    private static string FormatMm(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatSigned(double value) => (value >= 0 ? "+" : "") + FormatMm(value);
}
