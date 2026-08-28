using Autodesk.Revit.DB;
using RevitCheck.Core.Ir;
// Both Autodesk.Revit.DB and RevitCheck.Core.Ir declare a ParameterValue
// type - the Revit API's own is never used by name in this file, but the
// `using RevitCheck.Core.Ir` above still makes the reference ambiguous
// without this alias.
using ParameterValue = RevitCheck.Core.Ir.ParameterValue;

namespace RevitCheck.Addin.Adapters;

/// <summary>
/// The metadata-reconciliation adapter - reads category/family/parameter
/// facts off a live <see cref="Document"/>. Extracts facts and judges
/// nothing, matching the layering rule the whole IR follows (see
/// <see cref="ElementMetadata"/>'s own remarks): no classification, no
/// field-mapping awareness, no tolerance logic. Mirrors <c>capture.py</c>'s
/// role for the Python engine, scoped to just the metadata half of the IR.
/// </summary>
/// <remarks>
/// Category scope and the mm conversion factor below are the two decisions
/// confirmed from real data via <c>diagnostics/InspectElements.pushbutton</c>
/// (2026-08-23, see native/README.md's "Open questions" section this
/// closed): Floors / Generic Models / Structural Connections / Structural
/// Foundations / Structural Framing carry the tracked parameters on this
/// client's models. That export also confirmed how a nested sub-component
/// (e.g. a fixing bracket nested in a panel) actually appears -
/// <see cref="FamilyInstance.GetSubComponentIds"/>, each with its own
/// <c>ElementId</c>/<c>UniqueId</c> and independently-editable parameters -
/// so each is captured as its own <see cref="ElementMetadata"/>, walked
/// automatically from whatever the collector sweep finds, exactly as the
/// diagnostic did from a user's selection.
/// </remarks>
public static class RevitMetadataElementSource
{
    // Revit's internal length unit is always decimal feet regardless of
    // project display settings - multiplying by this constant is the same
    // choice ir.py's _mm() and CLAUDE.md's adapter note make, deliberately
    // instead of a UnitUtils/ForgeTypeId conversion call, so this file never
    // has to branch on a Revit-version-specific unit API.
    private const double MmPerFoot = 304.8;

    /// <summary>
    /// Categories confirmed to carry tracked parameters (see remarks above).
    /// Passed explicitly rather than assumed universal - a different
    /// client's mapping may need a different scope, so this is a documented
    /// default, not a silent hardcoded constant a future project is stuck
    /// with.
    /// </summary>
    public static readonly IReadOnlyList<BuiltInCategory> DefaultCategories = new[]
    {
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_StructConnections,
        BuiltInCategory.OST_StructuralFoundation,
        BuiltInCategory.OST_StructuralFraming,
    };

    /// <summary>
    /// Sweeps <paramref name="doc"/> for the tracked metadata elements.
    /// </summary>
    /// <param name="viewName">
    /// When set, scopes the sweep to elements visible in the named view
    /// (<see cref="FilteredElementCollector(Document, ElementId)"/>) instead
    /// of the whole document - see <see cref="ParameterMapping.ScopeViewName"/>
    /// for why this exists: category alone matched far more of a real
    /// project than the intended trackable set. Throws
    /// <see cref="InvalidOperationException"/> if no view with this exact
    /// name exists - deliberately not a silent fall-back to a whole-document
    /// sweep, which is exactly the too-broad behaviour this parameter exists
    /// to avoid.
    /// </param>
    /// <param name="populateLivePosition">
    /// When true, also computes each collected element's own
    /// <see cref="ElementMetadata.LocalPoint"/> (its <c>Location.Point</c>,
    /// mm, no survey transform) and <see cref="ElementMetadata.ProjectPositionEastingMm"/>/
    /// <see cref="ElementMetadata.ProjectPositionNorthingMm"/>/
    /// <see cref="ElementMetadata.ProjectPositionElevationMm"/> (a live
    /// <c>ProjectLocation.GetProjectPosition</c> call per element). Off by
    /// default - this is real per-element API cost
    /// (<c>InspectPileSetout.pushbutton</c>'s diagnostic confirmed the call
    /// itself works and gives real coordinates, PLANNING.md §14, but every
    /// existing caller of this method today is metadata reconciliation,
    /// which never needs a position at all) - on for the two pile commands
    /// (<c>PileModelScheduleConsistencyCommand</c>/
    /// <c>PileChainBearingConsistencyCommand</c>), the only callers that do.
    /// A failure computing either value for one element is soft - left null,
    /// same local try/catch discipline <see cref="ReadValue"/> already uses
    /// for a parameter that won't read - not an <see cref="MetadataCollectionResult.ExtractionErrors"/>
    /// entry, since the rest of that element's metadata is still genuinely
    /// useful on its own.
    /// </param>
    public static MetadataCollectionResult Collect(
        Document doc,
        string? viewName = null,
        IEnumerable<BuiltInCategory>? categories = null,
        bool populateLivePosition = false)
    {
        var scope = categories?.ToList() ?? DefaultCategories.ToList();
        var elements = new List<ElementMetadata>();
        var errors = new List<string>();
        var seen = new HashSet<long>();

        var categoryFilter = new ElementMulticategoryFilter(scope);
        FilteredElementCollector collector;
        if (string.IsNullOrWhiteSpace(viewName))
        {
            collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(categoryFilter);
        }
        else
        {
            var view = ResolveView(doc, viewName!);
            collector = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .WherePasses(categoryFilter);
        }

        var queue = new Queue<(Element Element, long? HostId)>();
        foreach (var element in collector)
        {
            queue.Enqueue((element, null));
        }

        while (queue.Count > 0)
        {
            var (element, hostId) = queue.Dequeue();
            var id = ElementIdValue(element.Id);
            if (!seen.Add(id))
            {
                continue;
            }

            // One bad element must not abort the whole sweep - isolated
            // per-element, same discipline capture.py's own extraction
            // loop uses (see RevitModel.ExtractionErrors / CaptureCoverageCheck).
            try
            {
                elements.Add(Describe(doc, element, hostId, populateLivePosition));
            }
            catch (Exception ex)
            {
                errors.Add($"element {id}: {ex.Message}");
                continue;
            }

            if (element is FamilyInstance familyInstance)
            {
                ICollection<ElementId> subIds;
                try
                {
                    subIds = familyInstance.GetSubComponentIds();
                }
                catch (Exception ex)
                {
                    errors.Add($"element {id}: sub-components: {ex.Message}");
                    continue;
                }

                foreach (var subId in subIds)
                {
                    var subElement = doc.GetElement(subId);
                    if (subElement is not null)
                    {
                        queue.Enqueue((subElement, id));
                    }
                }
            }
        }

        return new MetadataCollectionResult { Elements = elements, ExtractionErrors = errors };
    }

    private static ElementMetadata Describe(Document doc, Element element, long? hostId, bool populateLivePosition)
    {
        string? category = null;
        long? builtInCategory = null;
        if (element.Category is { } cat)
        {
            category = cat.Name;
            builtInCategory = ElementIdValue(cat.Id);
        }

        string? familyName = null;
        string? typeName = null;
        if (element is FamilyInstance familyInstance)
        {
            familyName = familyInstance.Symbol?.Family?.Name;
            typeName = familyInstance.Symbol?.Name;
        }

        var parameters = ReadParameters(element.Parameters);

        // Type parameters fill in only what the instance doesn't already
        // carry - an instance-level value always wins. A mapping field
        // resolves to a single canonical parameter name regardless of which
        // level it actually lives on, so both are folded into one bag here
        // rather than kept separate (the diagnostic kept them apart because
        // it existed to answer "which level", not to feed a check).
        var typeId = element.GetTypeId();
        if (typeId is not null && typeId != ElementId.InvalidElementId)
        {
            var typeElement = doc.GetElement(typeId);
            if (typeElement is not null)
            {
                foreach (var kv in ReadParameters(typeElement.Parameters))
                {
                    if (!parameters.ContainsKey(kv.Key))
                    {
                        parameters[kv.Key] = kv.Value;
                    }
                }
            }
        }

        Point3D? localPoint = null;
        double? eastingMm = null;
        double? northingMm = null;
        double? elevationMm = null;
        if (populateLivePosition && element.Location is LocationPoint locationPoint)
        {
            var rawPoint = locationPoint.Point;
            localPoint = PointOf(rawPoint);

            // A live GetProjectPosition call per element - real API cost,
            // only paid when a caller actually asked for it (see Collect's
            // own remarks on populateLivePosition). Soft-fail like the
            // parameter reads above: this element's other facts are still
            // worth keeping even if its survey-adjusted position can't be
            // computed (e.g. no configured Survey Point).
            try
            {
                var position = doc.ActiveProjectLocation.GetProjectPosition(rawPoint);
                eastingMm = position.EastWest * MmPerFoot;
                northingMm = position.NorthSouth * MmPerFoot;
                elevationMm = position.Elevation * MmPerFoot;
            }
            catch
            {
                // Left null - see the param doc on Collect.
            }
        }

        return new ElementMetadata
        {
            ElementId = ElementIdValue(element.Id),
            UniqueId = element.UniqueId,
            Category = category,
            BuiltInCategory = builtInCategory,
            FamilyName = familyName,
            TypeName = typeName,
            HostElementId = hostId,
            Parameters = parameters,
            LocalPoint = localPoint,
            ProjectPositionEastingMm = eastingMm,
            ProjectPositionNorthingMm = northingMm,
            ProjectPositionElevationMm = elevationMm,
        };
    }

    private static Point3D PointOf(XYZ xyz) =>
        new() { X = xyz.X * MmPerFoot, Y = xyz.Y * MmPerFoot, Z = xyz.Z * MmPerFoot };

    private static Dictionary<string, ParameterValue> ReadParameters(ParameterSet parameterSet)
    {
        var result = new Dictionary<string, ParameterValue>();
        foreach (Parameter parameter in parameterSet)
        {
            var name = parameter.Definition?.Name;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            // Last one wins on a duplicate definition name, same as the
            // Python diagnostic's dict build - not expected on a real
            // element, not worth failing over. (IsNullOrEmpty doesn't
            // narrow `name` on this compiler's nullable-analysis surface,
            // same situation MetadataReconciliationCheck.cs already notes.)
            result[name!] = ReadValue(parameter);
        }

        return result;
    }

    private static ParameterValue ReadValue(Parameter parameter)
    {
        string? displayString;
        try
        {
            displayString = parameter.AsValueString();
        }
        catch
        {
            // A handful of parameter states throw on AsValueString() (e.g.
            // an unset parameter on some types) - the raw value below is
            // still worth capturing, so this is a soft failure, not one
            // that aborts the element.
            displayString = null;
        }

        var isLength = IsLengthParameter(parameter.Definition);

        switch (parameter.StorageType)
        {
            case StorageType.String:
                return new ParameterValue
                {
                    StorageType = ParameterStorageType.String,
                    DisplayString = displayString,
                    RawString = parameter.AsString(),
                };

            case StorageType.Double:
                var raw = parameter.AsDouble();
                return new ParameterValue
                {
                    StorageType = ParameterStorageType.Double,
                    DisplayString = displayString,
                    NumericValue = isLength ? raw * MmPerFoot : raw,
                    IsLength = isLength,
                };

            case StorageType.Integer:
                return new ParameterValue
                {
                    StorageType = ParameterStorageType.Integer,
                    DisplayString = displayString,
                    IntegerValue = parameter.AsInteger(),
                };

            case StorageType.ElementId:
                var idValue = parameter.AsElementId();
                return new ParameterValue
                {
                    StorageType = ParameterStorageType.ElementId,
                    DisplayString = displayString,
                    ElementIdValue = idValue is null || idValue == ElementId.InvalidElementId
                        ? null
                        : ElementIdValue(idValue),
                };

            default:
                return new ParameterValue { StorageType = ParameterStorageType.None, DisplayString = displayString };
        }
    }

    private static bool IsLengthParameter(Definition? definition)
    {
        if (definition is null)
        {
            return false;
        }

        try
        {
            return definition.GetDataType() == SpecTypeId.Length;
        }
        catch
        {
            // Some built-in definitions don't support GetDataType() in
            // every Revit build - not a length if we can't tell.
            return false;
        }
    }

    private static long ElementIdValue(ElementId id) => id.Value;

    private static View ResolveView(Document doc, string viewName)
    {
        var view = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.Ordinal));

        if (view is null)
        {
            throw new InvalidOperationException(
                $"No view named '{viewName}' was found in this document (ParameterMapping.ScopeViewName). " +
                "Check the exact view name, and that it isn't a view template (which has no elements of its own).");
        }

        return view;
    }
}

/// <summary>
/// Elements captured, plus per-element extraction failures isolated rather
/// than raised - same shape as <c>RevitModel.ExtractionErrors</c>, which
/// this feeds directly.
/// </summary>
public sealed class MetadataCollectionResult
{
    public required List<ElementMetadata> Elements { get; init; }
    public required List<string> ExtractionErrors { get; init; }
}
