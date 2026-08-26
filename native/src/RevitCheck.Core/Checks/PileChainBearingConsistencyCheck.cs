using System.Globalization;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Reconstructs each real pile chain's own bearing from live model
/// geometry (<see cref="PileChainReconstruction"/>) and compares it
/// against the drafted bearing call nearest to it. This is the third pile
/// check named in PLANNING.md §14, and a simpler, stronger mechanism than
/// the originally-planned drawing-vs-schedule §5b DXF-chain-walk port: no
/// bearing/DMS parsing is needed as an <em>input</em> to the
/// reconstruction (only as the comparison target), no dimension-chain
/// traversal, no witness-point matching - the chain is built directly
/// from real pile positions already matched via tag-to-pile proximity.
/// </summary>
/// <remarks>
/// <para>
/// Calibrated end-to-end against real data, 2026-08-26: 4 real chains on
/// <c>DRG-2873041 - PILE LAYOUT</c> reconstructed from live model
/// geometry (excluding the 4 references confirmed by the user to be
/// setout-point markers, not piles), every one matching a real printed
/// bearing call to within a third of an arcsecond:
/// </para>
/// <list type="bullet">
/// <item>14 piles (PIL232101→114): reconstructed 165°13'25.93", printed "165°13'26"".</item>
/// <item>14 piles (PIL232139→126): reconstructed 165°07'01.24" (reciprocal), printed "165°07'01"".</item>
/// <item>5 piles (PIL232125→121): reconstructed 165°07'01.24" (reciprocal), printed "165°07'01"".</item>
/// <item>2 piles (PIL232116→115): reconstructed 165°13'07.67" (reciprocal), printed "165°13'08"".</item>
/// </list>
/// <para>
/// A 5th real printed bearing ("161°22'41"") correctly matched none of
/// these four - it belongs to a chain this particular view's 46
/// dimensions don't cover, and is left unmatched (a coverage finding, see
/// <see cref="RuleConfig.PileChainNoteMaxDistanceMm"/>'s own remarks)
/// rather than force-matched to whichever chain happens to be nearest.
/// </para>
/// </remarks>
public static class PileChainBearingConsistencyCheck
{
    public const string RuleId = "revitcheck.pile_chain_bearing_consistency";

    public static List<Issue> Run(RevitModel model, RuleConfig config)
    {
        var issues = new List<Issue>();

        var piles = model.Elements
            .Where(e => string.Equals(e.Category, config.PileCategoryName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (piles.Count == 0)
        {
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "low",
                Description =
                    $"No captured elements have category '{config.PileCategoryName}' - no pile chains could be reconstructed.",
            });
            return issues;
        }

        var notes = model.TextNotes
            .Select(n => (Note: n, Degrees: BearingText.TryParseDegrees(n.RawText)))
            .Where(t => t.Degrees is not null && t.Note.LocalPoint is not null)
            .ToList();

        var edges = PileChainReconstruction.BuildEdges(model, piles, config);
        var chainSet = PileChainReconstruction.BuildChains(edges);

        foreach (var component in chainSet.AmbiguousComponents)
        {
            var ids = string.Join(", ", component.Select(p => p.ElementId));
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "coverage",
                Severity = "medium",
                Description =
                    $"{component.Count} piles form a branched or cyclic tag-to-tag dimension network, not a " +
                    "simple chain, so no bearing could be reconstructed - this project's setout convention is " +
                    $"confirmed always linear (PLANNING.md §5b): {ids}.",
            });
        }

        foreach (var chain in chainSet.Chains)
        {
            if (chain.PilesInOrder.Count < config.PileChainMinimumPiles)
            {
                continue;
            }

            EvaluateChain(chain, notes, config, issues);
        }

        return issues;
    }

    private static void EvaluateChain(
        PileChain chain,
        List<(TextNoteInfo Note, double? Degrees)> notes,
        RuleConfig config,
        List<Issue> issues)
    {
        var first = chain.PilesInOrder[0];
        var last = chain.PilesInOrder[chain.PilesInOrder.Count - 1];
        var description = $"chain of {chain.PilesInOrder.Count} piles ({first.ElementId} → {last.ElementId})";

        if (first.ProjectPositionEastingMm is not { } eastingFrom ||
            first.ProjectPositionNorthingMm is not { } northingFrom ||
            last.ProjectPositionEastingMm is not { } eastingTo ||
            last.ProjectPositionNorthingMm is not { } northingTo)
        {
            issues.Add(ChainCoverageIssue(chain,
                $"Reconstructed a {description} but at least one endpoint has no live position captured - " +
                "its bearing could not be checked."));
            return;
        }

        var bearing = BearingMath.AzimuthDegrees(eastingFrom, northingFrom, eastingTo, northingTo);
        var reciprocal = BearingMath.Reciprocal(bearing);

        var matched = NearestNote(chain, notes, config.PileChainNoteMaxDistanceMm);
        if (matched is not { } matchedValue)
        {
            issues.Add(ChainCoverageIssue(chain,
                $"Reconstructed a {description}, real bearing {FormatDms(bearing)} - no bearing call was found " +
                $"within {FormatMm(config.PileChainNoteMaxDistanceMm)}mm to check it against."));
            return;
        }

        var (note, noteDegrees) = matchedValue;
        var delta = Math.Min(
            BearingMath.AngularDifference(bearing, noteDegrees!.Value),
            BearingMath.AngularDifference(reciprocal, noteDegrees.Value));

        if (delta <= config.PileChainBearingToleranceDegrees)
        {
            return;
        }

        issues.Add(new Issue
        {
            RuleId = RuleId,
            Category = "geometry",
            Severity = "high",
            ElementId = first.ElementId,
            UniqueId = first.UniqueId,
            Description =
                $"Reconstructed a {description}: real bearing {FormatDms(bearing)}, but the nearest bearing " +
                $"call ('{note.RawText.Trim()}', element {note.ElementId}) reads {FormatDms(noteDegrees.Value)} - " +
                $"{FormatDegrees(delta)} apart, beyond the {FormatDegrees(config.PileChainBearingToleranceDegrees)} tolerance.",
            SuggestedFix = new Dictionary<string, object?>
            {
                ["reconstructed_bearing_degrees"] = bearing,
                ["reciprocal_bearing_degrees"] = reciprocal,
                ["note_bearing_degrees"] = noteDegrees.Value,
                ["note_text"] = note.RawText.Trim(),
                ["note_element_id"] = note.ElementId,
                ["delta_degrees"] = delta,
                ["pile_element_ids"] = chain.PilesInOrder.Select(p => p.ElementId).ToList(),
                ["dimension_element_ids"] = chain.DimensionElementIds,
            },
        });
    }

    /// <summary>The parsed note nearest to any pile in the chain (2D), within the configured cap - not just the chain's endpoints, since a bearing call can sit anywhere along a chain's length.</summary>
    private static (TextNoteInfo Note, double? Degrees)? NearestNote(
        PileChain chain, List<(TextNoteInfo Note, double? Degrees)> notes, double maxDistanceMm)
    {
        (TextNoteInfo Note, double? Degrees)? best = null;
        var bestDistance = double.MaxValue;

        foreach (var candidate in notes)
        {
            var notePoint = candidate.Note.LocalPoint!;
            foreach (var pile in chain.PilesInOrder)
            {
                if (pile.LocalPoint is not { } pilePoint)
                {
                    continue;
                }

                var dx = notePoint.X - pilePoint.X;
                var dy = notePoint.Y - pilePoint.Y;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
        }

        return best is not null && bestDistance <= maxDistanceMm ? best : null;
    }

    private static Issue ChainCoverageIssue(PileChain chain, string description) => new()
    {
        RuleId = RuleId,
        Category = "coverage",
        Severity = "medium",
        ElementId = chain.PilesInOrder[0].ElementId,
        Description = description,
    };

    private static string FormatMm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatDegrees(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture) + "°";

    private static string FormatDms(double degrees)
    {
        var totalSeconds = Math.Abs(degrees) * 3600.0;
        var sign = degrees < 0 ? "-" : "";
        var d = (int)(totalSeconds / 3600.0);
        var m = (int)((totalSeconds % 3600.0) / 60.0);
        var s = totalSeconds % 60.0;
        return $"{sign}{d}° {m:00}' {s.ToString("00.##", CultureInfo.InvariantCulture)}\"";
    }
}
