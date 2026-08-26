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

**Revised again after the second real run (2026-08-25, same view,
segment-Origin fix in place):** the segment-Origin anchor was real and
plausible, but still didn't land near real geometry — the search from it
found the dimension's own referenced elements 0% of the time (checked
directly: neither of two references on one dimension turned up in that
dimension's own 750mm nearby-list), and the document-wide, unscoped
search was dominated by Cameras/Work Plane Grids/Scope Boxes/Groups
pulled in from unrelated views. Two fixes: (1) `_describe_reference` now
resolves each `Reference` to its actual touched `GeometryObject` via
`Element.GetGeometryObjectFromReference(ref)` and uses *that* real
point (`geometry_point`) as the search anchor, ahead of `GlobalPoint`/
`Location` — the correct API for "what does this specific reference
touch", not a proximity guess. (2) `_nearby_elements` now excludes the
categories/classes this run actually returned as pure noise
(`_NOISE_CATEGORIES`/`_NOISE_CLASSES`) rather than searching the whole
unfiltered document.

**Revised a third time after the third real run (2026-08-25, same view,
both prior fixes in place):** `GetGeometryObjectFromReference` resolved
cleanly for every `LINEAR` reference (real `Line`/`Edge` points via
`Evaluate(0.5, True)`) and the noise filter worked — no more Cameras/
Scope Boxes/Groups/internal-origin garbage, real structural content
(Floors, Structural Framing) turning up near resolved points instead.
But every `CUT_EDGE` reference — the ones pointing at real model
`FamilyInstance`s (Walls/Floors), the case that matters most — resolved
to a `Face` (`PlanarFace`/`RuledFace`), and `Face.Evaluate` takes a
single `UV`, not `(double, bool)`: the call threw, silently fell
through to `.Origin` (present on `PlanarFace` but not guaranteed near
the visible face; absent on `RuledFace` entirely), and from there fell
all the way back to the same broken `Location` `(0, 0, 0)` a *second*
time — confirmed directly (`element_location_point: [0,0,0]` on the
Floor reference that returned `geometry_point: null`). Fixed: `Face`
now resolves via its own `GetBoundingBox()` UV midpoint, and `Location`
is out of the search-anchor fallback chain entirely (a missing result
is more honest than a wrong one that looks like data).

**Revised a fourth time after the fourth real run (2026-08-25, same
view, the Face-bbox-midpoint fix in place):** every reference now
resolved to a real point with no errors — genuine progress. But
checking the actual numbers against the dimension's own typed values
exposed the bbox-midpoint's real limit: on a real 2-segment chain, the
3D distance between two Face-resolved points came out 4191mm and
1897mm against typed segment values of 451mm and 1489mm. A face can be
large, and its bounding-box midpoint has no reason to be anywhere near
where a specific dimension actually touches it — real geometry, wrong
point on it. Fixed properly: `Face.Project(candidate_point)` returns
the point *on the face* nearest to an arbitrary input point, so given
any reasonably-nearby real point it lands on the actual touched
location instead of an arbitrary one on the same face.
`_projection_candidate_xyz` supplies that input — the dimension's own
`Origin` when it has one (only single-value dimensions do), else its
first segment's `Origin` (real and in-range per the first real run,
just not itself precise enough to be the answer - which is exactly why
it's a projection candidate and not the final point). Not yet run with
this fix.

**Extended 2026-08-26, a different question than any of the above — not
another patch to the Face-projection logic, a genuinely new probe.**
Run 7 (a whole real pile-layout view, separate from this diagnostic's
own runs above, see PLANNING.md §14) found pile setout on this project
is drafted tag-to-tag against `AnnotationSymbol`s, never touching model
geometry at all - so `CUT_EDGE`/`Face.Project` has nothing to resolve
for a pile dimension specifically, no matter how precise it gets. This
raised a more direct question the same day: since a dimension's
reference already resolves to a tag's own `element_location_point`, can
that tag be matched to the nearest real `Pile` element, and can the
dimension's own stated value then be checked directly against the
measured distance between two real piles - no schedule, no bearing/DMS
parsing, no chain walk? `_collect_piles`/`_nearest_pile`/the new
`pile_match` section on each dimension exist to answer that from real
data before any matching/comparison logic gets written blind, same
discipline as every probe above. Two real risks, neither validated yet:
whether the nearest pile to a tag is actually the *correct* pile
(cross-checkable here against `DIT_SiteID`, and against the schedule's
own `LOCATION`/`SITE ID` columns by hand), and whether a tag sits at/near
its own pile at all rather than leader-offset (which would make
tag-to-tag and pile-to-pile distances disagree even when everything is
drafted correctly - the same *shape* of failure the Face-projection work
above hit once, for a different underlying reason). Matching is
deliberately 2D (X/Y) only, never 3D: a real check of this project's own
committed diagnostic output found an `AnnotationSymbol` reference's Z
sitting at a symbolic ~200,000mm annotation-plane value against a real
pile's ~18,500mm Z - a ~180m gap that would defeat any 3D search outright.
Not yet run.
"""

import math
import os

from pyrevit import revit, script

from Autodesk.Revit.DB import (
    BoundingBoxIntersectsFilter,
    BuiltInCategory,
    Dimension,
    ElementId,
    Face,
    FilteredElementCollector,
    Outline,
    SpotDimension,
    UV,
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


# Categories/classes confirmed as pure noise on the first real run
# (2026-08-25, PLANNING.md §14): a document-wide bounding-box search with
# no scoping pulled in cameras, work-plane grids, scope boxes, and
# view-specific annotation groups from *unrelated* views/3D views - none
# of which can ever be "model geometry a dimension might be verifying
# against", and their presence buried the few real hits. Data-driven, not
# a guess ahead of a sample: every name here is something this run
# actually returned as noise, not a speculative exclusion list.
_NOISE_CATEGORIES = {
    "Cameras",
    "Work Plane Grid",
    "Scope Boxes",
    "Guide Grid",
    "Internal Origin",
    "Survey Point",
    "Project Base Point",
}
_NOISE_CLASSES = {"Group"}

# --- Pile proximity matching (added 2026-08-26) - see the module
# docstring's "Extended" note for the real question this answers. ---

PILE_KEY_PARAMETER_NAME = "DIT_SiteID"


def _pile_key_value(pile_element):
    try:
        param = pile_element.LookupParameter(PILE_KEY_PARAMETER_NAME)
        if param is None:
            return None
        return param.AsString() or param.AsValueString()
    except Exception:  # noqa: BLE001
        return None


def _collect_piles():
    """Every OST_StructuralFoundation element with a resolvable Location.
    Point, its own real 3D position, and its DIT_SiteID - collected once,
    document-wide. Real pile counts on this project are small enough
    (dozens per schedule) that a plain document-wide sweep is simpler and
    more honest than tuning a search-radius collector filter."""
    piles = []
    errors = []
    try:
        collector = (
            FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_StructuralFoundation)
            .WhereElementIsNotElementType()
        )
    except Exception as exc:  # noqa: BLE001
        return {"piles": [], "_error": str(exc)}

    for element in collector:
        try:
            location = element.Location
            point = getattr(location, "Point", None)
        except Exception as exc:  # noqa: BLE001
            errors.append("pile {0}: {1}".format(_eid(element.Id), exc))
            continue
        if point is None:
            continue
        piles.append(
            {
                "element_id": _eid(element.Id),
                "key_value": _pile_key_value(element),
                "point": point,  # raw XYZ - kept for distance math, not JSON-serialized directly
                "point_mm": _point(point),
            }
        )
    return {"piles": piles, "_errors": errors} if errors else {"piles": piles}


def _nearest_pile(tag_point_xyz, piles):
    """2D (X/Y-only) nearest-neighbour search - see the module docstring
    for why Z is deliberately excluded (a real ~180m Z gap between a real
    AnnotationSymbol reference and a real pile on this project). Returns
    the nearest pile's own record plus the 2D distance, or None if there
    are no piles or no tag point to search from."""
    if tag_point_xyz is None or not piles:
        return None

    best = None
    best_dist_ft = None
    for pile in piles:
        p = pile["point"]
        dx = tag_point_xyz.X - p.X
        dy = tag_point_xyz.Y - p.Y
        dist_ft = math.sqrt(dx * dx + dy * dy)
        if best_dist_ft is None or dist_ft < best_dist_ft:
            best_dist_ft = dist_ft
            best = pile

    return {
        "pile_element_id": best["element_id"],
        "pile_key_value": best["key_value"],
        "pile_point_mm": best["point_mm"],
        "distance_2d_mm": _mm(best_dist_ft),
    }


def _pile_to_pile_distance_mm(pile_a, pile_b):
    """Real 3D and 2D-only distances between two matched piles' own
    points - both reported, since it isn't yet known (that's what this
    diagnostic exists to find out) whether this project's pile dimensions
    measure a straight 3D distance or a plan-projected one."""
    a = pile_a["point"]
    b = pile_b["point"]
    dx, dy, dz = a.X - b.X, a.Y - b.Y, a.Z - b.Z
    return {
        "distance_3d_mm": _mm(math.sqrt(dx * dx + dy * dy + dz * dz)),
        "distance_2d_mm": _mm(math.sqrt(dx * dx + dy * dy)),
    }


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

        class_name = type(element).__name__
        category_name = None
        try:
            if element.Category is not None:
                category_name = element.Category.Name
        except Exception:  # noqa: BLE001
            pass

        if class_name in _NOISE_CLASSES or category_name in _NOISE_CATEGORIES:
            continue

        total += 1
        if len(found) >= MAX_NEARBY_ELEMENTS_LISTED:
            continue
        found.append(
            {"element_id": eid, "class_name": class_name, "category": category_name, "workset": _workset_name(element)}
        )

    return {"search_radius_mm": NEARBY_SEARCH_RADIUS_MM, "total_nearby": total, "listed": found, "truncated": total > len(found)}


def _geometry_object_point(geom_obj, candidate_point=None):
    """A representative 3D point actually on a resolved GeometryObject
    (Edge/Curve/Face) - not a guess, the real touched geometry.

    Found necessary on the third real run (2026-08-25, see PLANNING.md
    §14): the previous version tried `Evaluate(0.5, True)` first for
    everything, which works for Edge/Curve (both confirmed for real) but
    throws for Face (`Face.Evaluate` takes a single `UV`, not
    `(double, bool)`) - silently caught, falling through to `Origin`,
    which a `PlanarFace` has (but isn't guaranteed to be anywhere *on*
    the visible bounded face - it's the point defining the face's
    underlying infinite plane) and a `RuledFace` doesn't have at all.
    The `GetBoundingBox()`-midpoint fix that followed resolved every
    Face without erroring, but the fourth real run found it too
    imprecise to be useful for anything more than "yes, this is roughly
    the right geometry": on a real 2-segment chain, the distance between
    two Face-resolved bbox-midpoints came out 4191mm and 1897mm against
    typed segment values of 451mm and 1489mm - a real face can be large,
    and its own bounding-box midpoint has no reason to be anywhere near
    where a specific dimension actually touches it.

    `candidate_point` (see `_projection_candidate_xyz` - the dimension's
    own `Origin` when it has one, else its first segment's) fixes this
    the correct way: `Face.Project(point)` returns the point *on the
    face* nearest to an arbitrary input point - given any reasonably-
    nearby real point, it lands on the actual touched location instead
    of an arbitrary one. Falls back to the bbox midpoint when there's no
    candidate point or the projection fails (e.g. the candidate is
    degenerate or off the face's parameter domain entirely) - still real
    geometry, just not this precise.
    """
    if geom_obj is None:
        return None

    if isinstance(geom_obj, Face):
        if candidate_point is not None:
            try:
                result = geom_obj.Project(candidate_point)
                if result is not None:
                    return result.XYZPoint
            except Exception:  # noqa: BLE001 - candidate point may be off the face entirely
                pass
        try:
            bbox = geom_obj.GetBoundingBox()
            mid_uv = UV((bbox.Min.U + bbox.Max.U) / 2.0, (bbox.Min.V + bbox.Max.V) / 2.0)
            return geom_obj.Evaluate(mid_uv)
        except Exception:  # noqa: BLE001
            pass
        return getattr(geom_obj, "Origin", None)

    # Edge and Curve both expose Evaluate(double, bool) - normalized
    # parameter - confirmed working for real on this project's data.
    evaluate = getattr(geom_obj, "Evaluate", None)
    if evaluate is not None:
        try:
            return evaluate(0.5, True)
        except Exception:  # noqa: BLE001
            pass
    as_curve = getattr(geom_obj, "AsCurve", None)
    if as_curve is not None:
        try:
            curve = as_curve()
            if curve is not None:
                return curve.Evaluate(0.5, True)
        except Exception:  # noqa: BLE001
            pass
    return getattr(geom_obj, "Origin", None)


def _describe_reference(ref, dimension_element_id, projection_candidate_xyz, piles=None):
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
        "geometry_object_kind": None,
        "geometry_point": None,
        "nearby_geometry": None,
        "nearest_pile": None,
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
    host_element = None
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
        raw_location_point = None
        try:
            location = resolved_element.Location
            point = getattr(location, "Point", None)
            if point is not None:
                raw_location_point = point
                entry["element_location_point"] = _point(point)
            else:
                curve = getattr(location, "Curve", None)
                if curve is not None:
                    raw_location_point = curve.Evaluate(0.5, True)
                    entry["element_location_point"] = _point(raw_location_point)
        except Exception:  # noqa: BLE001 - most references have no simple Location, that's expected
            pass

        # Pile proximity match (added 2026-08-26) - see the module
        # docstring's "Extended" note. 2D only, from the reference's own
        # resolved location, whatever it is (an AnnotationSymbol tag on
        # this project's real pile-layout dimensions, per run 7).
        if piles:
            try:
                entry["nearest_pile"] = _nearest_pile(raw_location_point, piles)
            except Exception as exc:  # noqa: BLE001
                errors.append("nearest_pile: {0}".format(exc))

    global_xyz = None
    try:
        global_xyz = ref.GlobalPoint
        entry["global_point"] = _point(global_xyz)
    except Exception as exc:  # noqa: BLE001 - the actual thing question 1 is asking
        errors.append("global_point: {0}".format(exc))

    # Added after the first real run (2026-08-25) found GlobalPoint always
    # null and the Location fallback silently wrong for hosted model
    # FamilyInstances (see PLANNING.md §14). This is the actually-correct
    # API for "what does this specific Reference touch": not a position
    # guess, the real Edge/Face/Curve GeometryObject the reference was
    # built from - only meaningful on the host element that owns the
    # reference's stable representation, so this is skipped for a
    # followed-link reference (`entry["linked"]`), which needs its own,
    # not-yet-built handling.
    geometry_xyz = None
    if host_element is not None and not entry["linked"]:
        try:
            geom_obj = host_element.GetGeometryObjectFromReference(ref)
            entry["geometry_object_kind"] = type(geom_obj).__name__ if geom_obj is not None else None
            geometry_xyz = _geometry_object_point(geom_obj, projection_candidate_xyz)
            entry["geometry_point"] = _point(geometry_xyz)
        except Exception as exc:  # noqa: BLE001
            errors.append("geometry_object: {0}".format(exc))

    # Preference order for where to search from: the actual touched
    # geometry (new, most trustworthy) > GlobalPoint (proved unusable so
    # far, kept as a fallback in case a reference type turns up where it
    # isn't). Location is deliberately NOT in this chain any more - it
    # silently produced (0,0,0), Revit's internal origin, for real model
    # FamilyInstances on both of the first two real runs (PLANNING.md
    # §14), and a missing search result is a far more honest failure mode
    # than a wrong one that looks like data. `element_location_point`
    # above still records it for visibility - just not as a search anchor.
    # Explicit None-checks, not `or` - an XYZ from the Revit API has no
    # defined truthiness, and a legitimate point can have all-zero
    # coordinates (seen for real on this project's Internal Origin), so
    # `or` risks silently falling through past a valid point.
    search_point = geometry_xyz if geometry_xyz is not None else global_xyz

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


def _projection_candidate_xyz(dim):
    """A raw XYZ reasonably near the dimension, for `Face.Project` to
    project from - doesn't need to be exact, just in the right
    neighbourhood, since `Project` finds the true nearest point on the
    face from wherever it's given. `Dimension.Origin` only exists for a
    single-value dimension (`NumberOfSegments == 0`); a real chain's own
    `Origin` came back null on this project's real data, so the first
    segment's `Origin` is the fallback - itself real and in-range (see
    PLANNING.md §14's run 2), just not close enough to *be* the answer,
    which is exactly why this is a projection candidate and not the
    final point."""
    try:
        origin = dim.Origin
        if origin is not None:
            return origin
    except Exception:  # noqa: BLE001
        pass

    try:
        if int(getattr(dim, "NumberOfSegments", 0) or 0) > 0:
            for seg in dim.Segments:
                try:
                    return seg.Origin
                except Exception:  # noqa: BLE001
                    continue
    except Exception:  # noqa: BLE001
        pass
    return None


def _describe_dimension(dim, piles=None):
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
        "pile_match": None,
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

    projection_candidate_xyz = _projection_candidate_xyz(dim)
    try:
        entry["references"] = [
            _describe_reference(ref, dim_element_id, projection_candidate_xyz, piles=piles)
            for ref in (dim.References or [])
        ]
    except Exception as exc:  # noqa: BLE001
        errors.append("references: {0}".format(exc))

    # Pile-to-pile comparison (added 2026-08-26) - see the module
    # docstring's "Extended" note. Only meaningful with exactly two
    # distinct matched piles (a straight-line dimension between two
    # points); a dimension chain with more segments, or references that
    # matched the same pile twice, is left as None rather than guessed at.
    if piles:
        try:
            matched_ids = [
                r["nearest_pile"]["pile_element_id"]
                for r in entry["references"]
                if r.get("nearest_pile") is not None
            ]
            if len(matched_ids) == 2 and matched_ids[0] != matched_ids[1]:
                by_id = {p["element_id"]: p for p in piles}
                pile_a, pile_b = by_id.get(matched_ids[0]), by_id.get(matched_ids[1])
                if pile_a is not None and pile_b is not None:
                    entry["pile_match"] = {
                        "pile_a_element_id": matched_ids[0],
                        "pile_a_key_value": pile_a["key_value"],
                        "pile_b_element_id": matched_ids[1],
                        "pile_b_key_value": pile_b["key_value"],
                    }
                    entry["pile_match"].update(_pile_to_pile_distance_mm(pile_a, pile_b))
        except Exception as exc:  # noqa: BLE001
            errors.append("pile_match: {0}".format(exc))

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

pile_collection = _collect_piles()
piles = pile_collection["piles"]

view_cache = {}
results = []
for dim in dimensions:
    entry = _describe_dimension(dim, piles=piles)
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

# Strip the raw XYZ objects (not JSON-serializable, and already captured
# as point_mm) before writing - piles list itself stays in memory as-is
# for the distance math above, this is only for the dump.
piles_for_json = [{k: v for k, v in pile.items() if k != "point"} for pile in piles]

with open(path, "w") as f:
    json.dump(
        {
            "source": source,
            "piles": piles_for_json,
            "pile_collection_errors": pile_collection.get("_errors"),
            "dimensions": results,
        },
        f,
        indent=2,
        sort_keys=True,
    )

output.print_md("### Inspection written")
output.print_md("`{0}`".format(path))
output.print_md("")
output.print_md("- {0} dimension(s) captured, from {1}".format(len(results), source))
output.print_md("- {0} pile(s) collected document-wide for proximity matching".format(len(piles)))
output.print_md(
    "- Delete this file once you're done with it, and don't commit it — "
    "same caution as a real capture (PLANNING.md §2)."
)
