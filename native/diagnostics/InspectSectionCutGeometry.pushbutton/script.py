#! python3
"""Can a drafted section dimension be checked against the model by taking
points off model faces at the section's cut plane?

ONE-OFF DIAGNOSTIC — not part of the frozen RevitCheck extension
(PLANNING.md §12). Copy this pushbutton folder into a scratch/local
extension on the Revit machine, run it once, take the JSON away, then
delete it — do not commit the output, same caution as a real capture
(PLANNING.md §2).

**Why this exists, and why it is not a repeat of §14.** That diagnostic
asked whether a *dimension's own witness points* resolve to 3D positions,
and the answer was no seven times over: `Reference.GlobalPoint` null on
all 17 real references, `Location` returning (0,0,0) for real model
geometry, `Dimension.Curve` throwing on 41 of 46, and the one usable
anchor — `DimensionSegment.Origin` — being where the value *text* sits,
measured 527m from its own witness lines on a real case because drafters
drag text.

This asks a different question, per the user (2026-09-10): *"get the
sections with dimensions to detail lines, find the geometry, get faces
points on the faces at the cut plane of the section, compare the distance
to the dimension."* Two things make it a genuinely different bet:

1. **The referenced DetailLine has its own readable curve.**
   `DetailLine.GeometryCurve` / `Location.Curve` is real geometry, reached
   by resolving the element rather than asking the Reference for a point
   it never had. That is the same move that already works for a tag's
   `Location.Point` and for a pile. So the anchor here is the witness
   *line the drafter actually drew*, not the dimension's text.
2. **`Face.Project` from that anchor is a proven technique in this
   codebase** — it is exactly what `revitcheck.spot_elevation_consistency`
   uses, the one check that worked on its first real run, generalised from
   horizontal faces to any face.

**The real open questions this has to answer, before any check is
written:**

1. Do the two referenced detail lines resolve to real curves, and how
   often — is `Location.Curve` reliable here the way `GlobalPoint` was
   not?
2. Is there real model geometry near each witness line at all? §14's
   equivalent search found document-wide noise and, for the two dimensions
   with real model references, *zero* nearby elements — but it searched
   from the text origin, hundreds of metres away.
3. Does `Face.Project` from a witness line land on a sensible face, and is
   the nearest face the right one? Which face wins, and by how much does
   the runner-up differ — a 1mm gap means the pick is arbitrary.
4. **The payoff:** does the distance between the two projected points
   match what the dimension states? If it does on real data, the check is
   buildable exactly as described. If it does not, the numbers say why.

Nothing here judges — it dumps. Every number needed to decide is in the
output, including the runners-up, so the decision is made from data
rather than from whether the idea sounded right.
"""

import json
import os

from pyrevit import revit, script

from Autodesk.Revit.DB import (
    BoundingBoxIntersectsFilter,
    Dimension,
    FilteredElementCollector,
    GeometryInstance,
    Options,
    Outline,
    Solid,
    ViewDetailLevel,
    ViewSection,
    XYZ,
)

MM_PER_FOOT = 304.8

# How far around a witness line to look for model geometry. Generous on
# purpose: this is a diagnostic, and reporting "nothing within 2m" is a
# real answer worth having. A real check would make this configurable.
SEARCH_RADIUS_MM = 2000.0

# How many candidate faces to keep per witness line. More than one, so the
# output shows whether the nearest face wins clearly or by a hair - a
# margin this small is the difference between a reliable pick and a
# coin-flip, and it is exactly what a single "nearest" answer hides.
FACE_CANDIDATES = 5

output = script.get_output()
doc = revit.doc
view = doc.ActiveView


def mm(value):
    return value * MM_PER_FOOT


def point(xyz):
    if xyz is None:
        return None
    return {"x": mm(xyz.X), "y": mm(xyz.Y), "z": mm(xyz.Z)}


def eid(element_id):
    if element_id is None:
        return None
    try:
        return element_id.Value
    except AttributeError:
        return element_id.IntegerValue


def distance_mm(a, b):
    return mm(a.DistanceTo(b))


def solids_of(element):
    """Every real solid an element has, following nested family geometry.

    DetailLevel.Fine explicitly: the default (Coarse) collapses a
    parametric civil profile to a simplified block, which is how §18's
    first probe found 2 faces where a real abutment has several steps.
    """
    options = Options()
    options.DetailLevel = ViewDetailLevel.Fine
    options.ComputeReferences = True

    found = []

    def walk(geometry):
        if geometry is None:
            return
        for item in geometry:
            if isinstance(item, Solid):
                if item.Faces.Size > 0 and item.Volume > 0:
                    found.append(item)
            elif isinstance(item, GeometryInstance):
                walk(item.GetInstanceGeometry())

    try:
        walk(element.get_Geometry(options))
    except Exception as exc:  # noqa: BLE001 - a dump must not stop on one element
        return [], str(exc)

    return found, None


def nearby_elements(anchor_xyz):
    """Model elements whose bounding box is within SEARCH_RADIUS of a point."""
    radius_ft = SEARCH_RADIUS_MM / MM_PER_FOOT
    low = XYZ(anchor_xyz.X - radius_ft, anchor_xyz.Y - radius_ft, anchor_xyz.Z - radius_ft)
    high = XYZ(anchor_xyz.X + radius_ft, anchor_xyz.Y + radius_ft, anchor_xyz.Z + radius_ft)

    try:
        collector = (
            FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .WherePasses(BoundingBoxIntersectsFilter(Outline(low, high)))
        )
        # View-specific elements are the drafted linework itself - the
        # thing being checked, never the thing to check it against.
        return [e for e in collector if not e.ViewSpecific]
    except Exception as exc:  # noqa: BLE001
        return []


def project_faces(anchor_xyz):
    """The nearest real model faces to a witness line's endpoint.

    Face.Project gives the point on the face closest to the anchor, which
    is the "point on the face at the cut plane" this is testing - the
    anchor lies in the view's own plane, being a detail line drawn on it.
    """
    candidates = []
    errors = []

    for element in nearby_elements(anchor_xyz):
        solids, error = solids_of(element)
        if error:
            errors.append({"element_id": eid(element.Id), "error": error})
            continue

        category = None
        try:
            category = element.Category.Name if element.Category else None
        except Exception:  # noqa: BLE001
            category = None

        for solid in solids:
            for face in solid.Faces:
                try:
                    result = face.Project(anchor_xyz)
                except Exception:  # noqa: BLE001
                    continue

                if result is None:
                    continue

                projected = result.XYZPoint
                candidates.append(
                    {
                        "element_id": eid(element.Id),
                        "category": category,
                        "class_name": type(element).__name__,
                        "projected_point": point(projected),
                        "distance_from_witness_mm": distance_mm(anchor_xyz, projected),
                        "face_normal": point(face.ComputeNormal(result.UVPoint)),
                    }
                )

    candidates.sort(key=lambda c: c["distance_from_witness_mm"])
    return candidates[:FACE_CANDIDATES], errors


def witness_curve(reference_element):
    """A referenced element's own curve endpoints, when it has one."""
    try:
        location = reference_element.Location
        curve = getattr(location, "Curve", None)
        if curve is None or not curve.IsBound:
            return None
        return {
            "start": point(curve.GetEndPoint(0)),
            "end": point(curve.GetEndPoint(1)),
            "start_xyz": curve.GetEndPoint(0),
            "end_xyz": curve.GetEndPoint(1),
        }
    except Exception:  # noqa: BLE001
        return None


def stated_value(dimension):
    """What the drawing says, and what Revit measured, kept apart."""
    try:
        segments = list(dimension.Segments)
    except Exception:  # noqa: BLE001
        segments = []

    if segments:
        return [
            {
                "measured_mm": mm(s.Value) if s.Value is not None else None,
                "override": s.ValueOverride,
            }
            for s in segments
        ]

    try:
        return [
            {
                "measured_mm": mm(dimension.Value) if dimension.Value is not None else None,
                "override": dimension.ValueOverride,
            }
        ]
    except Exception:  # noqa: BLE001
        return []


# --- the view's own cut plane -------------------------------------------

if not isinstance(view, ViewSection):
    output.print_md("### Not a section view")
    output.print_md(
        "Active view is `{0}` ({1}). Open a real SECTION with drafted "
        "dimensions and run this again — the whole question is what the "
        "cut plane gives.".format(view.Name, type(view).__name__)
    )
    raise SystemExit

cut_plane = {
    "view_name": view.Name,
    "view_id": eid(view.Id),
    "view_direction": point(view.ViewDirection),
    "right_direction": point(view.RightDirection),
    "up_direction": point(view.UpDirection),
    "origin": point(view.Origin),
    "crop_box_active": view.CropBoxActive,
}

# --- the dimensions to look at ------------------------------------------

selection = [doc.GetElement(i) for i in revit.get_selection().element_ids]
dimensions = [e for e in selection if e.Category and "Dimension" in (e.Category.Name or "")]
source = "selection"

if not dimensions:
    # Collected PER VIEW, never document-wide: Dimension.OwnerViewId is
    # confirmed untrustworthy read across the document (CLAUDE.md), and a
    # view-specific element can only be selected while its owning view is
    # active, so the active view is the safe stand-in either way.
    dimensions = list(FilteredElementCollector(doc, view.Id).OfClass(Dimension))
    source = "every dimension in the active view"

results = []

for dimension in dimensions:
    try:
        references = list(dimension.References)
    except Exception as exc:  # noqa: BLE001
        results.append({"dimension_id": eid(dimension.Id), "error": str(exc)})
        continue

    entry = {
        "dimension_id": eid(dimension.Id),
        "reference_count": len(references),
        "segments": stated_value(dimension),
        "references": [],
    }

    witnesses = []
    for reference in references:
        try:
            element = doc.GetElement(reference)
        except Exception:  # noqa: BLE001
            element = None

        described = {
            "element_id": eid(element.Id) if element else None,
            "class_name": type(element).__name__ if element else None,
            "view_specific": element.ViewSpecific if element else None,
        }

        curve = witness_curve(element) if element else None
        if curve:
            described["curve_start"] = curve["start"]
            described["curve_end"] = curve["end"]
            witnesses.append(curve)
        else:
            described["curve_start"] = None
            described["curve_end"] = None

        entry["references"].append(described)

    # Question 3/4: real faces near each witness line, and what distance
    # they imply between the two of them.
    if len(witnesses) == 2:
        probes = []
        for witness in witnesses:
            # Midpoint of the witness line: a detail line is drawn along
            # the thing it measures to, so its middle is the least
            # arbitrary single anchor on it.
            mid = XYZ(
                (witness["start_xyz"].X + witness["end_xyz"].X) / 2.0,
                (witness["start_xyz"].Y + witness["end_xyz"].Y) / 2.0,
                (witness["start_xyz"].Z + witness["end_xyz"].Z) / 2.0,
            )
            faces, errors = project_faces(mid)
            probes.append({"anchor": point(mid), "faces": faces, "errors": errors})

        entry["witness_probes"] = probes

        if probes[0]["faces"] and probes[1]["faces"]:
            a = probes[0]["faces"][0]["projected_point"]
            b = probes[1]["faces"][0]["projected_point"]
            model_distance = (
                (a["x"] - b["x"]) ** 2 + (a["y"] - b["y"]) ** 2 + (a["z"] - b["z"]) ** 2
            ) ** 0.5
            entry["model_distance_between_nearest_faces_mm"] = model_distance

            measured = entry["segments"][0]["measured_mm"] if entry["segments"] else None
            if measured is not None:
                entry["delta_vs_measured_mm"] = abs(model_distance - measured)

    results.append(entry)

path = os.path.join(
    os.path.expanduser("~"),
    "Desktop",
    "{0}.inspect_section_cut_geometry.json".format(doc.Title),
)

with open(path, "w") as f:
    json.dump({"cut_plane": cut_plane, "source": source, "dimensions": results}, f, indent=2, sort_keys=True)

output.print_md("### Inspection written")
output.print_md("`{0}`".format(path))
output.print_md("")
output.print_md("- {0} dimension(s) examined, from {1}".format(len(results), source))
output.print_md(
    "- {0} had two resolvable witness curves".format(
        sum(1 for r in results if r.get("witness_probes"))
    )
)
output.print_md(
    "- {0} produced a model distance to compare".format(
        sum(1 for r in results if "model_distance_between_nearest_faces_mm" in r)
    )
)
output.print_md("")
output.print_md(
    "**Read `delta_vs_measured_mm` first** — that is the whole question. Then "
    "read the runners-up in `witness_probes[].faces`: if the second face is "
    "about as close as the first, the pick is a coin-flip and a check built "
    "on it would be too."
)
output.print_md(
    "- Delete this file once you're done with it, and don't commit it — "
    "same caution as a real capture (PLANNING.md §2)."
)
