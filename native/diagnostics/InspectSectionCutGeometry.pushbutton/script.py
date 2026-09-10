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

1. **Answered, negatively, by the first real run (2026-09-11) — and the
   probe was rewritten because of it.** Across two real sections, 15
   references: 9 `AnnotationSymbol` (detail components), 5 `FilledRegion`
   (2D hatch), **1** `DetailLine`. So "a drafted dimension references a
   detail line" was simply wrong, no witness pair ever resolved, and
   questions 2-4 were never reached. The anchor exists regardless, just
   held per class - `Location.Point`, `Location.Curve`, `GetBoundaries()` -
   and this now tries each and records which answered, so the next run
   says how the real population splits.
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

Read `anchor_separation_mm` before any of the face results: it is the
distance between the two witness anchors themselves. If that does not
already match the dimension's own measured value, the anchors are not
where the dimension measures and nothing built on them can mean anything -
which is a cheaper way to find out than reading face projections.

Nothing here judges — it dumps. Every number needed to decide is in the
output, including the runners-up, so the decision is made from data
rather than from whether the idea sounded right.
"""

import json
import os

import clr

from pyrevit import revit, script

from Autodesk.Revit.DB import (
    BoundingBoxIntersectsFilter,
    Dimension,
    IntersectionResultArray,
    Line,
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


def cut_line_hits(anchor_xyz, axis_xyz, reach_mm=3000.0):
    """Where real model faces cross a line lying IN the cut plane.

    **This is what the user actually described** — "get faces points on the
    faces at the cut plane of the section" — and what the first two runs did
    not do. `Face.Project` finds the nearest point *anywhere* on a face, so
    it is free to wander in the view direction: the 2026-09-11 run's chosen
    points sat 50-400mm out of the section plane, and the resulting distance
    error was almost exactly how much the two ends differed. Flattening those
    points back onto the plane afterwards barely helped (21.1mm -> 20.3mm
    mean), because a point 400mm along the wrong part of a face does not
    become right by being projected.

    Intersecting with a line through the anchor, along the direction the
    dimension measures, fixes both halves at once: the hit is on the section
    by construction, and only faces the measurement actually crosses can
    take part - which is a far better face filter than "nearest".
    """
    if axis_xyz is None:
        return [], []

    reach_ft = reach_mm / MM_PER_FOOT
    try:
        start = XYZ(
            anchor_xyz.X - axis_xyz.X * reach_ft,
            anchor_xyz.Y - axis_xyz.Y * reach_ft,
            anchor_xyz.Z - axis_xyz.Z * reach_ft,
        )
        end = XYZ(
            anchor_xyz.X + axis_xyz.X * reach_ft,
            anchor_xyz.Y + axis_xyz.Y * reach_ft,
            anchor_xyz.Z + axis_xyz.Z * reach_ft,
        )
        probe_line = Line.CreateBound(start, end)
    except Exception as exc:  # noqa: BLE001
        return [], [{"error": "probe line: {0}".format(exc)}]

    hits = []
    errors = []
    tally = {}
    faces_tried = 0

    for element in nearby_elements(anchor_xyz):
        solids, error = solids_of(element)
        if error:
            errors.append({"element_id": eid(element.Id), "error": error})
            continue

        try:
            category = element.Category.Name if element.Category else None
        except Exception:  # noqa: BLE001
            category = None

        for solid in solids:
            for face in solid.Faces:
                faces_tried += 1
                results = clr_out_intersect(face, probe_line, tally)

                for hit in results:
                    hits.append(
                        {
                            "element_id": eid(element.Id),
                            "category": category,
                            "point": point(hit),
                            "distance_from_anchor_mm": distance_mm(anchor_xyz, hit),
                        }
                    )

    hits.sort(key=lambda h: h["distance_from_anchor_mm"])
    # Always reported, hit or miss: "0 hits from 84 faces, all Disjoint" is
    # a real answer and "0 hits, nothing attempted" is a bug, and the last
    # run could not tell them apart.
    errors.append({"faces_tried": faces_tried, "outcomes": tally})
    return hits[:FACE_CANDIDATES], errors


def clr_out_intersect(face, curve, tally):
    """Face.Intersect(Curve, out IntersectionResultArray) - the real overload.

    **Fixed 2026-09-11, after a whole run produced zero hits and zero
    errors.** The previous version called ``face.Intersect(curve)`` and
    hoped the binding would hand the ``out`` parameter back as a tuple.
    ``Face.Intersect`` has *two* overloads, and a one-argument call
    resolves to the one that returns a bare ``SetComparisonResult`` - not
    iterable, so every face fell into a ``TypeError`` branch that returned
    empty. 170 nearest-face candidates were found on the same run, so the
    geometry walk was never the problem; the intersection simply never
    happened, and said nothing about it.

    That is this project's own most-repeated failure - a confident empty
    answer - committed inside the probe built to avoid guessing. Hence
    ``tally``: every outcome is counted, so a zero can never again be
    indistinguishable from "not attempted".
    """
    array = None
    try:
        holder = clr.Reference[IntersectionResultArray]()
        outcome = face.Intersect(curve, holder)
        array = holder.Value
        tally[str(outcome)] = tally.get(str(outcome), 0) + 1
    except Exception as exc:  # noqa: BLE001
        # Older/IronPython bindings return the out parameter as a tuple.
        try:
            outcome = face.Intersect(curve)
            if isinstance(outcome, tuple) and len(outcome) > 1:
                array = outcome[1]
                tally[str(outcome[0])] = tally.get(str(outcome[0]), 0) + 1
            else:
                tally["no_out_parameter"] = tally.get("no_out_parameter", 0) + 1
        except Exception as inner:  # noqa: BLE001
            key = "error: {0}".format(inner)[:120]
            tally[key] = tally.get(key, 0) + 1
            return []

    points = []
    if array is None:
        tally["null_array"] = tally.get("null_array", 0) + 1
        return points

    try:
        for item in array:
            points.append(item.XYZPoint)
    except Exception as exc:  # noqa: BLE001
        key = "unreadable array: {0}".format(exc)[:120]
        tally[key] = tally.get(key, 0) + 1

    return points


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


def witness_anchor(reference_element):
    """A single real point on whatever the drafter snapped the dimension to.

    **Rewritten 2026-09-11, after the first real run of this probe.** It
    originally looked only for ``Location.Curve``, on the assumption that a
    drafted dimension references a DetailLine. Two real sections said
    otherwise: of 15 references, 9 were ``AnnotationSymbol`` (detail
    components), 5 were ``FilledRegion`` (2D hatch), and exactly **one** was
    a DetailLine. So the probe resolved no witness pair at all and never
    reached the questions it exists to answer.

    That is the assumption dying cheaply, which is the point of a probe -
    but the anchor is still there, just held on a different property per
    class. This asks each in turn and records which one answered, so the
    next run says how the real population actually splits:

    - ``Location.Point`` - an AnnotationSymbol/FamilyInstance's insertion
      point. Already the anchor pile tags use, and the largest group here.
    - ``Location.Curve`` - a DetailLine, the original assumption.
    - ``GetBoundaries()`` - a FilledRegion's own boundary loops; its
      centroid is the least arbitrary single point on a 2D region.
    """
    try:
        location = reference_element.Location
    except Exception:  # noqa: BLE001
        location = None

    try:
        curve = getattr(location, "Curve", None)
        if curve is not None and curve.IsBound:
            a = curve.GetEndPoint(0)
            b = curve.GetEndPoint(1)
            mid = XYZ((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, (a.Z + b.Z) / 2.0)
            return {"xyz": mid, "from": "location_curve",
                    "curve_start": point(a), "curve_end": point(b)}
    except Exception:  # noqa: BLE001
        pass

    try:
        pt = getattr(location, "Point", None)
        if pt is not None:
            return {"xyz": pt, "from": "location_point"}
    except Exception:  # noqa: BLE001
        pass

    # A filled region has no Location at all - its geometry is its
    # boundary loops. The centroid was tried first and failed 0 for 4 on
    # the 2026-09-11 run (270mm, 578mm, 910mm and 1.27km out), for an
    # obvious reason once seen: a dimension measures to a region's EDGE,
    # not its middle. Every boundary vertex is returned instead, and the
    # caller picks the one nearest the other end of the dimension - which
    # is what "measures to the near edge" means, and is checkable rather
    # than assumed.
    try:
        vertices = []
        for loop in reference_element.GetBoundaries():
            for edge in loop:
                vertices.append(edge.GetEndPoint(0))
        if vertices:
            return {
                "xyz": vertices[0],
                "from": "filled_region_boundary",
                "candidates": vertices,
            }
    except Exception:  # noqa: BLE001
        pass

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

        anchor = witness_anchor(element) if element else None
        if anchor:
            described["anchor_from"] = anchor["from"]
            described["anchor_point"] = point(anchor["xyz"])
            described["curve_start"] = anchor.get("curve_start")
            described["curve_end"] = anchor.get("curve_end")
            # Kept so a filled region's anchor, which can only be resolved
            # once both ends are known, is corrected here too rather than
            # leaving the reference entry disagreeing with the probe.
            anchor["described"] = described
            witnesses.append(anchor)
        else:
            described["anchor_from"] = None
            described["anchor_point"] = None

        entry["references"].append(described)

    # Question 3/4: real faces near each witness line, and what distance
    # they imply between the two of them.
    reference_ids = [r.get("element_id") for r in entry["references"] if r.get("element_id") is not None]
    same_element = len(reference_ids) >= 2 and len(set(reference_ids)) == 1

    if same_element:
        # 5 of 23 dimensions on the 2026-09-11 run measure between two
        # features of ONE element (all of them the same filled region).
        # Both anchors then resolve to the same point and the separation
        # comes out 0.00 against a real measured 2500mm - a fabricated
        # answer, which is worse than none.
        #
        # There is no non-circular way to pick the right pair yet: choosing
        # the two boundary vertices whose separation matches the stated
        # value would be fitting to the answer, which is exactly what
        # SpotElevationConsistencyCheck refuses to do when it declines to
        # pick "whichever face agrees". Recorded as an unhandled shape
        # instead, with the vertices dumped so a rule can be found from
        # data rather than invented.
        entry["same_element_both_ends"] = reference_ids[0]
        entry["unhandled_shape"] = (
            "both references resolve to one element - measures between two features of it, "
            "and nothing here can say which two"
        )
        entry["boundary_candidates"] = [
            point(c) for w in witnesses for c in (w.get("candidates") or [])
        ][:40]
        results.append(entry)
        continue

    if len(witnesses) == 2:
        # A filled region has no single anchor - resolve it against the
        # other end, since a dimension measures to the near edge. Done
        # here rather than in witness_anchor because it needs both ends.
        for index, witness in enumerate(witnesses):
            candidates = witness.get("candidates")
            if not candidates:
                continue
            other = witnesses[1 - index]["xyz"]
            nearest = min(candidates, key=lambda c: c.DistanceTo(other))
            witness["xyz"] = nearest
            witness["candidate_count"] = len(candidates)
            if witness.get("described") is not None:
                witness["described"]["anchor_point"] = point(nearest)
                witness["described"]["boundary_candidate_count"] = len(candidates)

        # The direction the dimension measures, which lies in the cut plane
        # because both anchors do. Used to aim the cut-line probe.
        a0 = witnesses[0]["xyz"]
        a1 = witnesses[1]["xyz"]
        span = a1 - a0
        axis = span.Normalize() if span.GetLength() > 1e-9 else None

        probes = []
        for witness in witnesses:
            faces, errors = project_faces(witness["xyz"])
            hits, hit_errors = cut_line_hits(witness["xyz"], axis)
            probes.append({
                "anchor": point(witness["xyz"]),
                "anchor_from": witness["from"],
                "faces": faces,
                "cut_line_hits": hits,
                "errors": errors + hit_errors,
            })

        entry["witness_probes"] = probes

        # The cut-line answer, reported ALONGSIDE the nearest-face one
        # rather than replacing it - the point of this run is to compare
        # them on the same dimensions, not to swap one guess for another.
        if probes[0]["cut_line_hits"] and probes[1]["cut_line_hits"]:
            h0 = probes[0]["cut_line_hits"][0]["point"]
            h1 = probes[1]["cut_line_hits"][0]["point"]
            cut_distance = (
                (h0["x"] - h1["x"]) ** 2 + (h0["y"] - h1["y"]) ** 2 + (h0["z"] - h1["z"]) ** 2
            ) ** 0.5
            entry["model_distance_along_cut_line_mm"] = cut_distance
            measured = entry["segments"][0]["measured_mm"] if entry["segments"] else None
            if measured is not None:
                entry["delta_cut_line_vs_measured_mm"] = abs(cut_distance - measured)

        # The drafted separation, straight from the two anchors. If this
        # already matches the stated value the anchors are the right ones,
        # whatever the model then says - and if it does not, no comparison
        # built on them can mean anything.
        entry["anchor_separation_mm"] = distance_mm(witnesses[0]["xyz"], witnesses[1]["xyz"])

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
    "- {0} had two resolvable witness anchors".format(
        sum(1 for r in results if r.get("witness_probes"))
    )
)

anchor_kinds = {}
for r in results:
    for ref in r.get("references") or []:
        key = ref.get("anchor_from") or "NONE"
        anchor_kinds[key] = anchor_kinds.get(key, 0) + 1
output.print_md(
    "- anchors by source: {0}".format(
        ", ".join("{0} {1}".format(v, k) for k, v in sorted(anchor_kinds.items()))
    )
)
output.print_md(
    "- {0} produced a model distance to compare".format(
        sum(1 for r in results if "model_distance_between_nearest_faces_mm" in r)
    )
)
output.print_md("")
output.print_md(
    "- {0} produced a cut-line distance".format(
        sum(1 for r in results if "model_distance_along_cut_line_mm" in r)
    )
)
output.print_md(
    "- {0} measure between two features of ONE element (unhandled shape)".format(
        sum(1 for r in results if r.get("same_element_both_ends"))
    )
)

faces_tried = 0
for r in results:
    for probe in r.get("witness_probes") or []:
        for e in probe.get("errors") or []:
            faces_tried += e.get("faces_tried", 0) if isinstance(e, dict) else 0
output.print_md("- {0} face(s) were actually intersected with a cut line".format(faces_tried))
output.print_md("")
output.print_md(
    "**Compare `delta_cut_line_vs_measured_mm` against `delta_vs_measured_mm`** "
    "— the first takes points where model faces cross a line lying IN the cut "
    "plane, the second takes the nearest point anywhere on a face. On the "
    "2026-09-11 run the second averaged 21mm error because its points sat "
    "50-400mm out of the section. If the first is materially better, that is "
    "the check."
)
output.print_md("")
output.print_md(
    "**Read `anchor_separation_mm` first** — that is the whole question. Then "
    "read the runners-up in `witness_probes[].faces`: if the second face is "
    "about as close as the first, the pick is a coin-flip and a check built "
    "on it would be too."
)
output.print_md(
    "- Delete this file once you're done with it, and don't commit it — "
    "same caution as a real capture (PLANNING.md §2)."
)
