using System.Linq;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Capture;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

public class CaptureSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesAllParameterValueVariants()
    {
        var model = RevitCheckTestBuilders.Model(elements: new[]
        {
            RevitCheckTestBuilders.Element(1, parameters: new Dictionary<string, ParameterValue>
            {
                ["Depth"] = RevitCheckTestBuilders.NumericParam(450.0, "450 mm"),
                ["Owner"] = RevitCheckTestBuilders.StringParam("Roads Authority"),
                ["Count"] = new ParameterValue { StorageType = ParameterStorageType.Integer, IntegerValue = 4, DisplayString = "4" },
                ["HostId"] = new ParameterValue { StorageType = ParameterStorageType.ElementId, ElementIdValue = 12345, DisplayString = "12345" },
                ["Unset"] = new ParameterValue(),
            }),
        });

        var loaded = CaptureSerializer.Loads(CaptureSerializer.Dumps(model));

        Assert.Equal(model.DocTitle, loaded.DocTitle);
        Assert.Single(loaded.Elements);
        var element = loaded.Elements[0];
        Assert.Equal(450.0, element.Parameters["Depth"].NumericValue);
        Assert.True(element.Parameters["Depth"].IsLength);
        Assert.Equal("Roads Authority", element.Parameters["Owner"].RawString);
        Assert.Equal(4, element.Parameters["Count"].IntegerValue);
        Assert.Equal(12345, element.Parameters["HostId"].ElementIdValue);
        Assert.Equal(ParameterStorageType.None, element.Parameters["Unset"].StorageType);
    }

    [Fact]
    public void RoundTrip_PreservesHostElementIdForNestedComponents()
    {
        var model = RevitCheckTestBuilders.Model(elements: new[]
        {
            RevitCheckTestBuilders.Element(1, category: "Structural Connections", familyName: "Fixing_Bracket", keyValue: "BRK-01", hostElementId: 99),
        });

        var loaded = CaptureSerializer.Loads(CaptureSerializer.Dumps(model));

        Assert.Equal(99, loaded.Elements[0].HostElementId);
    }

    [Fact]
    public void RoundTrip_PreservesSheetsViewsAndDimensions()
    {
        var sheet = new SheetInfo { ElementId = 1, SheetNumber = "S101", Name = "Plan", UniqueId = "sheet-guid" };
        var view = RevitCheckTestBuilders.View(10, sheetUniqueId: "sheet-guid", linkedToModelSection: true);
        var dim = RevitCheckTestBuilders.Chain(
            20, 10,
            new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.DraftedRef() },
            new (double?, string?)[] { (450.0, "450"), (600.0, null) },
            typeName: "Linear Dimension Style");

        var model = RevitCheckTestBuilders.Model(sheets: new[] { sheet }, views: new[] { view }, dimensions: new[] { dim });

        var loaded = CaptureSerializer.Loads(CaptureSerializer.Dumps(model));

        Assert.Single(loaded.Sheets);
        Assert.Equal("sheet-guid", loaded.Sheets[0].UniqueId);
        Assert.Single(loaded.Views);
        Assert.True(loaded.Views[0].LinkedToModelSection);
        Assert.Single(loaded.Dimensions);
        var loadedDim = loaded.Dimensions[0];
        Assert.Equal(2, loadedDim.References.Count);
        Assert.Equal("Wall", loadedDim.References[0].ClassName);
        Assert.Equal(2, loadedDim.Segments.Count);
        Assert.True(loadedDim.Segments[0].IsOverridden);
        Assert.False(loadedDim.Segments[1].IsOverridden);
        Assert.Equal(1050.0, loadedDim.ValueMm);
    }

    [Fact]
    public void SchemaVersion_IsWrittenOnEveryDump()
    {
        var json = CaptureSerializer.Dumps(RevitCheckTestBuilders.Model());

        Assert.Contains($"\"schema_version\": {CaptureSerializer.SchemaVersion}", json);
    }

    [Fact]
    public void ANewerCapture_IsRefusedRatherThanMisread()
    {
        var json = CaptureSerializer.Dumps(RevitCheckTestBuilders.Model())
            .Replace($"\"schema_version\": {CaptureSerializer.SchemaVersion}", $"\"schema_version\": {CaptureSerializer.SchemaVersion + 1}");

        Assert.Throws<InvalidOperationException>(() => CaptureSerializer.Loads(json));
    }

    [Fact]
    public void MissingElementsKey_LoadsAsEmptyList_ForwardCompatibility()
    {
        var json = $"{{\"doc_title\": \"OLD\", \"schema_version\": {CaptureSerializer.SchemaVersion}}}";

        var loaded = CaptureSerializer.Loads(json);

        Assert.Equal("OLD", loaded.DocTitle);
        Assert.Empty(loaded.Elements);
        Assert.Empty(loaded.ExtractionErrors);
    }

    [Fact]
    public void FileRoundTrip_SurvivesDisk()
    {
        var model = RevitCheckTestBuilders.Model(elements: new[] { RevitCheckTestBuilders.Element(7) });
        var path = Path.Combine(Path.GetTempPath(), $"revitcheck-test-{Guid.NewGuid()}.capture.json");
        try
        {
            CaptureSerializer.Save(model, path);
            var loaded = CaptureSerializer.Load(path);
            Assert.Equal(7, loaded.Elements[0].ElementId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The point of §25: a capture has to carry element geometry, or a
    /// positional check cannot be replayed off the Revit machine and two
    /// captures cannot be diffed to find what moved. Real captures carried
    /// none - 0 of 36,626 elements - because LocalPoint shared a flag with
    /// the expensive per-element GetProjectPosition call.
    /// </summary>
    [Fact]
    public void Element_positions_survive_a_capture_round_trip()
    {
        var model = RevitCheckTestBuilders.Model(elements: new[]
        {
            RevitCheckTestBuilders.Pile(
                5506399, "PIL234307",
                eastingMm: 278437528.42,
                northingMm: 6130713126.57,
                localPoint: new Point3D { X = 1234.5, Y = 6789.0, Z = 250.25 }),
        });

        var pile = Assert.Single(CaptureSerializer.Loads(CaptureSerializer.Dumps(model)).Elements);

        Assert.Equal(278437528.42, pile.ProjectPositionEastingMm!.Value, 3);
        Assert.Equal(6130713126.57, pile.ProjectPositionNorthingMm!.Value, 3);
        Assert.NotNull(pile.LocalPoint);
        Assert.Equal(1234.5, pile.LocalPoint!.X, 3);
        Assert.Equal(6789.0, pile.LocalPoint.Y, 3);
    }

    /// <summary>
    /// And the check itself must reach the same verdict through a capture
    /// as it does in Revit - which is the whole claim the capture workflow
    /// rests on, never actually tested for a positional check before.
    /// </summary>
    [Fact]
    public void A_positional_check_reaches_the_same_verdict_through_a_capture()
    {
        var pileA = RevitCheckTestBuilders.Pile(1, "P1", 0, 0);
        var pileB = RevitCheckTestBuilders.Pile(2, "P2", 0, 1000);
        var pileC = RevitCheckTestBuilders.Pile(3, "P3", 0, 2000);
        var model = RevitCheckTestBuilders.Model(
            elements: new[] { pileA, pileB, pileC },
            dimensions: new[]
            {
                RevitCheckTestBuilders.PileChainDimension(
                    100, 1,
                    RevitCheckTestBuilders.TagRef(200, RevitCheckTestBuilders.Pt(0, 0)),
                    RevitCheckTestBuilders.TagRef(201, RevitCheckTestBuilders.Pt(0, 1000))),
                RevitCheckTestBuilders.PileChainDimension(
                    101, 1,
                    RevitCheckTestBuilders.TagRef(202, RevitCheckTestBuilders.Pt(0, 1000)),
                    RevitCheckTestBuilders.TagRef(203, RevitCheckTestBuilders.Pt(0, 2000))),
            },
            textNotes: new[]
            {
                // Printed 90 degrees against a chain running due north - a
                // real disagreement, so there is a verdict to compare.
                RevitCheckTestBuilders.TextNote(300, 1, "90\u00b0 00' 00\"", RevitCheckTestBuilders.Pt(50, 500)),
            });

        var config = new RuleConfig();
        var live = PileChainBearingConsistencyCheck.Run(model, config);
        var replayed = PileChainBearingConsistencyCheck.Run(
            CaptureSerializer.Loads(CaptureSerializer.Dumps(model)), config);

        Assert.NotEmpty(live);
        Assert.Equal(
            live.Select(i => (i.RuleId, i.Category, i.Severity, i.ElementId)),
            replayed.Select(i => (i.RuleId, i.Category, i.Severity, i.ElementId)));
    }
}
