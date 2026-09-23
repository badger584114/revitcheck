using System;
using System.Collections.Generic;
using System.Linq;
using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Which investigation check, if any, can ever reach a given triaged
/// dimension - and where none can, why not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added 2026-09-10, to close off dimension checking.</b> Triage raises a
/// drafted dimension and says, correctly, that the file cannot show whether
/// it has drifted. What it never said is <i>whether anything will ever be
/// able to</i>. On real model 100302, of the 133 dimensions triage puts in
/// front of a reviewer, 59 are pile tag-to-tag (Pile Chain Bearing reaches
/// them) and 20 are spots (Spot Elevation reaches the elevations) - but
/// <b>54 have no tool at all</b>, and sat in <c>StillOpenTriage</c>
/// indistinguishable from work merely not done yet.
/// </para>
/// <para>
/// <b>Those 54 are not waiting for a tool that is coming.</b> Verifying an
/// ordinary linear dimension against the model needs its witness points, and
/// PLANNING.md §14's diagnostic went at exactly that seven times on real
/// drawings: <c>Reference.GlobalPoint</c> was null for all 17 references
/// tested; the <c>Location</c> fallback returned <c>(0,0,0)</c> for real
/// model geometry, which looks like data and is not; <c>Dimension.Curve</c>
/// threw on 41 of 46; and the one reliable anchor,
/// <c>DimensionSegment.Origin</c>, is where the dimension's <i>text</i>
/// sits - measured 527m from its own witness lines on a real dimension,
/// because drafters drag text. A drafting view is a harder no again: it has
/// no model behind it at all.
/// </para>
/// <para>
/// <b>Drafting views are out of scope by decision, not only by
/// limitation</b> (the user, 2026-09-11): "the drafting views can be
/// ignored for this tool, unless we can come up with a way to cross check
/// views across plans, section and multiple sheets - I think they are out
/// of the scope of what can be done for now." That names the one condition
/// under which it reopens, and it is a real one: a detail has no model to
/// check against, but two drawings of the same detail can still be checked
/// against each other. Until that exists, a drafting view's dimensions are
/// raised by triage and settled by a person.
/// </para>
/// <para>
/// So this is a statement of reach, not a guess about difficulty, and it
/// belongs in code rather than in a document nobody reads at review time.
/// A dimension no check can reach is a reviewer's call by construction -
/// which is exactly the outcome §21 added the third verdict for, and this
/// is what stops them having to find those 54 themselves.
/// </para>
/// </remarks>
public static class DimensionResolution
{
    /// <summary>The <see cref="Issue.SuggestedFix"/> key naming the rule that can settle a dimension, or null.</summary>
    public const string ResolvableByKey = "resolvable_by";

    /// <summary>The <see cref="Issue.SuggestedFix"/> key explaining why nothing can.</summary>
    public const string UnreachableReasonKey = "unreachable_reason";

    /// <summary>What can settle one dimension, and why not when nothing can.</summary>
    public readonly struct Path
    {
        public Path(string? ruleId, string? reason)
        {
            RuleId = ruleId;
            Reason = reason;
        }

        /// <summary>The investigation rule that can reach this dimension, or null when none can.</summary>
        public string? RuleId { get; }

        /// <summary>Why no check can reach it - null when one can.</summary>
        public string? Reason { get; }

        public bool Reachable => RuleId is not null;
    }

    /// <summary>
    /// Which check can settle <paramref name="dimension"/>, given the view
    /// it lives in.
    /// </summary>
    /// <remarks>
    /// Classifies on the <b>shape of its references</b>, which is what
    /// decides reachability - not on element type or view type, per §18's
    /// correction. The one view-level fact that overrides everything is a
    /// drafting view, where there is no model to check against at all.
    /// </remarks>
    public static Path For(DimensionInfo dimension, ViewInfo? view)
    {
        if (view is not null && ViewScoping.IsUnlinkedDraftingView(view))
        {
            return new Path(null,
                "it is in a drafting view, which has no model behind it - nothing can verify this against " +
                "the model because there is nothing to verify it against");
        }

        if (dimension.IsSpot)
        {
            // Spot coordinates state a plan position, so the elevation
            // check cannot answer for one either (2026-09-09).
            return IsElevationSpot(dimension)
                ? new Path(SpotElevationConsistencyCheck.RuleId, null)
                : new Path(null,
                    "it is a spot coordinate, which states a plan position rather than a level - the Spot " +
                    "Elevation check compares a drafted level against real horizontal faces and cannot " +
                    "answer for one");
        }

        var references = dimension.References;
        if (references.Count >= 2 && references.All(IsPileTag) && IsPlanView(view))
        {
            // Named as the dimension check rather than the bearing one:
            // both reconcile a triaged pile dimension, but only this one
            // compares the dimension's own stated distance against the
            // model. The bearing check uses it as chain topology and never
            // reads its value (2026-09-10).
            return new Path(PileDimensionConsistencyCheck.RuleId, null);
        }

        if (references.Count != 2)
        {
            // The drafted-dimension check compares one pair of anchors, so a
            // chain across three or more references - or a dimension with a
            // single one - is outside what it can answer. Reported as its
            // own shape rather than lumped in with the witness-point case,
            // which is a different and older reason.
            return new Path(null,
                "it does not measure between exactly two references (a chain across three or more, or a " +
                "single reference), and the drafted-dimension check compares one pair of anchors");
        }

        // Everything else in a model-backed view: the drafted-dimension
        // check anchors on the referenced element's own geometry and
        // compares in the view plane (§28). Routing these here rather than
        // to "unreachable" is the decision §28 deferred to the first real
        // run, and that run answered it - the findings were, per the user,
        // "mostly reasonable".
        return new Path(DrawnDimensionConsistencyCheck.RuleId, null);
    }

    /// <summary>Every distinct rule that could settle some part of <paramref name="dimensions"/>.</summary>
    public static List<string> ReachableRules(IEnumerable<DimensionInfo> dimensions, ViewInfo? view) =>
        dimensions
            .Select(d => For(d, view).RuleId)
            .Where(r => r is not null)
            .Distinct(StringComparer.Ordinal)
            .Select(r => r!)
            .ToList();

    /// <summary>
    /// A short per-shape tally for a run summary or a checklist column -
    /// "12 pile tag-to-tag, 3 spot elevation, 5 unreachable".
    /// </summary>
    /// <remarks>
    /// The point §18 named: a reviewer should be able to tell which button
    /// applies to a view without already knowing the answer.
    /// </remarks>
    public static string Describe(IEnumerable<DimensionInfo> dimensions, ViewInfo? view)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var dimension in dimensions)
        {
            var path = For(dimension, view);
            var label = path.RuleId switch
            {
                PileDimensionConsistencyCheck.RuleId => "pile tag-to-tag",
                SpotElevationConsistencyCheck.RuleId => "spot elevation",
                DrawnDimensionConsistencyCheck.RuleId => "drafted dimension",
                _ => "unreachable",
            };

            counts[label] = counts.TryGetValue(label, out var existing) ? existing + 1 : 1;
        }

        return counts.Count == 0
            ? "none"
            : string.Join(", ", counts.OrderByDescending(p => p.Value).Select(p => $"{p.Value} {p.Key}"));
    }

    private static bool IsElevationSpot(DimensionInfo dimension) =>
        dimension.SpotStyle is null ||
        string.Equals(dimension.SpotStyle, "SpotElevation", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A pile chain is dimensioned tag to tag, so every reference is an
    /// annotation symbol - the shape <c>PileChainReconstruction</c> resolves
    /// by proximity. Read off the reference's own class, which is raw API
    /// data rather than a family or type name a project can rename.
    /// </summary>
    /// <remarks>
    /// <b>Not sufficient on its own - see <see cref="IsPlanView"/>.</b> A
    /// detail component is also an <c>AnnotationSymbol</c> (§27 found 9 of 15
    /// references on real sections were exactly that), so this class test
    /// alone called ordinary drafted section dimensions pile chains.
    /// </remarks>
    private static bool IsPileTag(ReferenceInfo reference) =>
        string.Equals(reference.ClassName, "AnnotationSymbol", StringComparison.Ordinal);

    /// <summary>
    /// Pile setout is drawn in plan, so only a plan view can carry a pile
    /// chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-09-23, from a real run: "the check dimensions button is
    /// not doing much, it is clearing very few issues".</b> Because a detail
    /// component is an <c>AnnotationSymbol</c>, every drafted
    /// component-to-component dimension on a section was being labelled
    /// reachable by <see cref="PileDimensionConsistencyCheck"/> - a check
    /// that finds no piles in a section and so investigates none of them.
    /// Those dimensions never cleared, and because they were marked
    /// reachable they never routed to manual review either: they simply sat
    /// there. Measured on the committed capture: <b>49 Section views, 6
    /// Detail views and 7 plans</b>, with <c>DRG-2873175 - SECTION 8</c>
    /// having all 7 of its drafted dimensions misrouted.
    /// </para>
    /// <para>
    /// Tested on <see cref="ViewInfo.ViewType"/>, which is Revit's own
    /// value, not a drafting convention - and by suffix rather than a fixed
    /// list, so a plan type this build has never seen still counts as one.
    /// </para>
    /// </remarks>
    private static bool IsPlanView(ViewInfo? view) =>
        view is not null && view.ViewType.EndsWith("Plan", StringComparison.Ordinal);
}
