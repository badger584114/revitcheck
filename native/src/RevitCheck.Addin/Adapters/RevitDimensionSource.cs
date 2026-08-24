using Autodesk.Revit.DB;

namespace RevitCheck.Addin.Adapters;

/// <summary>
/// The dimension/sheet/view adapter - reads a live <see cref="Document"/>
/// into the shape <c>DimensionProvenanceCheck</c>/
/// <c>DimensionOverrideConsistencyCheck</c> consume. A line-for-line port of
/// <c>extensions/RevitCheck.extension/lib/revitcheck/adapters/revit_source.py</c>'s
/// <c>_collect_dimensions</c>/<c>_collect_sheets_and_views</c>/<c>read_model</c>
/// - that module is the spec this matches, including its comments' reasoning,
/// not just its behaviour. Extracts facts and judges nothing, same rule
/// <see cref="RevitMetadataElementSource"/> follows: no classification, no
/// tolerances, no filtering by view type - that all lives in
/// <c>Core/Checks/</c>, tunable and testable off a Revit machine against a
/// capture.
/// </summary>
/// <remarks>
/// Two version/behaviour notes carried over unchanged from the Python
/// original and from <see cref="RevitMetadataElementSource"/>:
///
/// - Revit's internal length unit is always decimal feet regardless of
///   project display settings, so mm conversion is <c>* 304.8</c> with no
///   <c>UnitUtils</c> call and no <c>UnitTypeId</c>/<c>DisplayUnitType</c>
///   version branching.
/// - This project targets Revit 2024 specifically, so <c>ElementId.Value</c>
///   (Int64) is used directly - no dual-attribute duck-typing for the
///   deprecated <c>IntegerValue</c> the Python side needs to run across
///   versions.
///
/// One correctness fix this port exists to carry forward, not just avoid
/// regressing: dimensions are collected **per view**
/// (<see cref="FilteredElementCollector(Document, ElementId)"/>), never via a
/// single document-wide sweep trusting <c>Dimension.OwnerViewId</c>. Found
/// 2026-08-22 against a real capture: <c>OwnerViewId</c> had attributed 430
/// dimensions to one Elevation view a reviewer counted at roughly a dozen,
/// confirmed wrong via Select-by-ID (a view-specific element can only be
/// selected while its real owning view is active). <c>ViewId</c> below always
/// comes from the loop variable, never read back off the element.
///
/// <c>IncludeWorksets</c> scoping is carried in the method signature for
/// parity with the Python original, but **no caller passes it yet** -
/// neither this command's callers nor <c>RevitMetadataElementSource</c> expose
/// a workset picker today. This is an explicitly deferred gap, not a silently
/// dropped one: v1 reads every workset, <see cref="DimensionCollectionResult.ExcludedWorksets"/>
/// stays empty.
/// </remarks>
public static class RevitDimensionSource
{
    private const double MmPerFoot = 304.8;

    /// <summary>
    /// Reads every sheet, view and dimension out of <paramref name="doc"/>.
    /// Read-only - no transaction is opened, and none should be; a check
    /// that silently edited the model while reporting on it is exactly the
    /// kind of black box CLAUDE.md rules out.
    /// </summary>
    /// <param name="sheetedViewsOnly">
    /// Skip dimension collection for any view not placed on a sheet
    /// (default true). Confirmed by the user as the right default for this
    /// project: a heavy template leaves thousands of premade, never-placed
    /// views in the document, and a <see cref="FilteredElementCollector"/>
    /// call per view is real cost multiplied by however many views there
    /// are. An unplaced view is never issued to anyone either way (the
    /// checks' own default), so this is the adapter doing the same
    /// narrowing the checks already do, for the same reason
    /// <paramref name="includeWorksets"/> does - avoiding the read, not just
    /// filtering the result afterwards.
    /// </param>
    /// <param name="includeWorksets">
    /// When set, a dimension whose workset resolves and isn't in this set is
    /// skipped before its references are read at all. See the class remarks
    /// - nothing populates this yet. Sheets and views are never filtered by
    /// workset (see <see cref="CollectSheetsAndViews"/>'s remarks).
    /// </param>
    public static DimensionCollectionResult Collect(
        Document doc,
        bool sheetedViewsOnly = true,
        ISet<string>? includeWorksets = null)
    {
        var errors = new List<string>();
        var (sheets, views) = CollectSheetsAndViews(doc, errors);
        var dimensions = CollectDimensions(doc, errors, views, includeWorksets, sheetedViewsOnly);

        var excludedWorksets = new List<string>();
        if (includeWorksets is not null)
        {
            excludedWorksets = ListWorksets(doc)
                .Where(w => !includeWorksets.Contains(w.Name))
                .Select(w => w.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }

        return new DimensionCollectionResult
        {
            Sheets = sheets,
            Views = views,
            Dimensions = dimensions,
            ExtractionErrors = errors,
            ExcludedWorksets = excludedWorksets,
        };
    }

    /// <summary>
    /// User-created worksets in <paramref name="doc"/>, as (id, name) pairs.
    /// Empty on a model that isn't workshared. Exists for parity with the
    /// Python original's <c>list_worksets</c> (a future workset-picker UI's
    /// data source) - nothing calls this yet beyond the excluded-worksets
    /// computation above, which only ever runs with a real
    /// <paramref name="doc"/> and an <c>includeWorksets</c> set no caller
    /// provides today.
    /// </summary>
    public static List<(long WorksetId, string Name)> ListWorksets(Document doc)
    {
        if (!doc.IsWorkshared)
        {
            return new List<(long, string)>();
        }

        var result = new List<(long, string)>();
        try
        {
            foreach (Workset workset in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                result.Add((workset.Id.IntegerValue, workset.Name));
            }
        }
        catch
        {
            // No worksets beats a broken capture - same "fail open" choice
            // the Python original makes.
            return new List<(long, string)>();
        }

        return result;
    }

    /// <summary>
    /// Every sheet and every view - neither filtered by workset. Both are
    /// index/container entities, not volume: a sheet just names a page, and
    /// a view just names a scope dimensions live inside. Only the
    /// dimensions themselves (<see cref="CollectDimensions"/>) are filtered
    /// by their own workset.
    /// </summary>
    /// <remarks>
    /// A view is never skipped for its own workset either, even though its
    /// <see cref="ViewInfo.WorksetName"/> is still recorded, purely
    /// informational. Found the hard way, 2026-08-22: a real project keeps
    /// every view on one administrative workset, so filtering views the
    /// same way dimensions are filtered meant deselecting that one workset
    /// in a future picker would silently produce a capture with zero views
    /// and therefore zero dimensions.
    /// </remarks>
    private static (List<Core.Ir.SheetInfo> Sheets, List<Core.Ir.ViewInfo> Views) CollectSheetsAndViews(
        Document doc, List<string> errors)
    {
        var sheets = new List<Core.Ir.SheetInfo>();
        var sheetById = new Dictionary<long, Core.Ir.SheetInfo>();

        foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)))
        {
            try
            {
                var sheetId = sheet.Id.Value;
                var info = new Core.Ir.SheetInfo
                {
                    ElementId = sheetId,
                    SheetNumber = sheet.SheetNumber,
                    Name = sheet.Name,
                    UniqueId = TextOrNone(sheet.UniqueId),
                };
                sheets.Add(info);
                sheetById[sheetId] = info;
            }
            catch (Exception ex)
            {
                errors.Add($"sheet {sheet.Id.Value}: {ex.Message}");
            }
        }

        // A view knows nothing about the sheet it sits on; the Viewport
        // linking them is a separate element. Build the map first so each
        // view can be tagged in one pass.
        var viewToSheet = new Dictionary<long, long>();
        foreach (Viewport viewport in new FilteredElementCollector(doc).OfClass(typeof(Viewport)))
        {
            try
            {
                viewToSheet[viewport.ViewId.Value] = viewport.SheetId.Value;
            }
            catch (Exception ex)
            {
                errors.Add($"viewport {viewport.Id.Value}: {ex.Message}");
            }
        }

        var referencedDraftingViews = ReferencedDraftingViewIds(doc, errors);

        var views = new List<Core.Ir.ViewInfo>();
        foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)))
        {
            try
            {
                var viewId = view.Id.Value;
                var worksetName = WorksetName(doc, view);
                var hasSheet = viewToSheet.TryGetValue(viewId, out var sheetId);
                Core.Ir.SheetInfo? sheet = null;
                if (hasSheet)
                {
                    sheetById.TryGetValue(sheetId, out sheet);
                }

                views.Add(new Core.Ir.ViewInfo
                {
                    ElementId = viewId,
                    Name = view.Name,
                    ViewType = view.ViewType.ToString(),
                    IsTemplate = view.IsTemplate,
                    Scale = view.Scale == 0 ? null : view.Scale,
                    SheetId = hasSheet ? sheetId : null,
                    SheetNo = sheet?.SheetNumber,
                    SheetUniqueId = sheet?.UniqueId,
                    WorksetName = worksetName,
                    LinkedToModelSection = referencedDraftingViews.Contains(viewId),
                    UniqueId = TextOrNone(view.UniqueId),
                });
            }
            catch (Exception ex)
            {
                errors.Add($"view {view.Id.Value}: {ex.Message}");
            }
        }

        return (sheets, views);
    }

    /// <summary>
    /// Element ids of every Drafting View referenced by a "Reference other
    /// view" callout drawn on a Section or Plan.
    /// </summary>
    /// <remarks>
    /// <b>Not yet implemented - always returns an empty set.</b> Porting a
    /// documented known-gap as a known-gap, not inventing new logic to fill
    /// it: <c>revit_source.py</c>'s own <c>_referenced_drafting_view_ids</c>
    /// is equally a stub, for the same reason - it needs a real workshared
    /// model and Revit's own API reference to confirm whether callout
    /// boundaries collect as distinct elements via
    /// <c>FilteredElementCollector(doc, view.Id).OfCategory(OST_Callouts)</c>
    /// and which property on one names the referenced Drafting View. Every
    /// Drafting View comes back <c>ViewInfo.LinkedToModelSection = false</c>
    /// until this is filled in, which is the conservative failure direction:
    /// an unconfirmed "yes" would silently exclude a view from
    /// <c>revit.dimension_provenance</c> that might carry real drift risk;
    /// an unconfirmed "no" only costs the volume
    /// <c>RuleConfig.SkipUnlinkedDraftingViews</c> exists to cut.
    /// </remarks>
    private static HashSet<long> ReferencedDraftingViewIds(Document doc, List<string> errors) => new();

    /// <summary>
    /// Every dimension in the document, spot dimensions included. See the
    /// class remarks for why this is collected per view rather than via a
    /// single document-wide sweep.
    /// </summary>
    /// <remarks>
    /// <c>OfClass(Dimension)</c> would return <c>SpotDimension</c> too,
    /// since it derives from <c>Dimension</c> - but both are collected and
    /// deduplicated by id explicitly rather than relying on that, because it
    /// costs nothing and a missing population would be invisible. Spot
    /// dimensions matter here more than ordinary ones, not less: a spot
    /// coordinate placed on detail linework is a setout value that looks
    /// authoritative and tracks nothing.
    /// </remarks>
    private static List<Core.Ir.DimensionInfo> CollectDimensions(
        Document doc,
        List<string> errors,
        List<Core.Ir.ViewInfo> views,
        ISet<string>? includeWorksets,
        bool sheetedViewsOnly)
    {
        var seen = new Dictionary<long, Core.Ir.DimensionInfo>();

        foreach (var view in views)
        {
            if (sheetedViewsOnly && view.SheetNo is null)
            {
                continue;
            }

            ElementId viewElementId;
            try
            {
                viewElementId = new ElementId(view.ElementId);
            }
            catch (Exception ex)
            {
                errors.Add($"view {view.ElementId}: could not scope a collector to it: {ex.Message}");
                continue;
            }

            foreach (var cls in new[] { typeof(Dimension), typeof(SpotDimension) })
            {
                FilteredElementCollector collector;
                try
                {
                    collector = new FilteredElementCollector(doc, viewElementId)
                        .OfClass(cls)
                        .WhereElementIsNotElementType();
                }
                catch (Exception ex)
                {
                    errors.Add($"collecting {cls.Name} in view {view.ElementId}: {ex.Message}");
                    continue;
                }

                foreach (Dimension element in collector)
                {
                    var elementId = element.Id.Value;
                    if (seen.ContainsKey(elementId))
                    {
                        continue;
                    }

                    var worksetName = WorksetName(doc, element);
                    if (includeWorksets is not null && worksetName is not null && !includeWorksets.Contains(worksetName))
                    {
                        continue;
                    }

                    try
                    {
                        var references = new List<Core.Ir.ReferenceInfo>();
                        if (element.References is not null)
                        {
                            foreach (Reference reference in element.References)
                            {
                                references.Add(ReadReference(doc, reference, errors));
                            }
                        }

                        Core.Ir.Point3D? origin;
                        try
                        {
                            origin = PointOf(element.Origin);
                        }
                        catch
                        {
                            // Some dimension geometries have no single
                            // origin. Not worth an error record - it costs a
                            // zoom target, not a finding.
                            origin = null;
                        }

                        seen[elementId] = new Core.Ir.DimensionInfo
                        {
                            ElementId = elementId,
                            ViewId = view.ElementId,
                            IsSpot = element is SpotDimension,
                            References = references,
                            Segments = ReadSegments(element),
                            Origin = origin,
                            TypeName = element.DimensionType?.Name,
                            WorksetName = worksetName,
                            UniqueId = TextOrNone(element.UniqueId),
                        };
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"dimension {elementId}: {ex.Message}");
                    }
                }
            }
        }

        return seen.Values.ToList();
    }

    /// <summary>
    /// Describes one dimension endpoint. The linked case matters on bridge
    /// projects, where the structure is routinely linked into a
    /// coordination file: <c>Reference.ElementId</c> then points at the
    /// <see cref="RevitLinkInstance"/>, not at the geometry the dimension
    /// actually measures. Follows <c>Reference.LinkedElementId</c> into the
    /// link document so the facts describe the real element - otherwise
    /// every dimension to a linked beam looks like a dimension to a link.
    /// </summary>
    private static Core.Ir.ReferenceInfo ReadReference(Document doc, Reference reference, List<string> errors)
    {
        var rawId = reference.ElementId?.Value ?? -1L;
        var elementId = rawId;
        var linked = false;
        long? linkInstanceId = null;
        var resolved = true;
        string? className = null;
        string? category = null;
        long? builtinCategory = null;
        bool? viewSpecific = null;

        try
        {
            var element = doc.GetElement(reference.ElementId);

            var linkedId = reference.LinkedElementId;
            if (linkedId is not null && linkedId.Value > 0 && element is RevitLinkInstance linkInstance)
            {
                var linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc is not null)
                {
                    var inner = linkDoc.GetElement(linkedId);
                    if (inner is not null)
                    {
                        linked = true;
                        linkInstanceId = rawId;
                        elementId = linkedId.Value;
                        element = inner;
                    }
                }
            }

            if (element is null)
            {
                resolved = false;
            }
            else
            {
                className = element.GetType().Name;
                viewSpecific = element.ViewSpecific;

                if (element.Category is { } cat)
                {
                    category = cat.Name;
                    builtinCategory = cat.Id.Value;
                }
            }
        }
        catch (Exception ex)
        {
            resolved = false;
            errors.Add($"reference on element {rawId}: {ex.Message}");
        }

        return new Core.Ir.ReferenceInfo
        {
            ElementId = elementId,
            Resolved = resolved,
            ClassName = className,
            Category = category,
            BuiltinCategory = builtinCategory,
            ViewSpecific = viewSpecific,
            Linked = linked,
            LinkInstanceId = linkInstanceId,
        };
    }

    /// <summary>
    /// A dimension's measured values. A dimension *chain* in Revit is one
    /// <see cref="Dimension"/> element carrying many segments, not many
    /// elements - the opposite of the archived DXF pipeline, where chains
    /// had to be reassembled from shared witness points.
    /// <see cref="Dimension.NumberOfSegments"/> is 0 for a plain
    /// single-value dimension, in which case the value lives on the
    /// dimension itself.
    /// </summary>
    private static List<Core.Ir.DimensionSegmentInfo> ReadSegments(Dimension dim)
    {
        var prefix = TextOrNone(dim.Prefix);
        var suffix = TextOrNone(dim.Suffix);

        var count = dim.NumberOfSegments;
        if (count == 0)
        {
            return new List<Core.Ir.DimensionSegmentInfo>
            {
                new()
                {
                    ValueMm = Mm(dim.Value),
                    ValueOverride = TextOrNone(dim.ValueOverride),
                    Prefix = prefix,
                    Suffix = suffix,
                },
            };
        }

        var segments = new List<Core.Ir.DimensionSegmentInfo>();
        foreach (DimensionSegment seg in dim.Segments)
        {
            segments.Add(new Core.Ir.DimensionSegmentInfo
            {
                ValueMm = Mm(seg.Value),
                ValueOverride = TextOrNone(seg.ValueOverride),
                Prefix = TextOrNone(seg.Prefix) ?? prefix,
                Suffix = TextOrNone(seg.Suffix) ?? suffix,
            });
        }

        return segments;
    }

    /// <summary>
    /// The name of the workset <paramref name="element"/> belongs to, or
    /// null. Null on a non-workshared model and on any lookup failure alike
    /// - both mean "nothing to filter on", and neither is worth an
    /// extraction-error entry of its own since the element itself is still
    /// read normally either way. Failing open here (never excluding an
    /// element because its workset couldn't be determined) matches
    /// <see cref="DimensionCollectionResult.ExtractionErrors"/>'s own
    /// principle: a capture must not quietly shrink for a reason nobody can
    /// see.
    /// </summary>
    private static string? WorksetName(Document doc, Element element)
    {
        try
        {
            var worksetId = element.WorksetId;
            if (worksetId is null || worksetId == WorksetId.InvalidWorksetId)
            {
                return null;
            }

            var workset = doc.GetWorksetTable().GetWorkset(worksetId);
            return workset?.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Revit returns an empty string for an unset text property, which is a
    /// different thing from a drafter typing one. Normalize to null so
    /// <c>DimensionSegmentInfo.IsOverridden</c> means what it says.
    /// </summary>
    private static string? TextOrNone(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var text = value.Trim();
        return text.Length == 0 ? null : text;
    }

    private static double? Mm(double? feet) => feet is null ? null : feet.Value * MmPerFoot;

    private static Core.Ir.Point3D? PointOf(XYZ? xyz) =>
        xyz is null ? null : new Core.Ir.Point3D { X = xyz.X * MmPerFoot, Y = xyz.Y * MmPerFoot, Z = xyz.Z * MmPerFoot };
}

/// <summary>
/// Sheets, views and dimensions collected off a live document, plus
/// per-element extraction failures isolated rather than raised - the
/// dimension-side counterpart of <see cref="MetadataCollectionResult"/>,
/// both feeding straight into <c>RevitModel</c>.
/// </summary>
public sealed class DimensionCollectionResult
{
    public required List<Core.Ir.SheetInfo> Sheets { get; init; }
    public required List<Core.Ir.ViewInfo> Views { get; init; }
    public required List<Core.Ir.DimensionInfo> Dimensions { get; init; }
    public required List<string> ExtractionErrors { get; init; }

    /// <summary>
    /// Names of worksets excluded from this capture by user choice. Always
    /// empty today - see the class remarks on <see cref="RevitDimensionSource"/>
    /// for why.
    /// </summary>
    public List<string> ExcludedWorksets { get; init; } = new();
}
