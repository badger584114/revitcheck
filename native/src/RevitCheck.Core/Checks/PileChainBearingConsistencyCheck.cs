using System.Globalization;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

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
/// <para>
/// <b>Corrected 2026-09-07, from a real failure on a second bridge
/// model.</b> A bearing used to be fitted endpoint-to-endpoint across a
/// whole chain. That is wrong in both directions, and the real run found
/// the first of them: two setout lines meeting at a shared pile form one
/// topologically simple chain, and the fitted bearing ran across the
/// corner, matching neither leg - a correct drawing flagged as wrong.
/// The second, never observed only because it fails silently: a line
/// through two points fits with zero residual, so an interior pile
/// sitting off its line was undetectable and such a chain was reported
/// clean. Every chain now goes through
/// <see cref="PileChainReconstruction.SplitIntoStraightRuns"/> first,
/// each straight run is checked against its own nearest bearing call, and
/// the corner itself is reported for a human
/// (<see cref="InvestigationReconciliation.ManualReviewCategory"/>) since
/// only a person can say whether two setout lines legitimately meet there
/// or a pile is out of place. Both shapes are covered by regression tests
/// built from this project's own real bearing figures, confirmed to fail
/// without the fix.
/// </para>
/// </remarks>
public static class PileChainBearingConsistencyCheck
{
    public const string RuleId = "revitcheck.pile_chain_bearing_consistency";

    public static List<Issue> Run(RevitModel model, RuleConfig config) => RunWithScope(model, config).Issues;

    /// <summary>
    /// Same as <see cref="Run"/>, but also returns every dimension
    /// ElementId this check actually reached a bearing verdict for - a
    /// chain long enough to evaluate (<see cref="RuleConfig.PileChainMinimumPiles"/>),
    /// whether it turned out clean or flagged. A chain too short to
    /// evaluate, or an ambiguous/branching component, is deliberately NOT
    /// included - this check never reached a real verdict on those
    /// dimensions, so marking them "investigated" would be a false claim
    /// of coverage, not an honest one.
    /// </summary>
    /// <remarks>
    /// Added for the interactive checking session (PLANNING.md §16 Stage
    /// 3): <c>InvestigationReconciliation.Reconcile</c> needs an
    /// investigation check's examined-dimension scope kept separate from
    /// its issues (see that class's own remarks on why "not in the issue
    /// list" can never mean "confirmed clean" on its own) - before this,
    /// nothing in this check exposed that scope at all, only its findings.
    /// <see cref="Run"/> itself is unchanged and stays the right entry
    /// point for the standalone ribbon button, which doesn't need the
    /// scope.
    /// </remarks>
    public static (List<Issue> Issues, List<long> InvestigatedDimensionElementIds) RunWithScope(RevitModel model, RuleConfig config)
    {
        var issues = new List<Issue>();
        var investigated = new List<long>();

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
            return (issues, investigated);
        }

        var notes = model.TextNotes
            .Select(n => (Note: n, Degrees: BearingText.TryParseDegrees(n.RawText)))
            .Where(t => t.Degrees is not null && t.Note.LocalPoint is not null)
            .ToList();

        var edges = PileChainReconstruction.BuildEdges(model, piles, config);
        var chainSet = PileChainReconstruction.BuildChains(edges);
        var edgeDimensionIds = new HashSet<long>(edges.Select(e => e.DimensionElementId));

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

            EvaluateChain(chain, notes, config, issues, investigated);
        }

        // Real dimensions that plausibly belong to a pile chain but didn't
        // confidently resolve into one - previously silently dropped, see
        // PileChainReconstruction.IsNearMissPileMatch's own remarks for the
        // real case this fixes. Reported per-dimension (ElementId is the
        // dimension itself, not a pile - no ExpandByElementIdList needed by
        // a caller), and counted as investigated - a human judgement is
        // still an examination, the same "clean/problem/manual review all
        // count as examined" rule InvestigationReconciliation already
        // applies everywhere else.
        foreach (var dim in model.Dimensions)
        {
            if (edgeDimensionIds.Contains(dim.ElementId))
            {
                continue;
            }

            if (!PileChainReconstruction.IsNearMissPileMatch(dim, piles, config))
            {
                continue;
            }

            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = InvestigationReconciliation.ManualReviewCategory,
                Severity = "medium",
                ElementId = dim.ElementId,
                ViewId = dim.ViewId,
                UniqueId = dim.UniqueId,
                Description =
                    "One reference on this dimension matches a real pile and the other doesn't confidently " +
                    "match any pile (or both references match the same pile) - it may be dimensioned to a " +
                    "setout-point marker or similar rather than a genuine pile-to-pile distance. Needs a human " +
                    "to check against the drawing with this view open, not an automated verdict.",
            });
            investigated.Add(dim.ElementId);
        }

        return (issues, investigated.Distinct().ToList());
    }

    /// <summary>
    /// Splits the chain into geometrically straight runs first, then checks
    /// each run's own bearing against its own nearest bearing call. See
    /// <see cref="PileChainReconstruction.SplitIntoStraightRuns"/> for why
    /// a chain cannot be assumed straight, and why an endpoint-to-endpoint
    /// bearing over the whole chain was wrong in both directions.
    /// </summary>
    private static void EvaluateChain(
        PileChain chain,
        List<(TextNoteInfo Note, double? Degrees)> notes,
        RuleConfig config,
        List<Issue> issues,
        List<long> investigated)
    {
        var split = PileChainReconstruction.SplitIntoStraightRuns(chain, config);

        if (split.PositionsIncomplete)
        {
            issues.Add(ChainCoverageIssue(chain,
                $"Reconstructed a {ChainDescription(chain)} but at least one pile has no live position " +
                "captured - the chain could not be checked for straightness, so no bearing was checked."));
            return;
        }

        foreach (var bend in split.Bends)
        {
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = InvestigationReconciliation.ManualReviewCategory,
                Severity = "medium",
                ElementId = bend.Pile.ElementId,
                UniqueId = bend.Pile.UniqueId,
                Description =
                    $"A {ChainDescription(chain)} changes direction at pile {bend.Pile.ElementId}: " +
                    $"{FormatDms(bend.BearingBeforeDegrees)} into it, {FormatDms(bend.BearingAfterDegrees)} out of it " +
                    $"({FormatDegrees(bend.DeviationDegrees)} apart). Tag-to-tag dimensioning connected these piles " +
                    "into one run, but they are not one straight line - either two different setout lines meet here " +
                    "(each is checked separately below) or this pile sits off its line. Needs a human to say which, " +
                    "not an automated verdict.",
                SuggestedFix = new Dictionary<string, object?>
                {
                    ["bend_pile_element_id"] = bend.Pile.ElementId,
                    ["bearing_before_degrees"] = bend.BearingBeforeDegrees,
                    ["bearing_after_degrees"] = bend.BearingAfterDegrees,
                    ["deviation_degrees"] = bend.DeviationDegrees,
                    ["dimension_element_ids"] = bend.DimensionElementIds,
                },
            });

            investigated.AddRange(bend.DimensionElementIds);
        }

        foreach (var run in split.Runs)
        {
            if (run.PilesInOrder.Count < config.PileChainMinimumPiles)
            {
                continue;
            }

            investigated.AddRange(run.DimensionElementIds);
            EvaluateRun(run, split.Bends.Count > 0, notes, config, issues);
        }
    }

    /// <summary>
    /// Checks one verified-straight run against its nearest bearing call.
    /// The endpoint-to-endpoint azimuth is meaningful here and only here -
    /// every interior edge has already been confirmed to point the same way
    /// within <see cref="RuleConfig.PileChainCollinearityToleranceDegrees"/>.
    /// </summary>
    private static void EvaluateRun(
        PileChainRun run,
        bool fromSplitChain,
        List<(TextNoteInfo Note, double? Degrees)> notes,
        RuleConfig config,
        List<Issue> issues)
    {
        var first = run.PilesInOrder[0];
        var last = run.PilesInOrder[run.PilesInOrder.Count - 1];
        var runLabel = fromSplitChain ? "straight run" : "chain";
        var description = $"{runLabel} of {run.PilesInOrder.Count} piles ({first.ElementId} → {last.ElementId})";

        // Positions are known to be present - SplitIntoStraightRuns returns
        // PositionsIncomplete rather than a run when any pile lacks one.
        var bearing = BearingMath.AzimuthDegrees(
            first.ProjectPositionEastingMm!.Value, first.ProjectPositionNorthingMm!.Value,
            last.ProjectPositionEastingMm!.Value, last.ProjectPositionNorthingMm!.Value);
        var reciprocal = BearingMath.Reciprocal(bearing);

        var matched = NearestNote(run.PilesInOrder, notes, config.PileChainNoteMaxDistanceMm);
        if (matched is not { } matchedValue)
        {
            issues.Add(RunCoverageIssue(run,
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
                ["max_internal_deviation_degrees"] = run.MaxInternalDeviationDegrees,
                ["pile_element_ids"] = run.PilesInOrder.Select(p => p.ElementId).ToList(),
                ["dimension_element_ids"] = run.DimensionElementIds,
            },
        });
    }

    private static string ChainDescription(PileChain chain) =>
        $"chain of {chain.PilesInOrder.Count} piles " +
        $"({chain.PilesInOrder[0].ElementId} → {chain.PilesInOrder[chain.PilesInOrder.Count - 1].ElementId})";

    /// <summary>The parsed note nearest to any pile in the run (2D), within the configured cap - not just its endpoints, since a bearing call can sit anywhere along a run's length.</summary>
    private static (TextNoteInfo Note, double? Degrees)? NearestNote(
        IReadOnlyList<ElementMetadata> pilesInOrder, List<(TextNoteInfo Note, double? Degrees)> notes, double maxDistanceMm)
    {
        (TextNoteInfo Note, double? Degrees)? best = null;
        var bestDistance = double.MaxValue;

        foreach (var candidate in notes)
        {
            var notePoint = candidate.Note.LocalPoint!;
            foreach (var pile in pilesInOrder)
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

    private static Issue RunCoverageIssue(PileChainRun run, string description) => new()
    {
        RuleId = RuleId,
        Category = "coverage",
        Severity = "medium",
        ElementId = run.PilesInOrder[0].ElementId,
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
