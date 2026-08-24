using RevitCheck.Core.Mapping;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// Loads the two real, committed starter mapping files
/// (samples/metadata database/*.mapping.json) - same reasoning as
/// RealCaptureParityTests: these are real files a real command loads on a
/// real Revit machine, so "does ParameterMappingSerializer.Load actually
/// accept them" deserves a permanent regression test, not a one-off manual
/// check. Added 2026-08-24 alongside ScopeViewName, after a real Revit-
/// machine run showed the category-only scope swept far more of the model
/// than intended - see ParameterMapping.ScopeViewName's own docstring.
/// </summary>
public class RealMappingFilesTests
{
    private static string SamplesDir()
    {
        // native/tests/RevitCheck.Core.Tests/bin/Debug/net8.0 -> repo root
        var dir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "samples", "metadata database");
    }

    [Theory]
    [InlineData("Asset Classification.mapping.json", "ATM_Asset_Identifier")]
    [InlineData("Location Referencing.mapping.json", "DIT_LocationHierarchyCode")]
    public void RealMappingFileLoadsAndScopesToTheCuratedView(string fileName, string expectedKeyParameter)
    {
        var path = Path.Combine(SamplesDir(), fileName);
        var mapping = ParameterMappingSerializer.Load(path);

        Assert.Equal(expectedKeyParameter, mapping.KeyParameterName);
        Assert.Equal("NavisworksExport", mapping.ScopeViewName);
        Assert.NotEmpty(mapping.Fields);
    }
}
