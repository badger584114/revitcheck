using System.Collections.Generic;
using System.Linq;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// The general drafted-dimension check (PLANNING.md §28). Shaped by five
/// real probe runs: anchors come from the referenced element's own
/// geometry, faces are restricted to those seen edge-on, and the
/// comparison is made in the view plane - which on real elevation
/// dimensions is the difference between 21.09mm and 1.44mm of mean error.
/// </summary>
public class DrawnDimensionConsistencyCheckTests
{
    private static Point3D P(double x, double y, double z = 0) => new() { X = x, Y = y, Z = z };

    /// <summary>An elevation looking along -Y, so depth is the Y axis and the drawing plane is XZ.</summary>
    private static ViewInfo Elevation(long id = 10) => new()
    {
        ElementId = id,
        Name = "DRG-2871116 - ELEVATION - BARRIER PPT234105",
        ViewType = "Elevation",
        SheetNo = "2871116",
        ViewDirection = P(0, -1, 0),
    };

    private static ReferenceInfo Witness(long elementId, Point3D anchor, params Point3D[] candidates) => new()
    {
        ElementId = elementId,
        ClassName = "AnnotationSymbol",
        ViewSpecific = true,
        LocalPoint = anchor,
        WitnessSearchPerformed = true,
        NearbyModelPoints = candidates
            .Select(c => new WitnessPointInfo
            {
                Point = c,
                SourceElementId = elementId + 1000,
                // Normal in the drawing plane -> the face is seen edge-on,
                // which is what a drafter dimensions to.
                FaceNormal = P(1, 0, 0),
            })
            .ToList(),
    };

    private static DimensionInfo Dimension(
        long id, ReferenceInfo a, ReferenceInfo b, double? statedMm, string? overrideText = null) =>
        RevitCheckTestBuilders.Dimension(id, 10, new[] { a, b }, valueMm: statedMm, overrideText: overrideText);

    private static RevitModel Model(params DimensionInfo[] dimensions) =>
        RevitCheckTestBuilders.Model(views: new[] { Elevation() }, dimensions: dimensions);

    /// <summary>
    /// The headline case. Both model points sit at a different depth from
    /// their witness - guaranteed in an elevation, whose plane is outside
    /// the geometry - and they differ from each other in depth too, which
    /// is exactly what made the 3D comparison wrong. Seen in the view
    /// plane they are 1550mm apart, which is what the drawing says.
    /// </summary>
    [Fact]
    public void A_dimension_agreeing_with_the_model_in_the_view_plane_reports_nothing()
    {
        var model = Model(Dimension(
            100,
            Witness(1, P(0, 0, 0), P(0, -200, 0)),
            Witness(2, P(1550, 0, 0), P(1550, -900, 0)),
            statedMm: 1550.0));

        Assert.Empty(DrawnDimensionConsistencyCheck.Run(model, new RuleConfig())
            .Where(i => i.Category == "geometry"));
    }

    /// <summary>
    /// And the same geometry compared in 3D would be 1573mm - a 23mm
    /// error invented purely by the depth difference, which is the shape
    /// of every bad number the probe produced before §28.
    /// </summary>
    [Fact]
    public void Depth_difference_alone_never_produces_a_finding()
    {
        var a = P(0, -200, 0);
        var b = P(1550, -900, 0);
        var in3d = System.Math.Sqrt(System.Math.Pow(b.X - a.X, 2) + System.Math.Pow(b.Y - a.Y, 2));

        Assert.True(in3d > 1570, "the 3D distance really is materially different");
        Assert.Equal(1550.0, InPlaneGeometry.DistanceMm(a, b, P(0, -1, 0)), 3);
    }

    [Fact]
    public void A_dimension_the_model_disagrees_with_is_flagged_at_its_real_magnitude()
    {
        var model = Model(Dimension(
            100,
            Witness(1, P(0, 0, 0), P(0, -200, 0)),
            Witness(2, P(1550, 0, 0), P(1600, -900, 0)),
            statedMm: 1550.0));

        var issue = Assert.Single(DrawnDimensionConsistencyCheck.Run(model, new RuleConfig()),
            i => i.Category == "geometry");

        Assert.Equal("high", issue.Severity);
        Assert.Equal(100, issue.ElementId);
        Assert.Contains("50mm out", issue.Description);
    }

    /// <summary>A typed override is what a reader of the sheet acts on, so it is what gets checked.</summary>
    [Fact]
    public void A_typed_override_is_what_gets_checked()
    {
        var model = Model(Dimension(
            100,
            Witness(1, P(0, 0, 0), P(0, -200, 0)),
            Witness(2, P(1550, 0, 0), P(1550, -900, 0)),
            statedMm: 1550.0,
            overrideText: "1500"));

        var issue = Assert.Single(DrawnDimensionConsistencyCheck.Run(model, new RuleConfig()),
            i => i.Category == "geometry");
        Assert.Contains("states 1500mm", issue.Description);
    }

    /// <summary>
    /// A face turned towards the viewer is the background behind the cut,
    /// not something anyone dimensions to. Ignoring the filter would pick
    /// it here and invent a 500mm error.
    /// </summary>
    [Fact]
    public void A_face_seen_face_on_is_not_treated_as_a_witness()
    {
        var backgroundFace = new WitnessPointInfo
        {
            Point = P(2050, -300, 0),
            SourceElementId = 999,
            FaceNormal = P(0, -1, 0),   // normal along the view direction
        };

        var far = Witness(2, P(1550, 0, 0), P(1550, -900, 0));
        far.NearbyModelPoints.Insert(0, backgroundFace);

        var model = Model(Dimension(100, Witness(1, P(0, 0, 0), P(0, -200, 0)), far, statedMm: 1550.0));

        Assert.Empty(DrawnDimensionConsistencyCheck.Run(model, new RuleConfig())
            .Where(i => i.Category == "geometry"));
    }

    /// <summary>
    /// Both references resolving to one element - 5 of 23 dimensions on a
    /// real view. Nothing here can say which two features are meant, and
    /// picking the pair whose separation matches the stated value would be
    /// fitting to the answer. Reported, never guessed.
    /// </summary>
    [Fact]
    public void A_dimension_between_two_features_of_one_element_is_coverage_not_a_verdict()
    {
        var model = Model(Dimension(
            100,
            Witness(7, P(0, 0, 0), P(0, -200, 0)),
            Witness(7, P(1550, 0, 0), P(1550, -900, 0)),
            statedMm: 1550.0));

        var issues = DrawnDimensionConsistencyCheck.Run(model, new RuleConfig());

        Assert.Empty(issues.Where(i => i.Category == "geometry"));
        Assert.Contains(issues, i =>
            i.Category == "coverage" && i.Description.Contains("two features of a single element"));
    }

    /// <summary>
    /// A capture predating ViewInfo.ViewDirection cannot be measured as
    /// the drawing sees it, and the 3D fallback is the answer that averaged
    /// 21mm of error. Refused and reported rather than answered weakly.
    /// </summary>
    [Fact]
    public void A_view_with_no_direction_is_refused_rather_than_measured_in_3d()
    {
        var view = new ViewInfo { ElementId = 10, Name = "old capture", ViewType = "Elevation" };
        var model = RevitCheckTestBuilders.Model(
            views: new[] { view },
            dimensions: new[]
            {
                Dimension(
                    100,
                    Witness(1, P(0, 0, 0), P(0, -200, 0)),
                    Witness(2, P(1550, 0, 0), P(1600, -900, 0)),
                    statedMm: 1550.0),
            });

        var issues = DrawnDimensionConsistencyCheck.Run(model, new RuleConfig());

        Assert.Empty(issues.Where(i => i.Category == "geometry"));
        Assert.Contains(issues, i => i.Category == "coverage" && i.Description.Contains("no recorded direction"));
    }

    /// <summary>
    /// The null-vs-empty distinction this project has been bitten by most:
    /// no search having run must never look like a clean result.
    /// </summary>
    [Fact]
    public void No_search_having_run_is_reported_rather_than_read_as_clean()
    {
        var reference = new ReferenceInfo { ElementId = 1, LocalPoint = P(0, 0, 0) };
        var model = Model(Dimension(100, reference, reference, statedMm: 1550.0));

        var issue = Assert.Single(DrawnDimensionConsistencyCheck.Run(model, new RuleConfig()));
        Assert.Equal("coverage", issue.Category);
        Assert.Contains("witness-geometry search", issue.Description);
    }

    /// <summary>Only a dimension actually compared counts as investigated, so a triage flag never reconciles on nothing.</summary>
    [Fact]
    public void Only_a_compared_dimension_counts_as_investigated()
    {
        var compared = DrawnDimensionConsistencyCheck.RunWithScope(
            Model(Dimension(
                100,
                Witness(1, P(0, 0, 0), P(0, -200, 0)),
                Witness(2, P(1550, 0, 0), P(1550, -900, 0)),
                statedMm: 1550.0)),
            new RuleConfig());
        Assert.Equal(new[] { 100L }, compared.InvestigatedElementIds);

        var noGeometry = DrawnDimensionConsistencyCheck.RunWithScope(
            Model(Dimension(
                100,
                Witness(1, P(0, 0, 0)),
                Witness(2, P(1550, 0, 0)),
                statedMm: 1550.0)),
            new RuleConfig());
        Assert.Empty(noGeometry.InvestigatedElementIds);
    }
}
