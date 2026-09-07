using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Checks;

/// <summary>One dimension's confidently-matched pair of piles - a graph edge, not a finding.</summary>
public sealed record PileChainEdge(ElementMetadata PileA, ElementMetadata PileB, long DimensionElementId);

/// <summary>A resolved, ordered run of piles connected end-to-end by confident tag-to-pile-matched dimensions.</summary>
/// <remarks>
/// Topologically a simple path, which is <em>not</em> the same as
/// geometrically straight - two different setout lines meeting at a shared
/// pile form a perfectly simple path with a corner in it. Establishing
/// straightness is <see cref="PileChainReconstruction.SplitIntoStraightRuns"/>'s
/// job, and every consumer that fits a single bearing to a chain must go
/// through it first; see its own remarks for the real false positive that
/// forced this distinction.
/// </remarks>
public sealed class PileChain
{
    public required IReadOnlyList<ElementMetadata> PilesInOrder { get; init; }

    /// <summary>The dimension(s) whose tag-to-pile matches built this chain - carried through for auditability (CLAUDE.md: a rule must say how it reached a conclusion).</summary>
    public required IReadOnlyList<long> DimensionElementIds { get; init; }

    /// <summary>
    /// The same dimension ids as <see cref="DimensionElementIds"/> but kept
    /// per edge - entry <c>i</c> holds the dimension(s) matching
    /// <see cref="PilesInOrder"/>[i] to [i+1], so there is one entry per
    /// edge and always exactly one fewer than there are piles. Needed
    /// because a chain that splits into separate straight runs has to
    /// attribute its dimensions to the run they actually belong to;
    /// the flattened list alone loses the edge boundaries.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<long>> EdgeDimensionElementIds { get; init; }
}

/// <summary>
/// A maximal geometrically straight run of piles inside one
/// <see cref="PileChain"/> - every consecutive edge within it points the
/// same way to within <see cref="RuleConfig.PileChainCollinearityToleranceDegrees"/>,
/// so fitting one bearing to it is meaningful.
/// </summary>
public sealed class PileChainRun
{
    public required IReadOnlyList<ElementMetadata> PilesInOrder { get; init; }

    public required IReadOnlyList<long> DimensionElementIds { get; init; }

    /// <summary>
    /// The largest disagreement (degrees) between any two consecutive edges
    /// inside this run - zero for a single-edge run, and within the
    /// collinearity tolerance by construction. Carried so a finding can
    /// state how straight the run it fitted a bearing to actually was,
    /// rather than asserting straightness without evidence.
    /// </summary>
    public required double MaxInternalDeviationDegrees { get; init; }
}

/// <summary>One pile where a chain changes direction, with the two edge bearings that meet there.</summary>
public sealed record PileChainBend(
    ElementMetadata Pile,
    double DeviationDegrees,
    double BearingBeforeDegrees,
    double BearingAfterDegrees,
    IReadOnlyList<long> DimensionElementIds);

/// <summary>The result of splitting one <see cref="PileChain"/> into straight runs.</summary>
public sealed class PileChainSplit
{
    public List<PileChainRun> Runs { get; init; } = new();

    /// <summary>Every pile at which the chain changes direction beyond tolerance - empty for a genuinely straight chain.</summary>
    public List<PileChainBend> Bends { get; init; } = new();

    /// <summary>
    /// True when at least one pile in the chain has no live project
    /// position, so straightness could not be established at all. Distinct
    /// from "straight with no bends" - a caller must not read an empty
    /// <see cref="Bends"/> list as evidence of straightness without
    /// checking this first.
    /// </summary>
    public bool PositionsIncomplete { get; init; }
}

/// <summary>The result of grouping confident pile-to-pile edges into chains.</summary>
public sealed class PileChainSet
{
    /// <summary>Clean, unambiguous linear runs - the normal, expected case (PLANNING.md §5: bridges are always set out linearly).</summary>
    public List<PileChain> Chains { get; init; } = new();

    /// <summary>A connected group of piles whose edges don't form a simple line (a branch or a cycle) - not resolved into an ordered chain, since this project's setout convention is confirmed always-linear and a branch/cycle here means something the matching itself got wrong, not a real structure to interpret.</summary>
    public List<IReadOnlyList<ElementMetadata>> AmbiguousComponents { get; init; } = new();
}

/// <summary>
/// Reconstructs real pile-to-pile setout chains directly from live model
/// geometry - the mechanism behind <see cref="PileChainBearingConsistencyCheck"/>.
/// See that class's remarks for the full real-data validation
/// (PLANNING.md §14, 2026-08-26): 31 of 32 real tag-to-tag pile
/// dimensions on a real pile-layout view matched their two nearest piles
/// with essentially exact (often floating-point-exact) agreement between
/// the dimension's own stated value and the measured pile-to-pile
/// distance, confirming both that the nearest-pile match is correct and
/// that tags sit at their own pile's location, not leader-offset.
/// </summary>
public static class PileChainReconstruction
{
    /// <summary>
    /// The two piles a dimension's own two references confidently resolve
    /// to (each within <see cref="RuleConfig.PileTagMatchToleranceMm"/> of
    /// its nearest pile, by 2D distance only - see <see cref="ReferenceInfo.LocalPoint"/>'s
    /// remarks for why 3D is deliberately excluded), or null if either
    /// reference doesn't confidently resolve, the dimension doesn't have
    /// exactly two references, or both references resolve to the same
    /// pile (not a real pair - e.g. a dimension between a pile tag and a
    /// setout-point marker sitting near the same pile).
    /// </summary>
    public static (ElementMetadata PileA, ElementMetadata PileB)? ResolvePileMatch(
        DimensionInfo dimension, IReadOnlyList<ElementMetadata> piles, RuleConfig config)
    {
        if (dimension.References.Count != 2)
        {
            return null;
        }

        var pileA = NearestPile(dimension.References[0].LocalPoint, piles, config.PileTagMatchToleranceMm);
        var pileB = NearestPile(dimension.References[1].LocalPoint, piles, config.PileTagMatchToleranceMm);
        if (pileA is null || pileB is null || pileA.ElementId == pileB.ElementId)
        {
            return null;
        }

        return (pileA, pileB);
    }

    /// <summary>
    /// True for a dimension that plausibly should be a pile-to-pile
    /// measurement but doesn't confidently resolve into a real chain edge -
    /// exactly one of its two references matches a pile within tolerance
    /// and the other doesn't, or both references match the <em>same</em>
    /// pile. Deliberately does NOT include a dimension where neither
    /// reference is anywhere near a pile - most dimensions in a pile-layout
    /// view have nothing to do with piles at all, and flagging every one of
    /// those would bury the real signal.
    /// </summary>
    /// <remarks>
    /// Found on the real Revit machine, 2026-08-31 (Stage 4): every
    /// dimension this method now catches was previously silently dropped
    /// by <see cref="ResolvePileMatch"/> returning null, with no Issue of
    /// any kind - not a confirmed problem, not a triage flag staying open,
    /// not a manual-review item, nothing at all. That's a real gap, not
    /// just a naming one: PLANNING.md §14 already confirmed the exact real
    /// shape this catches (a tag 0.0mm from one real pile, 1274.5mm from
    /// its nearest other candidate, turned out to be dimensioned to a
    /// setout-point marker rather than a pile - not a drafting bug, but
    /// something only a human reading the actual drawing can tell, not
    /// this check on its own). This is what
    /// <see cref="PileChainBearingConsistencyCheck.RunWithScope"/> uses to
    /// emit a real <c>manual_review</c> finding for it instead of silence.
    /// </remarks>
    public static bool IsNearMissPileMatch(DimensionInfo dimension, IReadOnlyList<ElementMetadata> piles, RuleConfig config)
    {
        if (dimension.References.Count != 2)
        {
            return false;
        }

        var pileA = NearestPile(dimension.References[0].LocalPoint, piles, config.PileTagMatchToleranceMm);
        var pileB = NearestPile(dimension.References[1].LocalPoint, piles, config.PileTagMatchToleranceMm);

        if (pileA is null && pileB is null)
        {
            return false;
        }

        if (pileA is not null && pileB is not null && pileA.ElementId != pileB.ElementId)
        {
            return false;
        }

        return true;
    }

    private static ElementMetadata? NearestPile(Point3D? point, IReadOnlyList<ElementMetadata> piles, double toleranceMm)
    {
        if (point is null)
        {
            return null;
        }

        ElementMetadata? best = null;
        var bestDistance = double.MaxValue;
        foreach (var pile in piles)
        {
            if (pile.LocalPoint is not { } pilePoint)
            {
                continue;
            }

            var dx = point.X - pilePoint.X;
            var dy = point.Y - pilePoint.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = pile;
            }
        }

        return best is not null && bestDistance <= toleranceMm ? best : null;
    }

    /// <summary>Every dimension in the model that confidently matches to a distinct pair of piles - one edge per dimension.</summary>
    public static List<PileChainEdge> BuildEdges(RevitModel model, IReadOnlyList<ElementMetadata> piles, RuleConfig config)
    {
        var edges = new List<PileChainEdge>();
        foreach (var dimension in model.Dimensions)
        {
            if (ResolvePileMatch(dimension, piles, config) is { } match)
            {
                edges.Add(new PileChainEdge(match.PileA, match.PileB, dimension.ElementId));
            }
        }

        return edges;
    }

    /// <summary>
    /// Groups edges into connected components, then resolves each
    /// component into an ordered <see cref="PileChain"/> only if it's a
    /// simple path (every pile has at most 2 neighbours, exactly 2 piles
    /// have exactly 1) - a branch or cycle is reported separately, not
    /// guessed at.
    /// </summary>
    public static PileChainSet BuildChains(IReadOnlyList<PileChainEdge> edges)
    {
        var byId = new Dictionary<long, ElementMetadata>();
        var adjacency = new Dictionary<long, HashSet<long>>();
        var edgeDimensions = new Dictionary<(long, long), List<long>>();

        foreach (var edge in edges)
        {
            byId[edge.PileA.ElementId] = edge.PileA;
            byId[edge.PileB.ElementId] = edge.PileB;

            AddAdjacency(adjacency, edge.PileA.ElementId, edge.PileB.ElementId);
            AddAdjacency(adjacency, edge.PileB.ElementId, edge.PileA.ElementId);

            var key = OrderedKey(edge.PileA.ElementId, edge.PileB.ElementId);
            if (!edgeDimensions.TryGetValue(key, out var dims))
            {
                dims = new List<long>();
                edgeDimensions[key] = dims;
            }

            dims.Add(edge.DimensionElementId);
        }

        var result = new PileChainSet();
        var visited = new HashSet<long>();

        foreach (var startNode in adjacency.Keys)
        {
            if (visited.Contains(startNode))
            {
                continue;
            }

            var component = CollectComponent(adjacency, startNode);
            foreach (var id in component)
            {
                visited.Add(id);
            }

            var endpoints = component.Where(id => adjacency[id].Count == 1).ToList();
            var isSimplePath = endpoints.Count == 2 && component.All(id => adjacency[id].Count <= 2);

            if (!isSimplePath)
            {
                result.AmbiguousComponents.Add(component.Select(id => byId[id]).ToList());
                continue;
            }

            var order = WalkPath(adjacency, endpoints[0]);
            var dimensionIds = new List<long>();
            var byEdge = new List<IReadOnlyList<long>>();
            for (var i = 0; i < order.Count - 1; i++)
            {
                var forEdge = edgeDimensions[OrderedKey(order[i], order[i + 1])];
                byEdge.Add(forEdge.ToList());
                dimensionIds.AddRange(forEdge);
            }

            result.Chains.Add(new PileChain
            {
                PilesInOrder = order.Select(id => byId[id]).ToList(),
                DimensionElementIds = dimensionIds,
                EdgeDimensionElementIds = byEdge,
            });
        }

        return result;
    }

    /// <summary>
    /// Splits one chain into maximal geometrically straight runs, reporting
    /// every pile where it changes direction beyond
    /// <see cref="RuleConfig.PileChainCollinearityToleranceDegrees"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists, from a real failure:</b> a chain is built from
    /// tag-to-tag dimension adjacency, so it is guaranteed to be a simple
    /// <em>path</em> and guaranteed nothing about being a straight
    /// <em>line</em>. Two setout lines that share a pile - or meet at one -
    /// form one topologically clean chain with a corner in it. Reported by
    /// the user 2026-09-07 after a real run on a second bridge model
    /// wrongly flagged a bearing: piles belonging to a different setout
    /// line had been absorbed into the line being checked.
    /// </para>
    /// <para>
    /// <b>The deeper problem this also fixes.</b> The bearing was
    /// previously measured endpoint-to-endpoint, and a line through
    /// exactly two points fits with zero residual - it cannot fail. So the
    /// old reconstruction could not detect an interior pile sitting off
    /// the line either, and would report such a chain clean: a false
    /// negative on precisely the defect the check exists to catch. Walking
    /// every edge is what gives the check any diagnostic power over a
    /// chain's interior at all.
    /// </para>
    /// <para>
    /// Both edge bearings are compared directly, never via
    /// <see cref="BearingMath.Reciprocal"/>: within a single ordered walk
    /// consecutive edges continue in the same direction, so a genuine
    /// straight run shows near-zero disagreement. The reciprocal ambiguity
    /// applies only when comparing a run against a printed bearing call,
    /// whose own direction convention is arbitrary relative to walk order.
    /// </para>
    /// <para>
    /// A pile with no live project position makes straightness
    /// unknowable rather than false - reported via
    /// <see cref="PileChainSplit.PositionsIncomplete"/> with no runs and no
    /// bends, so a caller cannot mistake it for a clean straight chain
    /// (CLAUDE.md: report a coverage indicator, never fail silently).
    /// </para>
    /// </remarks>
    public static PileChainSplit SplitIntoStraightRuns(PileChain chain, RuleConfig config)
    {
        var piles = chain.PilesInOrder;
        if (piles.Count < 2)
        {
            return new PileChainSplit();
        }

        var positions = new List<(double Easting, double Northing)>(piles.Count);
        foreach (var pile in piles)
        {
            if (pile.ProjectPositionEastingMm is not { } easting ||
                pile.ProjectPositionNorthingMm is not { } northing)
            {
                return new PileChainSplit { PositionsIncomplete = true };
            }

            positions.Add((easting, northing));
        }

        var edgeCount = piles.Count - 1;
        var bearings = new double[edgeCount];
        for (var i = 0; i < edgeCount; i++)
        {
            bearings[i] = BearingMath.AzimuthDegrees(
                positions[i].Easting, positions[i].Northing,
                positions[i + 1].Easting, positions[i + 1].Northing);
        }

        var split = new PileChainSplit();
        var runStartEdge = 0;
        var runMaxDeviation = 0.0;

        for (var i = 1; i < edgeCount; i++)
        {
            var deviation = BearingMath.AngularDifference(bearings[i], bearings[i - 1]);
            if (deviation <= config.PileChainCollinearityToleranceDegrees)
            {
                runMaxDeviation = Math.Max(runMaxDeviation, deviation);
                continue;
            }

            split.Runs.Add(BuildRun(chain, runStartEdge, i - 1, runMaxDeviation));
            split.Bends.Add(new PileChainBend(
                piles[i],
                deviation,
                bearings[i - 1],
                bearings[i],
                chain.EdgeDimensionElementIds[i - 1].Concat(chain.EdgeDimensionElementIds[i]).Distinct().ToList()));

            runStartEdge = i;
            runMaxDeviation = 0.0;
        }

        split.Runs.Add(BuildRun(chain, runStartEdge, edgeCount - 1, runMaxDeviation));
        return split;
    }

    /// <summary>One run covering edges <paramref name="startEdge"/>..<paramref name="endEdge"/> inclusive - so piles [startEdge .. endEdge + 1].</summary>
    private static PileChainRun BuildRun(PileChain chain, int startEdge, int endEdge, double maxDeviation)
    {
        var piles = new List<ElementMetadata>();
        for (var i = startEdge; i <= endEdge + 1; i++)
        {
            piles.Add(chain.PilesInOrder[i]);
        }

        var dimensionIds = new List<long>();
        for (var i = startEdge; i <= endEdge; i++)
        {
            dimensionIds.AddRange(chain.EdgeDimensionElementIds[i]);
        }

        return new PileChainRun
        {
            PilesInOrder = piles,
            DimensionElementIds = dimensionIds.Distinct().ToList(),
            MaxInternalDeviationDegrees = maxDeviation,
        };
    }

    private static void AddAdjacency(Dictionary<long, HashSet<long>> adjacency, long from, long to)
    {
        if (!adjacency.TryGetValue(from, out var set))
        {
            set = new HashSet<long>();
            adjacency[from] = set;
        }

        set.Add(to);
    }

    private static HashSet<long> CollectComponent(Dictionary<long, HashSet<long>> adjacency, long start)
    {
        var component = new HashSet<long>();
        var stack = new Stack<long>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!component.Add(current))
            {
                continue;
            }

            foreach (var neighbor in adjacency[current])
            {
                if (!component.Contains(neighbor))
                {
                    stack.Push(neighbor);
                }
            }
        }

        return component;
    }

    private static List<long> WalkPath(Dictionary<long, HashSet<long>> adjacency, long start)
    {
        var order = new List<long> { start };
        var seen = new HashSet<long> { start };
        var current = start;
        while (true)
        {
            long? next = null;
            foreach (var neighbor in adjacency[current])
            {
                if (!seen.Contains(neighbor))
                {
                    next = neighbor;
                    break;
                }
            }

            if (next is not { } nextNode)
            {
                break;
            }

            order.Add(nextNode);
            seen.Add(nextNode);
            current = nextNode;
        }

        return order;
    }

    private static (long, long) OrderedKey(long a, long b) => a < b ? (a, b) : (b, a);
}
