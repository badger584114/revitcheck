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
        string? uniqueId = null,
        double? projectPositionEastingMm = null,
        double? projectPositionNorthingMm = null,
        Point3D? localPoint = null)
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
            ProjectPositionEastingMm = projectPositionEastingMm,
            ProjectPositionNorthingMm = projectPositionNorthingMm,
            LocalPoint = localPoint,
        };

    internal static Ir.RevitModel Model(
        IEnumerable<ElementMetadata>? elements = null,
        List<string>? extractionErrors = null,
        List<string>? excludedWorksets = null,
        string docTitle = "TEST-BRIDGE",
        IEnumerable<SheetInfo>? sheets = null,
        IEnumerable<ViewInfo>? views = null,
        IEnumerable<DimensionInfo>? dimensions = null,
        IEnumerable<ScheduleInfo>? schedules = null,
        IEnumerable<TextNoteInfo>? textNotes = null)
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
            Schedules = schedules?.ToList() ?? new List<ScheduleInfo>(),
            TextNotes = textNotes?.ToList() ?? new List<TextNoteInfo>(),
        };

    // --- Pile / schedule builders, for PileModelScheduleConsistencyCheckTests ---

    /// <summary>
    /// A pile element with the real 2026-08-26 shape: DIT_SiteID as the
    /// join key, and a LIVE position (ElementMetadata.ProjectPositionEastingMm/
    /// NorthingMm) - the check's only real comparison input, per the
    /// 2026-08-26 correction (see RuleConfig's remarks: the Dynamo-written
    /// XYZ_Easting/XYZ_Northing parameters must never be read as this side
    /// of the comparison). `frozenXyzEastingMm`/`frozenXyzNorthingMm` are
    /// optional and, when given, populate those parameters anyway - purely
    /// so a test can demonstrate the real failure mode directly: a pile
    /// whose live position has moved while its Dynamo-written parameters
    /// (and therefore the schedule, which reads the same stale value)
    /// stayed frozen at the old position.
    /// </summary>
    internal static ElementMetadata Pile(
        long elementId,
        string siteId,
        double eastingMm,
        double northingMm,
        string category = "Structural Foundations",
        double? frozenXyzEastingMm = null,
        double? frozenXyzNorthingMm = null,
        Point3D? localPoint = null)
    {
        var parameters = new Dictionary<string, ParameterValue> { ["DIT_SiteID"] = StringParam(siteId) };
        if (frozenXyzEastingMm is { } fe)
        {
            parameters["XYZ_Easting"] = NumericParam(fe);
        }

        if (frozenXyzNorthingMm is { } fn)
        {
            parameters["XYZ_Northing"] = NumericParam(fn);
        }

        return Element(
            elementId,
            category: category,
            familyName: "01_SFO_FRP_Pile_CastInPlace_CS_BR08",
            typeName: "CAST-IN-PLACE PILE - 1200",
            keyValue: null,
            parameters: parameters,
            projectPositionEastingMm: eastingMm,
            projectPositionNorthingMm: northingMm,
            // Defaults to the same numbers as the project-position pair -
            // fine for tests that don't care about the local-vs-survey
            // distinction (see ElementMetadata.LocalPoint's own remarks);
            // pass localPoint explicitly for pile-chain reconstruction
            // tests, which do care.
            localPoint: localPoint ?? new Point3D { X = eastingMm, Y = northingMm, Z = 0 });
    }

    // --- Pile-chain / bearing builders, for PileChainBearingConsistencyCheckTests ---

    internal static Point3D Pt(double x, double y, double z = 0) => new() { X = x, Y = y, Z = z };

    /// <summary>A dimension reference to an AnnotationSymbol tag at a given local point - the real shape confirmed 2026-08-26 for this project's pile-mark tags.</summary>
    internal static ReferenceInfo TagRef(long elementId, Point3D localPoint) => new()
    {
        ElementId = elementId,
        ClassName = "AnnotationSymbol",
        Category = "Generic Annotations",
        ViewSpecific = true,
        LocalPoint = localPoint,
    };

    /// <summary>A tag-to-tag dimension between two AnnotationSymbol references, real shape - PileChainReconstruction only ever looks at References/ElementId, not Segments, but a value is still supplied for tests that also check DimensionProvenance-style behaviour incidentally.</summary>
    internal static DimensionInfo PileChainDimension(
        long elementId, long viewId, ReferenceInfo refA, ReferenceInfo refB, double? valueMm = null) => new()
        {
            ElementId = elementId,
            ViewId = viewId,
            References = new List<ReferenceInfo> { refA, refB },
            Segments = new List<DimensionSegmentInfo> { new() { ValueMm = valueMm } },
            TypeName = "Dimension_Standard_O (mm)",
        };

    internal static TextNoteInfo TextNote(long elementId, long viewId, string rawText, Point3D localPoint) => new()
    {
        ElementId = elementId,
        ViewId = viewId,
        RawText = rawText,
        LocalPoint = localPoint,
    };

    /// <summary>A pile schedule with the real 2026-08-26 column shape: SITE ID / EASTING (m) / NORTHING (m), values as printed metres text.</summary>
    internal static ScheduleInfo PileSchedule(
        string name,
        IEnumerable<(string SiteId, string EastingM, string NorthingM)> rows)
        => new()
        {
            Name = name,
            Headers = new List<string> { "SITE ID", "LOCATION", "EASTING (m)", "NORTHING (m)" },
            Rows = rows
                .Select(r => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                {
                    ["SITE ID"] = r.SiteId,
                    ["EASTING (m)"] = r.EastingM,
                    ["NORTHING (m)"] = r.NorthingM,
                })
                .ToList(),
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
