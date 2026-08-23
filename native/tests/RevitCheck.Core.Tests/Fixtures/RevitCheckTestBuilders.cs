using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Tests.Fixtures;

/// <summary>
/// Synthetic IR builders for tests, mirroring the role of
/// <c>tests/revit/conftest.py</c>'s factory functions on the Python side -
/// small, composable, defaulted so a test only spells out what it cares
/// about.
/// </summary>
internal static class RevitCheckTestBuilders
{
    internal static ParameterValue StringParam(string value) => new()
    {
        StorageType = ParameterStorageType.String,
        DisplayString = value,
        RawString = value,
    };

    internal static ParameterValue NumericParam(double mm, string? display = null) => new()
    {
        StorageType = ParameterStorageType.Double,
        NumericValue = mm,
        IsLength = true,
        DisplayString = display ?? $"{mm} mm",
    };

    internal static ElementMetadata Element(
        long elementId,
        string? category = "Structural Framing",
        string? familyName = "PC_I_Beam",
        string? typeName = "PC_I_Beam: 900mm",
        string? keyValue = "ASSET-001",
        long? hostElementId = null,
        Dictionary<string, ParameterValue>? parameters = null,
        string? uniqueId = null)
        => new()
        {
            ElementId = elementId,
            UniqueId = uniqueId ?? $"guid-{elementId}",
            Category = category,
            FamilyName = familyName,
            TypeName = typeName,
            KeyValue = keyValue,
            HostElementId = hostElementId,
            Parameters = parameters ?? new Dictionary<string, ParameterValue>(),
        };

    internal static Ir.RevitModel Model(
        IEnumerable<ElementMetadata>? elements = null,
        List<string>? extractionErrors = null,
        List<string>? excludedWorksets = null,
        string docTitle = "TEST-BRIDGE")
        => new()
        {
            DocTitle = docTitle,
            RevitVersion = "2024",
            CapturedAt = "2026-08-23T00:00:00",
            Elements = elements?.ToList() ?? new List<ElementMetadata>(),
            ExtractionErrors = extractionErrors ?? new List<string>(),
            ExcludedWorksets = excludedWorksets ?? new List<string>(),
        };
}
