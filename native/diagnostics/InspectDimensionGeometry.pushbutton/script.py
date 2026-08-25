#! python3
"""Dump a dimension's witness-point geometry, its view's cut plane, and
nearby model geometry to JSON.

ONE-OFF DIAGNOSTIC — not part of the frozen RevitCheck extension
(PLANNING.md §12: pyRevit stays "no new buttons, no growing surface").
Copy this pushbutton folder into a scratch/local extension on the Revit
machine, run it once, take the JSON away, then delete it — do not commit
it back into extensions/RevitCheck.extension.

Exists to answer PLANNING.md §14 Track B's three open questions from
ground truth, before any dimension-vs-model comparison logic gets
written blind (this project's own repeated lesson: every extractor that
guessed ahead of a real sample had to be rewritten):

1. Can a dimension's witness point be resolved to a real 3D position in
   model space (`Reference.GlobalPoint`, or a resolved element's own
   `Location`), and how reliably — does it fail for some reference
   types, and if so which?
2. What does a view's cut plane/direction actually look like via the
   API (`View.SketchPlane.GetPlane()`, `View.ViewDirection`,
   `View.Origin`) for the view types drafted dimensions actually live
   in (Section vs. Detail vs. Drafting View vs. EngineeringPlan) — do
   they differ in ways that matter?
3. For a real witness point, what model geometry is actually nearby (a
   bounding-box-scoped collector query)? Is there a real geometric
   signal here at all, or does "nearest model edge/face wins" need a
   different approach entirely?

**How to run it:** first identify drafted views to look at — either run
the real Dimension Provenance button and read its "views to verify"
list, or run `native/tools/RevitCheck.CheckRunner` against a capture and
read its "Views to verify against the model" section (both expose
`DimensionProvenanceCheck.DraftedViews()`). Open one of those views in
Revit, then either select some/all of its dimensions and run this
button, or run it with nothing selected — it falls back to every
Dimension/SpotDimension in the active view, matching the same per-view
collection the real adapter uses (`RevitDimensionSource.cs`/
`revit_source.py`'s own `OwnerViewId`-isn't-trustworthy fix: a
view-specific element can only be selected while its real owning view is
active, so "the active view" is a safe stand-in for "this dimension's
owning view" either way — no `OwnerViewId` read needed here at all).

**Scope this to real setout-critical dimensions first** — piles/
abutments/foundations, matching `geometry.ifc_setout_consistency`'s own
original scope (ARCHIVE-pdf-dwg.md) and `RuleConfig.SetoutCriticalTypeNames`'s
existing (currently unpopulated) config knob — not every drafted
dimension in the model. A handful of real, representative dimensions
answers the three questions above; a full-model sweep does not answer
them any better and produces a much larger file to review.

The output contains real client geometry (coordinates, element
identities). Treat it the way this project treats a capture
(PLANNING.md §2): check before it leaves this machine, and do not
commit it to git.

**Revised after the first real run (2026-08-25, section DRG-2873022,
7 dimensions/17 references — see PLANNING.md §14 for the full
write-up):** `Reference.GlobalPoint` came back null for every reference
seen (LINEAR/SURFACE/CUT_EDGE alike), and the `Location`-based fallback
silently returned `(0, 0, 0)` — Revit's internal origin, not a real
position — for hosted model `FamilyInstance`s (Walls/Floors)
specifically, which is exactly the case a real comparison needs most.
`DimensionSegment.Origin` was the one position that came back real and
plausible for every segment that had one, so `_describe_segments` now
runs its own nearby-geometry search anchored there too, not only on the
(sometimes-misleading) per-reference points.
"""

import os

from pyrevit import revit, script

from Autodesk.Revit.DB import (
    BoundingBoxIntersectsFilter,
    Dimension,
    ElementId,
    FilteredElementCollector,
    Outline,
    SpotDimension,
    XYZ,
)

output = script.get_output()
output.set_title("Inspect Dimension Geometry (diagnostic)")

doc = revit.doc
uidoc = revit.uidoc

MM_PER_FOOT = 304.8

# How far to search around a resolved witness point for nearby model
# geometry - generous enough to catch a face/edge a few hundred mm away
# without also catching everything on the sheet.
NEARBY_SEARCH_RADIUS_MM = 750.0
NEARBY_SEARCH_RADIUS_FT = NEARBY_SEARCH_RADIUS_MM / MM_PER_FOOT
MAX_NEARBY_ELEMENTS_LISTED = 25


def _eid(element_id):
    """Version-safe ElementId -> int (Revit 2024+ uses .Value; older uses .IntegerValue)."""
    if element_id is None:
        return None
    try:
        return element_id.Value
    except AttributeError:
        pass
    try:
        return element_id.IntegerValue
    except AttributeError:
        return None


def _mm(feet):
    return None if feet is None else float(feet) * MM_PER_FOOT


def _point(xyz):
    if xyz is None:
        return None
    return [_mm(xyz.X), _mm(xyz.Y), _mm(xyz.Z)]


def _vector(xyz):
    """A direction, unitless (not a length) - no mm conversion."""
    if xyz is None:
        return None
    return [xyz.X, xyz.Y, xyz.Z]


def _workset_name(element):
    try:
        workset_id = getattr(element, "WorksetId", None)
        if workset_id is None:
            return None
        workset = doc.GetWorksetTable().GetWorkset(workset_id)
        return str(workset.Name) if workset is not None else None
    except Exception:  # noqa: BLE001
        return None


def _describe_view(view):
    entry = {
        "element_id": _eid(view.Id),
        "name": None,
        "view_type": None,
        "sheet_no": None,
        "sketch_plane": None,
        "view_direction": None,
        "up_direction": None,
        "right_direction": None,
        "origin": None,
        "crop_box": None,
    }
    errors = []

    try:
        entry["name"] = view.Name
    except Exception as exc:  # noqa: BLE001
        errors.append("name: {0}".format(exc))

    try:
        entry["view_type"] = str(view.ViewType)
    except Exception as exc:  # noqa: BLE001
        errors.append("view_type: {0}".format(exc))

    try:
        sheet_id = getattr(view, "SheetId", None)
        if sheet_id is not None and sheet_id != ElementId.InvalidElementId:
            sheet = doc.GetElement(sheet_id)
            if sheet is not None:
                entry["sheet_no"] = getattr(sheet, "SheetNumber", None)
    except Exception:  # noqa: BLE001 - a view's own sheet lookup isn't the point here
        pass

    try:
        plane = view.SketchPlane.GetPlane()
        entry["sketch_plane"] = {"origin": _point(plane.Origin), "normal": _vector(plane.Normal)}
    except Exception as exc:  # noqa: BLE001 - not every view has a SketchPlane
        errors.append("sketch_plane: {0}".format(exc))

    try:
        entry["view_direction"] = _vector(view.ViewDirection)
    except Exception as exc:  # noqa: BLE001
        errors.append("view_direction: {0}".format(exc))

    try:
        entry["up_direction"] = _vector(view.UpDirection)
    except Exception as exc:  # noqa: BLE001
        errors.append("up_direction: {0}".format(exc))

    try:
        entry["right_direction"] = _vector(view.RightDirection)
    except Exception as exc:  # noqa: BLE001
        errors.append("right_direction: {0}".format(exc))

    try:
        entry["origin"] = _point(view.Origin)
    except Exception as exc:  # noqa: BLE001
        errors.append("origin: {0}".format(exc))

    try:
        if view.CropBoxActive:
            box = view.CropBox
            entry["crop_box"] = {"min": _point(box.Min), "max": _point(box.Max)}
    except Exception as exc:  # noqa: BLE001
        errors.append("crop_box: {0}".format(exc))

    if errors:
        entry["_errors"] = errors
    return entry


def _nearby_elements(global_point_xyz, exclude_element_id):
    """Elements whose bounding box intersects a small region around a
    witness point - question 3's "is there a real geometric signal
    here" probe. Capped, not exhaustive: enough to see what's actually
    nearby, not a full spatial index."""
    try:
        min_pt = XYZ(
            global_point_xyz.X - NEARBY_SEARCH_RADIUS_FT,
            global_point_xyz.Y - NEARBY_SEARCH_RADIUS_FT,
            global_point_xyz.Z - NEARBY_SEARCH_RADIUS_FT,
        )
        max_pt = XYZ(
            global_point_xyz.X + NEARBY_SEARCH_RADIUS_FT,
            global_point_xyz.Y + NEARBY_SEARCH_RADIUS_FT,
            global_point_xyz.Z + NEARBY_SEARCH_RADIUS_FT,
        )
        outline = Outline(min_pt, max_pt)
        collector = FilteredElementCollector(doc).WherePasses(BoundingBoxIntersectsFilter(outline)).WhereElementIsNotElementType()
    except Exception as exc:  # noqa: BLE001
        return {"search_radius_mm": NEARBY_SEARCH_RADIUS_MM, "total_nearby": None, "listed": [], "truncated": False, "_error": str(exc)}

    found = []
    total = 0
    for element in collector:
        eid = _eid(element.Id)
        if eid is None or eid == exclude_element_id:
            continue
        total += 1
        if len(found) >= MAX_NEARBY_ELEMENTS_LISTED:
            continue
        entry = {"element_id": eid, "class_name": type(element).__name__, "category": None, "workset": _workset_name(element)}
        try:
            if element.Category is not None:
                entry["category"] = element.Category.Name
        except Exception:  # noqa: BLE001
            pass
        found.append(entry)

    return {"search_radius_mm": NEARBY_SEARCH_RADIUS_MM, "total_nearby": total, "listed": found, "truncated": total > len(found)}


def _describe_reference(ref, dimension_element_id):
    entry = {
        "element_reference_type": None,
        "linked": False,
        "resolved_element_id": _eid(getattr(ref, "ElementId", None)),
        "resolved_category": None,
        "resolved_class_name": None,
        "resolved_workset": None,
        "stable_representation": None,
        "global_point": None,
        "element_location_point": None,
        "nearby_geometry": None,
    }
    errors = []

    try:
        entry["element_reference_type"] = str(ref.ElementReferenceType)
    except Exception as exc:  # noqa: BLE001
        errors.append("element_reference_type: {0}".format(exc))

    try:
        entry["stable_representation"] = ref.ConvertToStableRepresentation(doc)
    except Exception as exc:  # noqa: BLE001
        errors.append("stable_representation: {0}".format(exc))

    resolved_element = None
    try:
        linked_id = _eid(getattr(ref, "LinkedElementId", None))
        host_element = doc.GetElement(ref.ElementId) if ref.ElementId is not None else None
        if linked_id is not None and linked_id > 0 and host_element is not None:
            get_link_doc = getattr(host_element, "GetLinkDocument", None)
            link_doc = get_link_doc() if get_link_doc is not None else None
            if link_doc is not None:
                inner = link_doc.GetElement(ElementId(linked_id))
                if inner is not None:
                    entry["linked"] = True
                    entry["resolved_element_id"] = linked_id
                    resolved_element = inner
        if resolved_element is None:
            resolved_element = host_element
    except Exception as exc:  # noqa: BLE001
        errors.append("resolving element: {0}".format(exc))

    if resolved_element is not None:
        try:
            entry["resolved_class_name"] = type(resolved_element).__name__
        except Exception:  # noqa: BLE001
            pass
        try:
            if resolved_element.Category is not None:
                entry["resolved_category"] = resolved_element.Category.Name
        except Exception:  # noqa: BLE001
            pass
        try:
            entry["resolved_workset"] = _workset_name(resolved_element)
        except Exception:  # noqa: BLE001
            pass
        # A secondary way to get a position when GlobalPoint fails or
        # isn't the point that matters - question 1's fallback.
        try:
            location = resolved_element.Location
            point = getattr(location, "Point", None)
            if point is not None:
                entry["element_location_point"] = _point(point)
            else:
                curve = getattr(location, "Curve", None)
                if curve is not None:
                    entry["element_location_point"] = _point(curve.Evaluate(0.5, True))
        except Exception:  # noqa: BLE001 - most references have no simple Location, that's expected
            pass

    global_xyz = None
    try:
        global_xyz = ref.GlobalPoint
        entry["global_point"] = _point(global_xyz)
    except Exception as exc:  # noqa: BLE001 - the actual thing question 1 is asking
        errors.append("global_point: {0}".format(exc))

    search_point = global_xyz
    if search_point is None and resolved_element is not None:
        try:
            location = resolved_element.Location
            search_point = getattr(location, "Point", None)
        except Exception:  # noqa: BLE001
            search_point = None

    if search_point is not None:
        entry["nearby_geometry"] = _nearby_elements(search_point, dimension_element_id)

    if errors:
        entry["_errors"] = errors
    return entry


def _describe_curve(dim):
    try:
        curve = dim.Curve
    except Exception as exc:  # noqa: BLE001 - not every dimension has one (e.g. some angular/arc cases)
        return {"_error": str(exc)}
    if curve is None:
        return None
    # GetEndPoint(0/1) works the same way across every Curve subclass
    # (Line, Arc, ...) - "kind" above already records which one this was,
    # no need to branch on it here too.
    entry = {"kind": type(curve).__name__}
    try:
        entry["start"] = _point(curve.GetEndPoint(0))
        entry["end"] = _point(curve.GetEndPoint(1))
    except Exception as exc:  # noqa: BLE001
        entry["_error"] = str(exc)
    return entry


def _describe_segments(dim, dimension_element_id):
    """Each segment's value/origin, plus a nearby-geometry search anchored
    on the segment's own `Origin` - found necessary on the first real run
    (2026-08-25, see PLANNING.md §14): `Reference.GlobalPoint` came back
    null for every reference type seen, and the `Location`-based fallback
    silently returned (0,0,0) - Revit's internal origin, not a real
    position - for hosted model FamilyInstances specifically (Walls/
    Floors), which polluted their `nearby_geometry` search with whatever
    happens to sit near the model's internal origin instead of near the
    dimension. `DimensionSegment.Origin` was the one position in that run
    that came back real and plausible (matching the view's own Origin's
    coordinate range) for every segment that had one - worth its own
    independent nearby-geometry search rather than trusting only the
    per-reference anchors, which is what this fixes.
    """
    count = int(getattr(dim, "NumberOfSegments", 0) or 0)
    if count == 0:
        raw_origin = None
        try:
            raw_origin = dim.Origin
        except Exception:  # noqa: BLE001 - some dimension geometries have no single origin
            raw_origin = None
        return [
            {
                "index": 0,
                "value_mm": _mm(getattr(dim, "Value", None)),
                "value_override": getattr(dim, "ValueOverride", None),
                "origin": _point(raw_origin),
                "nearby_geometry": _nearby_elements(raw_origin, dimension_element_id) if raw_origin is not None else None,
            }
        ]

    segments = []
    for index, seg in enumerate(dim.Segments):
        raw_origin = None
        try:
            raw_origin = seg.Origin
        except Exception:  # noqa: BLE001 - not every segment has one
            raw_origin = None
        segments.append(
            {
                "index": index,
                "value_mm": _mm(getattr(seg, "Value", None)),
                "value_override": getattr(seg, "ValueOverride", None),
                "origin": _point(raw_origin),
                "nearby_geometry": _nearby_elements(raw_origin, dimension_element_id) if raw_origin is not None else None,
            }
        )
    return segments


def _describe_dimension(dim):
    entry = {
        "element_id": _eid(dim.Id),
        "unique_id": None,
        "is_spot_dimension": isinstance(dim, SpotDimension),
        "dimension_type_name": None,
        "number_of_segments": int(getattr(dim, "NumberOfSegments", 0) or 0),
        "origin": None,
        "curve": None,
        "segments": [],
        "references": [],
    }
    errors = []

    try:
        entry["unique_id"] = dim.UniqueId
    except Exception as exc:  # noqa: BLE001
        errors.append("unique_id: {0}".format(exc))

    try:
        dim_type = getattr(dim, "DimensionType", None)
        if dim_type is not None:
            entry["dimension_type_name"] = getattr(dim_type, "Name", None)
    except Exception as exc:  # noqa: BLE001
        errors.append("dimension_type_name: {0}".format(exc))

    try:
        entry["origin"] = _point(getattr(dim, "Origin", None))
    except Exception:  # noqa: BLE001 - some dimension geometries have no single origin
        entry["origin"] = None

    entry["curve"] = _describe_curve(dim)

    dim_element_id = entry["element_id"]
    entry["segments"] = _describe_segments(dim, dim_element_id)

    try:
        entry["references"] = [_describe_reference(ref, dim_element_id) for ref in (dim.References or [])]
    except Exception as exc:  # noqa: BLE001
        errors.append("references: {0}".format(exc))

    if errors:
        entry["_errors"] = errors
    return entry


selected_ids = list(uidoc.Selection.GetElementIds())
dimensions = []
source = None

if selected_ids:
    for eid in selected_ids:
        element = doc.GetElement(eid)
        if isinstance(element, Dimension):
            dimensions.append(element)
    source = "selection"

if not dimensions:
    active_view = doc.ActiveView
    if active_view is None:
        output.print_md("### No active view and nothing selected\n\nOpen a drafted view first, then run this again.")
        script.exit()
    try:
        for cls in (Dimension, SpotDimension):
            collector = FilteredElementCollector(doc, active_view.Id).OfClass(cls).WhereElementIsNotElementType()
            seen_ids = {_eid(d.Id) for d in dimensions}
            for element in collector:
                if _eid(element.Id) not in seen_ids:
                    dimensions.append(element)
                    seen_ids.add(_eid(element.Id))
    except Exception as exc:  # noqa: BLE001
        output.print_md("### Could not collect dimensions from the active view\n\n`{0}`".format(exc))
        script.exit()
    source = "active view ({0})".format(active_view.Name)

if not dimensions:
    output.print_md(
        "### No dimensions found\n\nSelect one or more dimensions, or open a view "
        "that has some, then run this again."
    )
    script.exit()

view_cache = {}
results = []
for dim in dimensions:
    entry = _describe_dimension(dim)
    try:
        owner_view = doc.ActiveView
        view_key = _eid(owner_view.Id)
        if view_key not in view_cache:
            view_cache[view_key] = _describe_view(owner_view)
        entry["view"] = view_cache[view_key]
    except Exception as exc:  # noqa: BLE001
        entry["view"] = {"_error": str(exc)}
    results.append(entry)


default_name = "{0}.inspect_dimension_geometry.json".format(
    os.path.splitext(os.path.basename(doc.Title or "model"))[0]
)


def _ask_where_to_save(suggested_name):
    """Same WPF SaveFileDialog fallback pattern as CaptureModel's script.py —
    pyrevit.forms is IronPython-only under CPython, see the README's
    "Three CPython gotchas"."""
    try:
        import clr

        clr.AddReference("PresentationFramework")
        from Microsoft.Win32 import SaveFileDialog

        dialog = SaveFileDialog()
        dialog.FileName = suggested_name
        dialog.DefaultExt = ".json"
        dialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        return dialog.FileName if dialog.ShowDialog() else None
    except Exception as exc:  # noqa: BLE001 - fall back, don't lose the read
        fallback = os.path.join(os.path.expanduser("~"), "Documents", suggested_name)
        output.print_md(
            "> Could not open a save dialog (`{0}`), so this was written to "
            "the default location below.".format(exc)
        )
        return fallback


path = _ask_where_to_save(default_name)
if not path:
    script.exit()

import json  # noqa: E402 - after the early-exit paths above, same style as capture.py

with open(path, "w") as f:
    json.dump({"source": source, "dimensions": results}, f, indent=2, sort_keys=True)

output.print_md("### Inspection written")
output.print_md("`{0}`".format(path))
output.print_md("")
output.print_md("- {0} dimension(s) captured, from {1}".format(len(results), source))
output.print_md(
    "- Delete this file once you're done with it, and don't commit it — "
    "same caution as a real capture (PLANNING.md §2)."
)
