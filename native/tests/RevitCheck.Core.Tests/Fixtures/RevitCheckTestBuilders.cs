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
        string docTitle = "TEST-BRIDGE",
        IEnumerable<SheetInfo>? sheets = null,
        IEnumerable<ViewInfo>? views = null,
        IEnumerable<DimensionInfo>? dimensions = null)
        => new()
        {
            DocTitle = docTitle,
            RevitVersion = "2024",
            CapturedAt = "2026-08-23T00:00:00",
            Elements = elements?.ToList() ?? new List<ElementMetadata>(),
            ExtractionErrors = extractionErrors ?? new List<string>(),
            ExcludedWorksets = excludedWorksets ?? new List<string>(),
            // Mirrors conftest.py's build_model: sheets defaults to one
            // stand-in sheet when not explicitly given (an explicit empty
            // list stays empty), so dimension/view tests that don't care
            // about sheet content don't each have to supply one.
            Sheets = sheets?.ToList() ?? new List<SheetInfo> { new() { ElementId = 1, SheetNumber = "S101", Name = "Plan" } },
            Views = views?.ToList() ?? new List<ViewInfo>(),
            Dimensions = dimensions?.ToList() ?? new List<DimensionInfo>(),
        };

    // --- ReferenceInfo builders, mirroring conftest.py's model_ref/drafted_ref/datum_ref/unresolved_ref ---

    /// <summary>A reference to real model geometry.</summary>
    internal static ReferenceInfo ModelRef(long elementId = 100, string className = "Wall") => new()
    {
        ElementId = elementId,
        ClassName = className,
        Category = "Structural Framing",
        ViewSpecific = false,
    };

    /// <summary>A reference to view-specific linework - the drift case.</summary>
    internal static ReferenceInfo DraftedRef(long elementId = 200, string className = "DetailLine") => new()
    {
        ElementId = elementId,
        ClassName = className,
        Category = "Lines",
        ViewSpecific = true,
    };

    internal static ReferenceInfo DatumRef(long elementId = 300, string className = "Grid") => new()
    {
        ElementId = elementId,
        ClassName = className,
        Category = "Grids",
        ViewSpecific = false,
    };

    internal static ReferenceInfo UnresolvedRef(long elementId = 400) => new()
    {
        ElementId = elementId,
        Resolved = false,
    };

    // --- DimensionInfo builders, mirroring conftest.py's dimension/chain ---

    internal static DimensionInfo Dimension(
        long elementId,
        long viewId,
        IEnumerable<ReferenceInfo> references,
        double? valueMm = 1000.0,
        string? overrideText = null,
        bool spot = false,
        string? typeName = null,
        string? uniqueId = null)
        => new()
        {
            ElementId = elementId,
            ViewId = viewId,
            IsSpot = spot,
            References = references.ToList(),
            Segments = new List<DimensionSegmentInfo> { new() { ValueMm = valueMm, ValueOverride = overrideText } },
            TypeName = typeName,
            UniqueId = uniqueId,
        };

    /// <summary>A dimension chain: one Revit element carrying many segments. `segments` is a list of (valueMm, overrideText) pairs.</summary>
    internal static DimensionInfo Chain(
        long elementId,
        long viewId,
        IEnumerable<ReferenceInfo> references,
        IEnumerable<(double? ValueMm, string? OverrideText)> segments,
        string? typeName = null)
        => new()
        {
            ElementId = elementId,
            ViewId = viewId,
            References = references.ToList(),
            Segments = segments.Select(s => new DimensionSegmentInfo { ValueMm = s.ValueMm, ValueOverride = s.OverrideText }).ToList(),
            TypeName = typeName,
        };

    // --- ViewInfo builder, mirroring conftest.py's view ---

    internal static ViewInfo View(
        long elementId,
        string name = "SECTION A-A",
        string viewType = "Section",
        string? sheetNo = "S101",
        long? sheetId = null,
        bool isTemplate = false,
        string? sheetUniqueId = null,
        bool linkedToModelSection = false,
        string? uniqueId = null,
        string? worksetName = null)
        => new()
        {
            ElementId = elementId,
            Name = name,
            ViewType = viewType,
            SheetNo = sheetNo,
            SheetId = sheetId ?? (sheetNo is not null ? 1 : null),
            IsTemplate = isTemplate,
            SheetUniqueId = sheetUniqueId,
            LinkedToModelSection = linkedToModelSection,
            UniqueId = uniqueId,
            WorksetName = worksetName,
        };
}
