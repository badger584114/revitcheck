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
}
