using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Reporting;
using Xunit;

namespace RevitCheck.Core.Tests;

public class SpotElevationConsistencyCheckTests
{
    private static List<Issues.Issue> Run(RevitModel model, RuleConfig? config = null) =>
        SpotElevationConsistencyCheck.Run(model, config ?? new RuleConfig());

    private static (List<Issues.Issue> Issues, List<long> InvestigatedElementIds) RunWithScope(RevitModel model, RuleConfig? config = null) =>
        SpotElevationConsistencyCheck.RunWithScope(model, config ?? new RuleConfig());

    private static DimensionInfo SpotDimension(
        long elementId,
        long viewId = 10,
        double? originZMm = 1000.0,
        bool shelfSearchPerformed = true,
        List<NearbyFaceInfo>? faces = null,
        string? spotStyle = null) =>
        new()
        {
            ElementId = elementId,
            ViewId = viewId,
            IsSpot = true,
            SpotStyle = spotStyle,
            Origin = originZMm is { } z ? new Point3D { X = 0, Y = 0, Z = z } : null,
            ShelfSearchPerformed = shelfSearchPerformed,
            NearbyHorizontalFaces = faces ?? new List<NearbyFaceInfo>(),
        };

    private static Issues.Issue Coverage(List<Issues.Issue> issues) =>
        Assert.Single(issues, i => i.Category == "coverage" && i.SuggestedFix != null);

    /// <summary>
    /// From the user, 2026-09-09: "the spot elevation check is finding spot
    /// coordinates, they will often be attached to linework as they will
    /// not snap to model geometry accurately." A spot coordinate answers
    /// where in plan, not at what level, so this check's comparison cannot
    /// say anything about one - and a wrong-looking Z on one must never
    /// become a finding.
    /// </summary>
    [Fact]
    public void A_spot_coordinate_is_set_aside_rather_than_answered_wrongly()
    {
        var faces = new List<NearbyFaceInfo> { new() { ZMm = 5000.0, Distance2DMm = 10.0, SourceElementId = 99 } };
        var model = new RevitModel
        {
            Dimensions = new List<DimensionInfo>
            {
                // Its Z is 3m from the only face near it - which would be a
                // loud mismatch if this were a Spot Elevation.
                SpotDimension(1, originZMm: 8000.0, faces: faces, spotStyle: "SpotCoordinate"),
            },
        };

        var issues = Run(model);

        Assert.DoesNotContain(issues, i => i.Category == "geometry");
        Assert.Contains(issues, i =>
            i.Category == "coverage" && i.Description.Contains("not Spot Elevations"));
    }

    /// <summary>
    /// The exclusion is by style, not by the check quietly finding nothing:
    /// a real Spot Elevation alongside it is still checked and still flagged.
    /// </summary>
    [Fact]
    public void A_real_spot_elevation_beside_a_coordinate_is_still_checked()
    {
        var faces = new List<NearbyFaceInfo> { new() { ZMm = 5000.0, Distance2DMm = 10.0, SourceElementId = 99 } };
        var model = new RevitModel
        {
            Dimensions = new List<DimensionInfo>
            {
                SpotDimension(1, originZMm: 8000.0, faces: faces, spotStyle: "SpotCoordinate"),
                SpotDimension(2, originZMm: 8000.0, faces: faces, spotStyle: "SpotElevation"),
            },
        };

        var flagged = Assert.Single(Run(model), i => i.Category == "geometry");
        Assert.Equal(2, flagged.ElementId);
    }

    /// <summary>
    /// A capture predating SpotStyle carries null for every spot. Narrowing
    /// those to nothing would turn an old capture into a confidently empty
    /// run - this project's most repeated failure - so an unknown style is
    /// still checked.
    /// </summary>
    [Fact]
    public void A_spot_of_unknown_style_is_still_checked_not_silently_skipped()
    {
        var faces = new List<NearbyFaceInfo> { new() { ZMm = 5000.0, Distance2DMm = 10.0, SourceElementId = 99 } };
        var model = new RevitModel
        {
            Dimensions = new List<DimensionInfo> { SpotDimension(1, originZMm: 8000.0, faces: faces) },
        };

        Assert.Contains(Run(model), i => i.Category == "geometry");
    }

    [Fact]
    public void NoSpotDimensionsReportsCoverageNotSilence()
    {
        var model = new RevitModel { Dimensions = new List<DimensionInfo>() };
        var issues = Run(model);
        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
    }

    [Fact]
    public void AnUnsearchedSpotIsNotFlaggedButIsCounted()
    {
        var dims = new List<DimensionInfo> { SpotDimension(1, shelfSearchPerformed: false) };
        var model = new RevitModel { Dimensions = dims };
        var issues = Run(model);

        // Only the coverage issue - nothing else was emitted for a
        // dimension the search never ran against.
        var coverage = Assert.Single(issues);
        Assert.Equal(1, coverage.SuggestedFix!["total_spot_elevations"]);
        Assert.Equal(1, coverage.SuggestedFix!["not_searched"]);
        Assert.Contains("not the same as being confirmed clean", coverage.Description);
    }

    [Fact]
    public void NoOriginIsReportedAsUncheckedNotClean()
    {
        var dims = new List<DimensionInfo> { SpotDimension(1, originZMm: null) };
        var model = new RevitModel { Dimensions = dims };
        var issues = Run(model);

        Assert.Equal(2, issues.Count);
        var finding = issues.Single(i => i.RuleId == SpotElevationConsistencyCheck.RuleId && i.ElementId == 1);
        // manual_review, not coverage - this dimension WAS investigated,
        // just inconclusively (see the check's own remarks on why plain
        // coverage/geometry here would wrongly auto-export as confirmed).
        Assert.Equal(InvestigationReconciliation.ManualReviewCategory, finding.Category);
        Assert.Equal("medium", finding.Severity);
        Assert.Contains("no Origin captured", finding.Description);
        Assert.Equal(1, Coverage(issues).SuggestedFix!["no_value"]);
    }

    [Fact]
    public void NoNearbyGeometryIsReportedNotAssumedCorrect()
    {
        var dims = new List<DimensionInfo> { SpotDimension(1, originZMm: 5000.0, faces: new List<NearbyFaceInfo>()) };
        var model = new RevitModel { Dimensions = dims };
        var issues = Run(model);

        var finding = issues.Single(i => i.ElementId == 1);
        Assert.Equal(InvestigationReconciliation.ManualReviewCategory, finding.Category);
        Assert.Equal("medium", finding.Severity);
        Assert.Contains("no real geometry was found nearby", finding.Description);
        Assert.Equal(1, Coverage(issues).SuggestedFix!["no_candidate"]);
    }

    [Fact]
    public void ANearFaceWithinToleranceIsClean()
    {
        var faces = new List<NearbyFaceInfo>
        {
            new() { ZMm = 5002.0, Distance2DMm = 0.0, SourceElementId = 99 },
        };
        var dims = new List<DimensionInfo> { SpotDimension(1, originZMm: 5000.0, faces: faces) };
        var model = new RevitModel { Dimensions = dims };
        var issues = Run(model);

        // Only the coverage issue - within the default 10mm tolerance.
        var coverage = Assert.Single(issues);
        Assert.Equal(1, coverage.SuggestedFix!["confirmed"]);
    }

    [Fact]
    public void AFaceBeyondToleranceIsFlagged()
    {
        var faces = new List<NearbyFaceInfo>
        {
            new() { ZMm = 5050.0, Distance2DMm = 0.0, SourceElementId = 99 },
        };
        var dims = new List<DimensionInfo> { SpotDimension(1, originZMm: 5000.0, faces: faces) };
        var model = new RevitModel { Dimensions = dims };
        var issues = Run(model);

        var finding = issues.Single(i => i.ElementId == 1);
        Assert.Equal("high", finding.Severity);
        Assert.Equal(-50.0, finding.SuggestedFix!["delta_mm"]);
        Assert.Equal(99L, finding.SuggestedFix!["source_element_id"]);
        Assert.Equal(1, Coverage(issues).SuggestedFix!["mismatched"]);
    }

    [Fact]
    public void NearestByPlanDistanceIsJudgedNotNearestByZAgreement()
    {
        // A farther face that happens to agree exactly in Z must not save
        // a nearer face that disagrees - see the check's own class remarks
        // on why picking by Z agreement would be circular.
        var faces = new List<NearbyFaceInfo>
        {
            new() { ZMm = 5000.0, Distance2DMm = 3000.0, SourceElementId = 1 }, // far, agrees exactly
            new() { ZMm = 5040.0, Distance2DMm = 10.0, SourceElementId = 2 },   // near, disagrees
        };
        var dims = new List<DimensionInfo> { SpotDimension(1, originZMm: 5000.0, faces: faces) };
        var model = new RevitModel { Dimensions = dims };
        var issues = Run(model);

        var finding = issues.Single(i => i.ElementId == 1);
        Assert.Equal("high", finding.Severity);
        Assert.Equal(2L, finding.SuggestedFix!["source_element_id"]);
        Assert.Equal(-40.0, finding.SuggestedFix!["delta_mm"]);
    }

    [Fact]
    public void NonSpotDimensionsAreIgnored()
    {
        var dims = new List<DimensionInfo>
        {
            new() { ElementId = 1, ViewId = 10, IsSpot = false },
        };
        var model = new RevitModel { Dimensions = dims };
        var issues = Run(model);

        var issue = Assert.Single(issues);
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("No Spot Elevations were found", issue.Description);
    }

    [Fact]
    public void ToleranceIsConfigurable()
    {
        var faces = new List<NearbyFaceInfo> { new() { ZMm = 5008.0, Distance2DMm = 0.0 } };
        var dims = new List<DimensionInfo> { SpotDimension(1, originZMm: 5000.0, faces: faces) };
        var model = new RevitModel { Dimensions = dims };

        var loose = Run(model, new RuleConfig { SpotElevationToleranceMm = 10.0 });
        Assert.Equal(1, Coverage(loose).SuggestedFix!["confirmed"]);

        var strict = Run(model, new RuleConfig { SpotElevationToleranceMm = 5.0 });
        Assert.Equal(1, Coverage(strict).SuggestedFix!["mismatched"]);
    }

    [Fact]
    public void UnsearchedDimensionsAreExcludedFromTheInvestigatedScope()
    {
        var faces = new List<NearbyFaceInfo> { new() { ZMm = 5002.0, Distance2DMm = 0.0 } };
        var dims = new List<DimensionInfo>
        {
            SpotDimension(1, originZMm: 5000.0, faces: faces), // searched, confirmed
            SpotDimension(2, shelfSearchPerformed: false), // never in scope for this check at all
        };
        var model = new RevitModel { Dimensions = dims };

        var (issues, investigated) = RunWithScope(model);

        Assert.Equal(new List<long> { 1 }, investigated);
        // The confirmed dimension carries no issue of its own - only the
        // coverage summary - matching every other clean-verdict check in
        // this codebase.
        Assert.DoesNotContain(issues, i => i.ElementId == 1);
    }

    [Fact]
    public void EveryOutcomeCountsAsInvestigatedIncludingInconclusiveOnes()
    {
        // Confirmed, mismatched, no-candidate and no-value all reached a
        // real verdict-attempt - all four belong in the investigated
        // scope, since the point is whether the check looked, not whether
        // it managed a confident automated answer (mirrors
        // PileChainBearingConsistencyCheck.RunWithScope's own reasoning).
        var confirmedFace = new List<NearbyFaceInfo> { new() { ZMm = 5000.0, Distance2DMm = 0.0 } };
        var mismatchedFace = new List<NearbyFaceInfo> { new() { ZMm = 5050.0, Distance2DMm = 0.0 } };
        var dims = new List<DimensionInfo>
        {
            SpotDimension(1, originZMm: 5000.0, faces: confirmedFace),
            SpotDimension(2, originZMm: 5000.0, faces: mismatchedFace),
            SpotDimension(3, originZMm: 5000.0, faces: new List<NearbyFaceInfo>()),
            SpotDimension(4, originZMm: null),
        };
        var model = new RevitModel { Dimensions = dims };

        var (_, investigated) = RunWithScope(model);

        Assert.Equal(new List<long> { 1, 2, 3, 4 }, investigated);
    }
}
