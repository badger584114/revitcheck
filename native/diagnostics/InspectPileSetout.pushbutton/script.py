#! python3
"""Dump everything needed to design pile-setout reconstruction/verification
from ground truth: full parameters on the setout origin + a few real piles,
the bearing TextNote(s), the live schedule's real field structure and a
sample of rows, and whether Revit's own ProjectLocation transform already
gives real Easting/Northing.

ONE-OFF DIAGNOSTIC — not part of the frozen RevitCheck extension
(PLANNING.md §12: pyRevit stays "no new buttons, no growing surface").
Copy this pushbutton folder into a scratch/local extension on the Revit
machine, run it once, take the JSON away, then delete it — do not commit
it back into extensions/RevitCheck.extension.

PLANNING.md §14 Track B's diagnostic-and-rerun loop on
`InspectDimensionGeometry.pushbutton` was paused (2026-08-25, run 7) after
finding that pile setout on this project is drafted tag-to-tag against a
schedule, not tag-to-model-geometry — a different problem from that
diagnostic's `Face.Project` work, and the same problem PLANNING.md §5b's
`extraction/setout_reconstruction.py` already solved once for the PDF/DWG
pipeline (same real sheet number, 2873041, confirmed by the user). This
diagnostic exists to answer the real-data questions that port needs before
any parsing/comparison logic gets written blind:

1. What parameters does the setout-point origin annotation (family type
   `Coordinate_Survey (m)_2.5`, confirmed by the user) actually carry —
   which one holds its real Easting/Northing?
2. What parameters does a real Pile element carry — its own tag ID
   (`DIT_SiteID`, confirmed by the user, though this varies by client),
   and whatever field(s) the live schedule is actually reading?
3. What does the bearing TextNote's real text look like (confirmed to
   contain a literal `°`) — exact format, one note or two stacked?
4. What does the live pile schedule's `ScheduleDefinition` actually list
   as fields, and what do a few real rows say?
5. Does `ProjectLocation.GetProjectPosition(point)` already give real
   Easting/Northing matching the origin annotation's own stated value —
   if the model's Survey Point (the `BasePoint` with `IsShared = true`,
   which is what carries real E/N and the Angle-to-True-North rotation —
   the Project Base Point is purely internal) is correctly configured,
   `GetProjectPosition` is documented to return the same answer as
   Revit's own "Report Shared Coordinates" command, already fully
   rotated/translated — no need to hand-apply the Angle. That would let
   model-vs-schedule comparison skip the whole bearing/dimension-chain
   transform entirely; if the Survey Point isn't configured (or is
   wrong), `GetProjectPosition` returns a confident-looking wrong answer,
   not an error, which is exactly why this cross-checks it against the
   origin annotation's own independently-known-real value rather than
   trusting it blind.

**How to run it:** open the sheet/view containing the pile setout
(`DRG-2873041 - PILE LAYOUT` on this project), select the spot-coordinate
origin annotation plus 2-3 real piles (include at least one you can
independently check against the drawing), then run this button. It also
sweeps the active view's TextNotes for bearing candidates and the whole
document's ViewSchedules for anything pile-related — no need to select
those.

The output contains real client coordinates, parameter values, and
schedule data. Treat it the way this project treats a capture (PLANNING.md
§2): check before it leaves this machine, and do not commit it to git.
"""

import math
import os

from pyrevit import revit, script

from Autodesk.Revit.DB import (
    ElementId,
    FamilyInstance,
    FilteredElementCollector,
    SectionType,
    StorageType,
    TextNote,
    ViewSchedule,
)

output = script.get_output()
output.set_title("Inspect Pile Setout (diagnostic)")

doc = revit.doc
uidoc = revit.uidoc

MM_PER_FOOT = 304.8


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


# Same parameter-dump logic as InspectElements.pushbutton - duplicated
# rather than imported, same reasoning that script's own docstring gives:
# a throwaway script copied ad hoc into a scratch extension shouldn't
# depend on anything but itself.
def _param_value(param):
    storage = param.StorageType
    entry = {
        "storage_type": str(storage),
        "display_string": None,
        "raw_string": None,
        "numeric_value": None,
        "integer_value": None,
        "element_id_value": None,
    }
    try:
        entry["display_string"] = param.AsValueString()
    except Exception as exc:  # noqa: BLE001 - diagnostic, keep going
        entry["display_string"] = "<error: {0}>".format(exc)

    try:
        if storage == StorageType.String:
            entry["raw_string"] = param.AsString()
        elif storage == StorageType.Double:
            entry["numeric_value"] = param.AsDouble()
        elif storage == StorageType.Integer:
            entry["integer_value"] = param.AsInteger()
        elif storage == StorageType.ElementId:
            pid = param.AsElementId()
            entry["element_id_value"] = _eid(pid) if pid is not None else None
    except Exception as exc:  # noqa: BLE001
        entry["raw_string"] = "<error reading value: {0}>".format(exc)

    return entry


def _params_dict(element):
    params = {}
    for param in element.Parameters:
        try:
            name = param.Definition.Name
        except Exception:  # noqa: BLE001
            continue
        params[name] = _param_value(param)
    return params


def _describe_element(element):
    entry = {
        "element_id": _eid(element.Id),
        "unique_id": None,
        "class_name": type(element).__name__,
        "category": None,
        "family_name": None,
        "type_name": None,
        "location_point": None,
        "instance_parameters": {},
        "type_parameters": {},
        "project_position": None,
    }
    errors = []

    try:
        entry["unique_id"] = element.UniqueId
    except Exception as exc:  # noqa: BLE001
        errors.append("unique_id: {0}".format(exc))

    try:
        if element.Category is not None:
            entry["category"] = element.Category.Name
    except Exception as exc:  # noqa: BLE001
        errors.append("category: {0}".format(exc))

    try:
        if isinstance(element, FamilyInstance):
            entry["family_name"] = element.Symbol.Family.Name
            entry["type_name"] = element.Symbol.Name
    except Exception as exc:  # noqa: BLE001
        errors.append("family/type name: {0}".format(exc))

    raw_point = None
    try:
        location = element.Location
        raw_point = getattr(location, "Point", None)
        entry["location_point"] = _point(raw_point)
    except Exception as exc:  # noqa: BLE001
        errors.append("location_point: {0}".format(exc))

    try:
        entry["instance_parameters"] = _params_dict(element)
    except Exception as exc:  # noqa: BLE001
        errors.append("instance_parameters: {0}".format(exc))

    try:
        type_id = element.GetTypeId()
        if type_id is not None and type_id != ElementId.InvalidElementId:
            type_element = doc.GetElement(type_id)
            if type_element is not None:
                entry["type_parameters"] = _params_dict(type_element)
    except Exception as exc:  # noqa: BLE001
        errors.append("type_parameters: {0}".format(exc))

    # Question 5 - does Revit's own configured survey transform already
    # give real Easting/Northing for this element's position?
    if raw_point is not None:
        try:
            location = doc.ActiveProjectLocation
            position = location.GetProjectPosition(raw_point)
            entry["project_position"] = {
                "east_west_mm": _mm(position.EastWest),
                "north_south_mm": _mm(position.NorthSouth),
                "elevation_mm": _mm(position.Elevation),
                "angle_degrees": math.degrees(position.Angle),
            }
        except Exception as exc:  # noqa: BLE001
            errors.append("project_position: {0}".format(exc))

    if errors:
        entry["_errors"] = errors
    return entry


def _describe_bearing_textnotes(view):
    """Every TextNote in the active view containing a literal '°' -
    confirmed real for this project's bearing notes. Doesn't try to
    identify which is a "BEARING" label vs the DMS value itself, or
    whether they're one note or two - same reasoning
    extraction/setout_reconstruction.py's own bearing-matching already
    used: match the value pattern directly, don't depend on a label."""
    results = []
    try:
        collector = FilteredElementCollector(doc, view.Id).OfClass(TextNote)
    except Exception as exc:  # noqa: BLE001
        return {"_error": str(exc), "notes": []}

    for note in collector:
        try:
            text = note.Text
        except Exception:  # noqa: BLE001
            continue
        if "°" not in text:
            continue
        entry = {"element_id": _eid(note.Id), "text": text}
        try:
            coord = note.Coord
            entry["position"] = _point(coord)
        except Exception:  # noqa: BLE001
            entry["position"] = None
        results.append(entry)
    return {"notes": results}


def _describe_schedules():
    """Every ViewSchedule in the document, with field names for all of
    them (cheap) and a sample of real cell text for ones whose name
    suggests they're pile-related (more expensive, and only useful for
    the real candidate)."""
    results = []
    try:
        collector = FilteredElementCollector(doc).OfClass(ViewSchedule)
    except Exception as exc:  # noqa: BLE001
        return {"_error": str(exc), "schedules": []}

    for schedule in collector:
        entry = {"element_id": _eid(schedule.Id), "name": None, "fields": [], "sample_rows": None}
        try:
            entry["name"] = schedule.Name
        except Exception:  # noqa: BLE001
            pass

        try:
            definition = schedule.Definition
            field_count = definition.GetFieldCount()
            for i in range(field_count):
                field = definition.GetField(i)
                try:
                    heading = field.ColumnHeading
                except Exception:  # noqa: BLE001
                    heading = None
                try:
                    name = field.GetName()
                except Exception:  # noqa: BLE001
                    name = None
                entry["fields"].append({"name": name, "column_heading": heading})
        except Exception as exc:  # noqa: BLE001
            entry.setdefault("_errors", []).append("fields: {0}".format(exc))

        is_pile_related = entry["name"] is not None and "PILE" in entry["name"].upper()
        if is_pile_related:
            try:
                table_data = schedule.GetTableData()
                section = table_data.GetSectionData(SectionType.Body)
                rows = min(section.NumberOfRows, 20)
                cols = section.NumberOfColumns
                sample = []
                for r in range(rows):
                    row_cells = []
                    for c in range(cols):
                        try:
                            row_cells.append(schedule.GetCellText(SectionType.Body, r, c))
                        except Exception as exc:  # noqa: BLE001
                            row_cells.append("<error: {0}>".format(exc))
                    sample.append(row_cells)
                entry["sample_rows"] = sample
                entry["total_rows"] = section.NumberOfRows
            except Exception as exc:  # noqa: BLE001
                entry.setdefault("_errors", []).append("sample_rows: {0}".format(exc))

        results.append(entry)
    return {"schedules": results}


selection_ids = list(uidoc.Selection.GetElementIds())
if not selection_ids:
    output.print_md(
        "### Nothing selected\n\nSelect the setout-point origin annotation "
        "plus 2-3 real piles first, then run this again."
    )
    script.exit()

selected_elements = [_describe_element(doc.GetElement(eid)) for eid in selection_ids]

active_view = doc.ActiveView
bearing_notes = _describe_bearing_textnotes(active_view) if active_view is not None else {"notes": [], "_error": "no active view"}
schedules = _describe_schedules()

default_name = "{0}.inspect_pile_setout.json".format(
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
    json.dump(
        {
            "selected_elements": selected_elements,
            "bearing_textnotes": bearing_notes,
            "schedules": schedules,
        },
        f,
        indent=2,
        sort_keys=True,
    )

output.print_md("### Inspection written")
output.print_md("`{0}`".format(path))
output.print_md("")
output.print_md("- {0} selected element(s) with full parameters".format(len(selected_elements)))
output.print_md("- {0} bearing TextNote(s) found in the active view".format(len(bearing_notes.get("notes", []))))
output.print_md("- {0} ViewSchedule(s) found in the document".format(len(schedules.get("schedules", []))))
output.print_md(
    "- Delete this file once you're done with it, and don't commit it — "
    "same caution as a real capture (PLANNING.md §2)."
)
