using System;
using System.Collections.Generic;
using System.Linq;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Checks each pile-to-pile dimension's own stated distance against the
/// model, and against every other dimension of the same pile pair.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added 2026-09-10, and it closes a real hole.</b>
/// <see cref="PileChainBearingConsistencyCheck"/> marks every dimension in
/// an evaluated run as investigated - which reconciles the triage flag on
/// it - having used those dimensions only as chain <i>topology</i>. It
/// never reads a single one's value. So triage says "this 3755mm dimension
/// is drafted, verify it against the model", the run is checked for
/// bearing, and the dimension's own stated number is reconciled away
/// without anything ever having compared it to anything.
/// </para>
/// <para>
/// <b>This needs none of the geometry that blocked the general case.</b>
/// PLANNING.md §14 established over seven real runs that an ordinary
/// linear dimension's witness points cannot be recovered from the API. A
/// pile chain dimension does not need them: the tag-to-tag join already
/// resolves it to exactly two real piles, and the model already knows
/// where those two piles are. Stated distance against real distance, with
/// nothing inferred.
/// </para>
/// <para>
/// It is also the direct Revit-era equivalent of the archived pipeline's
/// §5b work (<c>geometry.setout_reconstruction</c> /
/// <c>geometry.ifc_setout_consistency</c>), which walked a drawn dimension
/// chain and cross-checked the result against the model - the half the
/// bearing check never replaced, because it reconstructs from the model
/// and compares against a printed <i>bearing</i>, not against the printed
/// <i>distances</i>.
/// </para>
/// <para>
/// <b>Two comparisons, deliberately in one check</b>, because they share
/// every bit of resolution work and disagree in informative ways: a
/// dimension wrong against the model but agreeing with its twin on another
/// sheet is a model/drawing drift; two dimensions of the same pair
/// disagreeing with each other is a drafting error whichever one the model
/// backs.
/// </para>
/// </remarks>
public static class PileDimensionConsistencyCheck
{
    public const string RuleId = "revitcheck.pile_dimension_consistency";

    private const int MaxListed = 5;

    public static List<Issue> Run(RevitModel model, RuleConfig config) =>
        RunWithScope(model, config).Issues;

    public static (List<Issue> Issues, List<long> InvestigatedElementIds) RunWithScope(
        RevitModel model, RuleConfig config)
    {
        var issues = new List<Issue>();
        var investigated = new List<long>();

        var piles = model.Elements
            .Where(e => string.Equals(e.Category, config.PileCategoryName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (piles.Count == 0)
        {
            issues.Add(Coverage(
                $"No captured elements have category '{config.PileCategoryName}' - no pile-to-pile dimension " +
                "could be resolved, so no stated distance was checked against anything."));
            return (issues, investigated);
        }

        var byId = model.Dimensions.ToDictionary(d => d.ElementId, d => d);
        var measured = new List<Measured>();
        var unreadable = new List<long>();

        foreach (var edge in PileChainReconstruction.BuildEdges(model, piles, config))
        {
            if (!byId.TryGetValue(edge.DimensionElementId, out var dimension))
            {
                continue;
            }

            var stated = StatedMm(dimension);
            var actual = DistanceMm(edge.PileA, edge.PileB);

            if (stated is null || actual is null)
            {
                // Skip rather than guess: an override that is not a number
                // ("500 MIN.", a bar mark), or a pile with no live position.
                unreadable.Add(dimension.ElementId);
                continue;
            }

            measured.Add(new Measured(dimension, edge.PileA, edge.PileB, stated.Value, actual.Value));
        }

        if (measured.Count == 0)
        {
            issues.Add(Coverage(
                "No pile-to-pile dimension resolved to two piles with a readable stated distance and a live " +
                "position, so nothing was compared." + Skipped(unreadable)));
            return (issues, investigated);
        }

        foreach (var item in measured)
        {
            investigated.Add(item.Dimension.ElementId);
            var deltaMm = Math.Abs(item.StatedMm - item.ActualMm);
            if (deltaMm <= config.PileDimensionToleranceMm)
            {
                continue;
            }

            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "geometry",
                Severity = "high",
                ElementId = item.Dimension.ElementId,
                UniqueId = item.Dimension.UniqueId,
                ViewId = item.Dimension.ViewId,
                Description =
                    $"Dimension {item.Dimension.ElementId} states {Mm(item.StatedMm)}mm between piles " +
                    $"{item.PileA.ElementId} and {item.PileB.ElementId}, but the model has them " +
                    $"{Mm(item.ActualMm)}mm apart - {Mm(deltaMm)}mm out, beyond the " +
                    $"{Mm(config.PileDimensionToleranceMm)}mm tolerance. Either the piles moved after this was " +
                    "dimensioned, or the dimension states something the model does not.",
                SuggestedFix = new Dictionary<string, object?>
                {
                    ["stated_mm"] = item.StatedMm,
                    ["model_mm"] = item.ActualMm,
                    ["delta_mm"] = deltaMm,
                    ["pile_element_ids"] = new List<long> { item.PileA.ElementId, item.PileB.ElementId },
                },
            });
        }

        issues.AddRange(DisagreeingPairs(measured, config));

        if (unreadable.Count > 0)
        {
            issues.Add(Coverage(
                $"{unreadable.Count} pile-to-pile dimension(s) were not compared." + Skipped(unreadable)));
        }

        return (issues, investigated.Distinct().ToList());
    }

    /// <summary>
    /// Dimension against dimension: the same pair of piles measured in more
    /// than one place, stating different distances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the cross-view half of CLAUDE.md's own brief ("dimensional
    /// consistency within and across views") and it needs no model at all -
    /// two drawings simply cannot both be right. It catches what the
    /// model comparison above cannot: a dimension copied to a second sheet
    /// and then edited on only one of them, where each is individually
    /// plausible.
    /// </para>
    /// <para>
    /// Reported once per disagreeing pair-of-piles rather than once per
    /// dimension, and it names both sides - the finding is the
    /// contradiction, not either dimension on its own, and nothing here can
    /// say which one is wrong.
    /// </para>
    /// </remarks>
    private static List<Issue> DisagreeingPairs(List<Measured> measured, RuleConfig config)
    {
        var issues = new List<Issue>();

        foreach (var group in measured.GroupBy(m => OrderedKey(m.PileA.ElementId, m.PileB.ElementId)))
        {
            var distinct = group.ToList();
            if (distinct.Count < 2)
            {
                continue;
            }

            var spread = distinct.Max(m => m.StatedMm) - distinct.Min(m => m.StatedMm);
            if (spread <= config.PileDimensionToleranceMm)
            {
                // The same answer given twice - which is worth nothing on
                // its own, and is not a finding.
                continue;
            }

            var stated = string.Join(", ", distinct.Select(m => $"{m.Dimension.ElementId} states {Mm(m.StatedMm)}mm"));
            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "geometry",
                Severity = "high",
                ElementId = distinct[0].Dimension.ElementId,
                UniqueId = distinct[0].Dimension.UniqueId,
                ViewId = distinct[0].Dimension.ViewId,
                Description =
                    $"Piles {group.Key.Item1} and {group.Key.Item2} are dimensioned {distinct.Count} times and " +
                    $"the drawings disagree by {Mm(spread)}mm ({stated}). They cannot all be right, and this " +
                    "says nothing about which is - the model has them " +
                    $"{Mm(distinct[0].ActualMm)}mm apart.",
                SuggestedFix = new Dictionary<string, object?>
                {
                    ["spread_mm"] = spread,
                    ["model_mm"] = distinct[0].ActualMm,
                    ["dimension_element_ids"] = distinct.Select(m => m.Dimension.ElementId).ToList(),
                    ["stated_mm"] = distinct.Select(m => m.StatedMm).ToList(),
                },
            });
        }

        return issues;
    }

    /// <summary>
    /// What the drawing says: the typed override where there is one, since
    /// that is what a reader of the sheet acts on, otherwise the measured
    /// value. Null when an override is present but not a number - a bar
    /// mark or "500 MIN." states something this comparison cannot use, and
    /// guessing at it is exactly what this project's "skip rather than
    /// guess" rule forbids.
    /// </summary>
    private static double? StatedMm(DimensionInfo dimension)
    {
        var segment = dimension.Segments.Count == 1 ? dimension.Segments[0] : null;
        if (segment is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(segment.ValueOverride))
        {
            return DimensionOverrideConsistencyCheck.ParseOverrideMm(segment.ValueOverride);
        }

        return segment.ValueMm;
    }

    /// <summary>
    /// Real 2D distance between two piles. Survey coordinates when both
    /// have them, otherwise both piles' local points - never a mix, and
    /// never derived: a distance is invariant under the survey transform,
    /// so either frame answers correctly as long as it is one frame.
    /// </summary>
    private static double? DistanceMm(ElementMetadata a, ElementMetadata b)
    {
        if (a.ProjectPositionEastingMm is { } ae && a.ProjectPositionNorthingMm is { } an &&
            b.ProjectPositionEastingMm is { } be && b.ProjectPositionNorthingMm is { } bn)
        {
            return Hypot(ae - be, an - bn);
        }

        if (a.LocalPoint is { } ap && b.LocalPoint is { } bp)
        {
            return Hypot(ap.X - bp.X, ap.Y - bp.Y);
        }

        return null;
    }

    /// <summary>
    /// Names what was skipped, on both the "some were compared" and the
    /// "none were" paths - a count alone leaves a real extraction or
    /// convention problem undiagnosable, and these are the dimensions a
    /// reviewer still has to settle by hand.
    /// </summary>
    private static string Skipped(List<long> unreadable) =>
        unreadable.Count == 0
            ? string.Empty
            : " Not compared (no readable stated distance, or no live pile position): " +
              string.Join(", ", unreadable.Take(MaxListed)) +
              (unreadable.Count > MaxListed ? ", ..." : "") + ".";

    private static double Hypot(double dx, double dy) => Math.Sqrt((dx * dx) + (dy * dy));

    private static (long, long) OrderedKey(long a, long b) => a <= b ? (a, b) : (b, a);

    private static string Mm(double value) =>
        value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static Issue Coverage(string description) => new()
    {
        RuleId = RuleId,
        Category = InvestigationReconciliation.CoverageCategory,
        Severity = "low",
        Description = description,
    };

    private sealed class Measured
    {
        public Measured(
            DimensionInfo dimension, ElementMetadata pileA, ElementMetadata pileB, double statedMm, double actualMm)
        {
            Dimension = dimension;
            PileA = pileA;
            PileB = pileB;
            StatedMm = statedMm;
            ActualMm = actualMm;
        }

        public DimensionInfo Dimension { get; }

        public ElementMetadata PileA { get; }

        public ElementMetadata PileB { get; }

        /// <summary>What the drawing says.</summary>
        public double StatedMm { get; }

        /// <summary>What the model says.</summary>
        public double ActualMm { get; }
    }
}
