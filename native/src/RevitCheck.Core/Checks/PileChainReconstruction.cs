using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Checks;

/// <summary>One dimension's confidently-matched pair of piles - a graph edge, not a finding.</summary>
public sealed record PileChainEdge(ElementMetadata PileA, ElementMetadata PileB, long DimensionElementId);

/// <summary>A resolved, ordered run of piles connected end-to-end by confident tag-to-pile-matched dimensions.</summary>
public sealed class PileChain
{
    public required IReadOnlyList<ElementMetadata> PilesInOrder { get; init; }

    /// <summary>The dimension(s) whose tag-to-pile matches built this chain - carried through for auditability (CLAUDE.md: a rule must say how it reached a conclusion).</summary>
    public required IReadOnlyList<long> DimensionElementIds { get; init; }
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
            for (var i = 0; i < order.Count - 1; i++)
            {
                dimensionIds.AddRange(edgeDimensions[OrderedKey(order[i], order[i + 1])]);
            }

            result.Chains.Add(new PileChain
            {
                PilesInOrder = order.Select(id => byId[id]).ToList(),
                DimensionElementIds = dimensionIds,
            });
        }

        return result;
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
