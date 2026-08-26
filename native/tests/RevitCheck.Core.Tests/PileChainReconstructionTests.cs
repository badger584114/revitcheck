using RevitCheck.Core.Checks;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

public class PileChainReconstructionTests
{
    private static readonly RuleConfig DefaultConfig = new();

    [Fact]
    public void ResolvePileMatch_matches_two_confidently_tagged_piles()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0.2)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 999.9)));

        var match = PileChainReconstruction.ResolvePileMatch(dim, new[] { pileA, pileB }, DefaultConfig);

        Assert.NotNull(match);
        Assert.Equal(1, match!.Value.PileA.ElementId);
        Assert.Equal(2, match.Value.PileB.ElementId);
    }

    [Fact]
    public void ResolvePileMatch_returns_null_when_a_reference_is_beyond_tolerance()
    {
        // Real confirmed shape for a setout-point marker, not a pile tag -
        // the user's own correction, 2026-08-26: this reference is 1274.5mm
        // from its nearest pile in the real data, nothing like the ~0mm
        // every genuine pile tag showed.
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(1274.5, 2274.5)));

        var match = PileChainReconstruction.ResolvePileMatch(dim, new[] { pileA, pileB }, DefaultConfig);

        Assert.Null(match);
    }

    [Fact]
    public void ResolvePileMatch_returns_null_when_both_references_resolve_to_the_same_pile()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var dim = RevitCheckTestBuilders.PileChainDimension(
            100, 1,
            RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
            RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0.1, 0.1)));

        var match = PileChainReconstruction.ResolvePileMatch(dim, new[] { pileA }, DefaultConfig);

        Assert.Null(match);
    }

    [Fact]
    public void ResolvePileMatch_returns_null_for_a_dimension_with_more_than_two_references()
    {
        var piles = new[]
        {
            RevitCheckTestBuilders.Pile(1, "P1", 0, 0),
            RevitCheckTestBuilders.Pile(2, "P2", 0, 1000),
            RevitCheckTestBuilders.Pile(3, "P3", 0, 2000),
        };
        var dim = new RevitCheck.Core.Ir.DimensionInfo
        {
            ElementId = 100,
            ViewId = 1,
            References = new List<RevitCheck.Core.Ir.ReferenceInfo>
            {
                RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
                RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 1000)),
                RevitCheckTestBuilders.TagRef(202, RevitCheckTestBuilders.Pt(0, 2000)),
            },
        };

        var match = PileChainReconstruction.ResolvePileMatch(dim, piles, DefaultConfig);

        Assert.Null(match);
    }

    [Fact]
    public void BuildChains_orders_a_simple_four_pile_chain_end_to_end()
    {
        var p1 = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var p2 = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var p3 = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);
        var p4 = RevitCheckTestBuilders.Pile(4, "P4", 0, 3000);

        var edges = new List<PileChainEdge>
        {
            new(p1, p2, 101),
            new(p2, p3, 102),
            new(p3, p4, 103),
        };

        var result = PileChainReconstruction.BuildChains(edges);

        Assert.Empty(result.AmbiguousComponents);
        var chain = Assert.Single(result.Chains);
        Assert.Equal(4, chain.PilesInOrder.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, chain.PilesInOrder.Select(p => p.ElementId).ToArray());
        Assert.Equal(new long[] { 101, 102, 103 }, chain.DimensionElementIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public void BuildChains_finds_two_independent_chains()
    {
        var p1 = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var p2 = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var p3 = RevitCheckTestBuilders.Pile(3, "P3", 5000, 0);
        var p4 = RevitCheckTestBuilders.Pile(4, "P4", 5000, 1000);

        var edges = new List<PileChainEdge> { new(p1, p2, 101), new(p3, p4, 102) };

        var result = PileChainReconstruction.BuildChains(edges);

        Assert.Equal(2, result.Chains.Count);
        Assert.All(result.Chains, c => Assert.Equal(2, c.PilesInOrder.Count));
    }

    [Fact]
    public void BuildChains_treats_a_branch_as_ambiguous_not_a_chain()
    {
        // Bridges are always set out linearly (PLANNING.md §5b, confirmed
        // by the user) - a real branch here means the tag-to-pile matching
        // got something wrong, not a real structure to interpret.
        var hub = RevitCheckTestBuilders.Pile(1, "HUB", 0, 0);
        var a = RevitCheckTestBuilders.Pile(2, "A", 0, 1000);
        var b = RevitCheckTestBuilders.Pile(3, "B", 1000, 0);
        var c = RevitCheckTestBuilders.Pile(4, "C", 0, -1000);

        var edges = new List<PileChainEdge> { new(hub, a, 101), new(hub, b, 102), new(hub, c, 103) };

        var result = PileChainReconstruction.BuildChains(edges);

        Assert.Empty(result.Chains);
        var component = Assert.Single(result.AmbiguousComponents);
        Assert.Equal(4, component.Count);
    }

    [Fact]
    public void BuildChains_treats_a_cycle_as_ambiguous_not_a_chain()
    {
        var p1 = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var p2 = RevitCheckTestBuilders.Pile(2, "P2", 1000, 0);
        var p3 = RevitCheckTestBuilders.Pile(3, "P3", 500, 866);

        var edges = new List<PileChainEdge> { new(p1, p2, 101), new(p2, p3, 102), new(p3, p1, 103) };

        var result = PileChainReconstruction.BuildChains(edges);

        Assert.Empty(result.Chains);
        Assert.Single(result.AmbiguousComponents);
    }
}
