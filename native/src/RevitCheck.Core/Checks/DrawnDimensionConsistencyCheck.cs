using System;
using System.Collections.Generic;
using System.Linq;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Checks what a drafted dimension states against what the model actually
/// shows in that view - the general case triage exists to raise.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built 2026-09-11 (PLANNING.md §28), after five probe runs.</b> Triage
/// flags a dimension measuring detail linework and says the file cannot
/// show whether it has drifted. This is the check that answers it, and it
/// works by not asking the question that has no answer.
/// </para>
/// <para>
/// <b>It does not try to recover the dimension's witness points.</b>
/// PLANNING.md §14 established over seven real runs that those are not
/// obtainable - <c>Reference.GlobalPoint</c> null, <c>Location</c>
/// returning the origin for real model geometry, <c>Dimension.Curve</c>
/// throwing, and <c>DimensionSegment.Origin</c> being where the value text
/// sits, 527m from its witness lines on a real case. Instead it resolves
/// the <i>referenced element</i> and reads its own geometry, which is the
/// move that already works for a tag and a pile: a detail component's
/// <c>Location.Point</c>, a detail line's <c>Location.Curve</c>.
/// </para>
/// <para>
/// <b>Then it measures in the view plane</b>, which is the whole thing. A
/// drawing dimensions what it sees, so comparing 3D distances is wrong -
/// guaranteed wrong for an elevation, whose plane sits outside the
/// geometry so every point is at some depth behind it. On real data the
/// same points compared in 3D average 21.09mm of error and in the view
/// plane 1.44mm.
/// </para>
/// <para>
/// <b>What it deliberately does not cover</b>, because real data says no
/// rule for them is known yet:
/// </para>
/// <list type="bullet">
/// <item>A dimension anchored on a <c>FilledRegion</c>. Two rules were
/// tried on real data and both failed - the centroid was out by 270mm to
/// 1.27km, and the nearest boundary vertex collapses both anchors
/// together, turning a real 1200mm into 1.36mm. 0 of 4 within 5mm.</item>
/// <item>A dimension whose two references resolve to the <i>same</i>
/// element - measuring between two features of one object, 5 of 23 on a
/// real view. Nothing here can say which two, and picking the pair whose
/// separation matches the stated value would be fitting to the answer.</item>
/// </list>
/// <para>
/// Both are reported as coverage rather than skipped silently, so a
/// reviewer knows they remain theirs.
/// </para>
/// </remarks>
public static class DrawnDimensionConsistencyCheck
{
    public const string RuleId = "revitcheck.drawn_dimension_consistency";

    private const int MaxListed = 5;

    public static List<Issue> Run(RevitModel model, RuleConfig config) =>
        RunWithScope(model, config).Issues;

    public static (List<Issue> Issues, List<long> InvestigatedElementIds) RunWithScope(
        RevitModel model, RuleConfig config)
    {
        var issues = new List<Issue>();
        var investigated = new List<long>();

        var views = model.Views.ToDictionary(v => v.ElementId, v => v);

        var searched = model.Dimensions.Where(d => !d.IsSpot && AnySearchRan(d)).ToList();
        if (searched.Count == 0)
        {
            issues.Add(Coverage(
                "No dimension had a witness-geometry search run for it, so nothing was compared against the " +
                "model. This check needs a capture or a run that populates nearby model points per reference."));
            return (issues, investigated);
        }

        var unanchored = new List<long>();
        var noGeometry = new List<long>();
        var sameElement = new List<long>();
        var noViewDirection = new List<long>();

        foreach (var dimension in searched)
        {
            views.TryGetValue(dimension.ViewId, out var view);

            if (dimension.References.Count != 2)
            {
                continue;
            }

            var a = dimension.References[0];
            var b = dimension.References[1];

            if (a.ElementId == b.ElementId)
            {
                sameElement.Add(dimension.ElementId);
                continue;
            }

            if (a.Anchor is not { } anchorA || b.Anchor is not { } anchorB)
            {
                unanchored.Add(dimension.ElementId);
                continue;
            }

            if (!InPlaneGeometry.CanMeasureInPlane(view?.ViewDirection))
            {
                // Without the view's own direction there is no way to
                // measure what the drawing sees, and the 3D fallback is
                // exactly the answer that averaged 21mm of error. Refused
                // rather than reported weakly.
                noViewDirection.Add(dimension.ElementId);
                continue;
            }

            var pointA = NearestInPlane(anchorA, a, view, config);
            var pointB = NearestInPlane(anchorB, b, view, config);
            if (pointA is null || pointB is null)
            {
                noGeometry.Add(dimension.ElementId);
                continue;
            }

            if (StatedMm(dimension) is not { } stated)
            {
                unanchored.Add(dimension.ElementId);
                continue;
            }

            investigated.Add(dimension.ElementId);

            var modelMm = InPlaneGeometry.DistanceMm(pointA.Point, pointB.Point, view!.ViewDirection);
            var deltaMm = Math.Abs(modelMm - stated);
            if (deltaMm <= config.DrawnDimensionToleranceMm)
            {
                continue;
            }

            issues.Add(new Issue
            {
                RuleId = RuleId,
                Category = "geometry",
                Severity = "high",
                ElementId = dimension.ElementId,
                UniqueId = dimension.UniqueId,
                ViewId = dimension.ViewId,
                ViewName = view.Name,
                SheetNo = view.SheetNo,
                Description =
                    $"Dimension {dimension.ElementId} states {Mm(stated)}mm, but the model geometry it sits " +
                    $"against measures {Mm(modelMm)}mm as this view sees it - {Mm(deltaMm)}mm out, beyond the " +
                    $"{Mm(config.DrawnDimensionToleranceMm)}mm tolerance. Measured between elements " +
                    $"{pointA.SourceElementId} and {pointB.SourceElementId}.",
                SuggestedFix = new Dictionary<string, object?>
                {
                    ["stated_mm"] = stated,
                    ["model_mm"] = modelMm,
                    ["delta_mm"] = deltaMm,
                    ["measured_between_element_ids"] = new List<long?>
                    {
                        pointA.SourceElementId, pointB.SourceElementId,
                    },
                },
            });
        }

        issues.AddRange(CoverageNotes(sameElement, noGeometry, unanchored, noViewDirection, config));
        return (issues, investigated.Distinct().ToList());
    }

    /// <summary>
    /// The candidate nearest this witness <i>as the view sees it</i>, from
    /// the faces seen edge-on.
    /// </summary>
    /// <remarks>
    /// Both halves matter and both come from real data. Selecting by 3D
    /// proximity picks faces at the wrong depth - that alone is the
    /// difference between 21.09mm and 1.44mm of mean error. Restricting to
    /// edge-on faces drops roughly 59 of every 70 candidates, because a
    /// face turned towards the viewer is background nobody dimensions to.
    /// </remarks>
    private static WitnessPointInfo? NearestInPlane(
        Point3D anchor, ReferenceInfo reference, ViewInfo? view, RuleConfig config)
    {
        WitnessPointInfo? best = null;
        var bestDistance = double.MaxValue;

        foreach (var candidate in reference.NearbyModelPoints)
        {
            if (!InPlaneGeometry.IsEdgeOn(
                    candidate.FaceNormal, view?.ViewDirection, config.DrawnDimensionEdgeOnToleranceSine))
            {
                continue;
            }

            var distance = InPlaneGeometry.DistanceMm(anchor, candidate.Point, view?.ViewDirection);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// What the drawing says: the typed override where there is one, since
    /// that is what a reader acts on, otherwise the measured value. Null for
    /// an override that is not a number - a bar mark or "500 MIN." states
    /// something this comparison cannot use.
    /// </summary>
    private static double? StatedMm(DimensionInfo dimension)
    {
        if (dimension.Segments.Count != 1)
        {
            return null;
        }

        var segment = dimension.Segments[0];
        return string.IsNullOrWhiteSpace(segment.ValueOverride)
            ? segment.ValueMm
            : DimensionOverrideConsistencyCheck.ParseOverrideMm(segment.ValueOverride);
    }

    private static bool AnySearchRan(DimensionInfo dimension) =>
        dimension.References.Any(r => r.WitnessSearchPerformed);

    private static List<Issue> CoverageNotes(
        List<long> sameElement,
        List<long> noGeometry,
        List<long> unanchored,
        List<long> noViewDirection,
        RuleConfig config)
    {
        var notes = new List<Issue>();

        if (sameElement.Count > 0)
        {
            notes.Add(Coverage(
                $"{sameElement.Count} dimension(s) measure between two features of a single element, which " +
                "nothing here can resolve - picking the pair whose separation matches the stated value would " +
                "be fitting to the answer. A reviewer's: " + List(sameElement)));
        }

        if (unanchored.Count > 0)
        {
            notes.Add(Coverage(
                $"{unanchored.Count} dimension(s) had no usable witness anchor or no numeric stated value and " +
                "were not compared. A filled region is the common case: neither its centroid nor its nearest " +
                "boundary vertex has matched a real measured value on any run. " + List(unanchored)));
        }

        if (noGeometry.Count > 0)
        {
            notes.Add(Coverage(
                $"{noGeometry.Count} dimension(s) had an anchor but no model face seen edge-on near it, so " +
                $"there was nothing to measure against (searched {Mm(config.DrawnDimensionSearchRadiusMm)}mm " +
                "around each witness). " + List(noGeometry)));
        }

        if (noViewDirection.Count > 0)
        {
            notes.Add(Coverage(
                $"{noViewDirection.Count} dimension(s) are in a view with no recorded direction, so what the " +
                "drawing sees could not be measured. A capture taken before 2026-09-11 carries none - re-capture " +
                "to check these. " + List(noViewDirection)));
        }

        return notes;
    }

    private static string List(List<long> ids) =>
        string.Join(", ", ids.Take(MaxListed)) + (ids.Count > MaxListed ? ", ..." : "") + ".";

    private static string Mm(double value) =>
        value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static Issue Coverage(string description) => new()
    {
        RuleId = RuleId,
        Category = InvestigationReconciliation.CoverageCategory,
        Severity = "low",
        Description = description,
    };
}
