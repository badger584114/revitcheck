using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Tests.Fixtures;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>Port of test_dimension_provenance.py's TestClassifyReference (8 tests).</summary>
public class ClassifyReferenceTests
{
    [Fact]
    public void ModelGeometry() =>
        Assert.Equal(Provenance.Model, DimensionClassification.ClassifyReference(RevitCheckTestBuilders.ModelRef()));

    [Fact]
    public void ViewSpecificIsDrafted() =>
        Assert.Equal(Provenance.Drafted, DimensionClassification.ClassifyReference(RevitCheckTestBuilders.DraftedRef()));

    [Fact]
    public void GridIsADatumNotARisk() =>
        // Dimensioning to a grid is good practice: move the grid and the
        // dimension follows. It must not be lumped in with linework.
        Assert.Equal(Provenance.Datum, DimensionClassification.ClassifyReference(RevitCheckTestBuilders.DatumRef()));

    [Theory]
    [InlineData("Level")]
    [InlineData("ReferencePlane")]
    [InlineData("MultiSegmentGrid")]
    public void LevelAndReferencePlaneAreDatums(string className)
    {
        var reference = new ReferenceInfo { ElementId = 5, ClassName = className, ViewSpecific = false };
        Assert.Equal(Provenance.Datum, DimensionClassification.ClassifyReference(reference));
    }

    [Fact]
    public void ImportedCadIsDraftedDespiteNotBeingViewSpecific()
    {
        // A DWG imported into the model isn't view-specific, so the
        // ViewSpecific test alone would call it model geometry. It's a
        // static snapshot of someone else's file - same failure mode as
        // detail linework, different mechanism.
        var reference = new ReferenceInfo { ElementId = 7, ClassName = "ImportInstance", ViewSpecific = false };
        Assert.Equal(Provenance.Drafted, DimensionClassification.ClassifyReference(reference));
    }

    [Fact]
    public void UnresolvedIsUnknownNotAssumedClean() =>
        Assert.Equal(Provenance.Unknown, DimensionClassification.ClassifyReference(RevitCheckTestBuilders.UnresolvedRef()));

    [Fact]
    public void InvalidElementIdIsUnknown() =>
        Assert.Equal(Provenance.Unknown, DimensionClassification.ClassifyReference(new ReferenceInfo { ElementId = -1 }));

    [Fact]
    public void ViewSpecificBeatsClassName()
    {
        // Order matters: Revit's own ViewSpecific flag is the invariant, so
        // it wins even when the class name looks like model geometry.
        var reference = new ReferenceInfo { ElementId = 9, ClassName = "Wall", ViewSpecific = true };
        Assert.Equal(Provenance.Drafted, DimensionClassification.ClassifyReference(reference));
    }
}

/// <summary>Port of test_dimension_provenance.py's TestClassifyDimension (9 tests).</summary>
public class ClassifyDimensionTests
{
    [Fact]
    public void AllModel()
    {
        var dim = RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.ModelRef(101) });
        Assert.Equal(Provenance.Model, DimensionClassification.ClassifyDimension(dim));
    }

    [Fact]
    public void AllDrafted()
    {
        var dim = RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.DraftedRef(), RevitCheckTestBuilders.DraftedRef(201) });
        Assert.Equal(Provenance.Drafted, DimensionClassification.ClassifyDimension(dim));
    }

    [Fact]
    public void DatumOnly()
    {
        var dim = RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.DatumRef(), RevitCheckTestBuilders.DatumRef(301) });
        Assert.Equal(Provenance.Datum, DimensionClassification.ClassifyDimension(dim));
    }

    [Fact]
    public void ModelPlusDatumIsLive()
    {
        var dim = RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.DatumRef() });
        Assert.Equal(Provenance.Model, DimensionClassification.ClassifyDimension(dim));
    }

    [Fact]
    public void ModelPlusDraftedIsMixed()
    {
        var dim = RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.ModelRef(), RevitCheckTestBuilders.DraftedRef() });
        Assert.Equal(Provenance.Mixed, DimensionClassification.ClassifyDimension(dim));
    }

    [Fact]
    public void DatumPlusDraftedIsAlsoMixed()
    {
        var dim = RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.DatumRef(), RevitCheckTestBuilders.DraftedRef() });
        Assert.Equal(Provenance.Mixed, DimensionClassification.ClassifyDimension(dim));
    }

    [Fact]
    public void UnknownDoesNotMaskADraftedReference()
    {
        // A dimension half of whose references failed to resolve is still
        // drafted if the resolvable half is linework - an extraction gap
        // must not launder a real finding.
        var dim = RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.UnresolvedRef(), RevitCheckTestBuilders.DraftedRef() });
        Assert.Equal(Provenance.Drafted, DimensionClassification.ClassifyDimension(dim));
    }

    [Fact]
    public void AllUnknown()
    {
        var dim = RevitCheckTestBuilders.Dimension(1, 10, new[] { RevitCheckTestBuilders.UnresolvedRef() });
        Assert.Equal(Provenance.Unknown, DimensionClassification.ClassifyDimension(dim));
    }

    [Fact]
    public void NoReferencesAtAll()
    {
        var dim = RevitCheckTestBuilders.Dimension(1, 10, Array.Empty<ReferenceInfo>());
        Assert.Equal(Provenance.Unknown, DimensionClassification.ClassifyDimension(dim));
    }
}
