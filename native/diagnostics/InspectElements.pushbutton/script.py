#! python3
"""Dump the current selection's full identity + parameter set to JSON.

ONE-OFF DIAGNOSTIC — not part of the frozen RevitCheck extension
(PLANNING.md §12: pyRevit stays "no new buttons, no growing surface").
Copy this pushbutton folder into a scratch/local extension on the Revit
machine, run it once, take the JSON away, then delete it — do not commit
it back into extensions/RevitCheck.extension.

Exists to answer three questions the native add-in's metadata adapter is
blocked on, that are much better answered from ground truth than from a
description in words:

1. What Revit parameter name is actually used as the cross-tool join key.
2. How a nested sub-component (e.g. a fixing bracket nested in a concrete
   panel, needing independent metadata rather than inherited) actually
   shows up via the API — SuperComponent / GetSubComponentIds(), whether
   it has its own ElementId and independently editable parameters.
3. Which categories/classes actually carry the tracked parameters, so the
   adapter's collection sweep is scoped correctly rather than guessed.

Select representative elements before running this — including at least
one instance of a nested-component case — then run it. Nested
sub-components of anything selected are walked automatically, so you
only need to select the host.

Both instance parameters (element.Parameters) and type parameters
(the element's type/symbol, where one exists) are captured and kept
separate, since a tracked field could live on either and the current
adapter design (ElementMetadata.Parameters) hasn't had to distinguish
them yet — this diagnostic is also what settles that.

The output contains real client parameter values. Treat it the way this
project treats a capture (PLANNING.md §2): check before it leaves this
machine, and do not commit it to git.
"""

import os

from pyrevit import revit, script

from Autodesk.Revit.DB import Element, ElementId, FamilyInstance, StorageType

output = script.get_output()
output.set_title("Inspect Elements (diagnostic)")

doc = revit.doc
uidoc = revit.uidoc


def _eid(element_id):
    """Version-safe ElementId -> int (Revit 2024+ uses .Value; older uses .IntegerValue)."""
    try:
        return element_id.Value
    except AttributeError:
        return element_id.IntegerValue


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
    except Exception as exc:  # noqa: BLE001 - diagnostic, keep going
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


def _describe(element, host_id=None):
    errors = []
    entry = {
        "element_id": _eid(element.Id),
        "unique_id": None,
        "host_element_id": host_id,
        "class_name": type(element).__name__,
        "category": None,
        "builtin_category": None,
        "family_name": None,
        "type_name": None,
        "has_sub_components": False,
        "instance_parameters": {},
        "type_parameters": {},
    }

    try:
        entry["unique_id"] = element.UniqueId
    except Exception as exc:  # noqa: BLE001
        errors.append("unique_id: {0}".format(exc))

    try:
        if element.Category is not None:
            entry["category"] = element.Category.Name
            entry["builtin_category"] = _eid(element.Category.Id)
    except Exception as exc:  # noqa: BLE001
        errors.append("category: {0}".format(exc))

    try:
        if isinstance(element, FamilyInstance):
            entry["family_name"] = element.Symbol.Family.Name
            entry["type_name"] = element.Symbol.Name
    except Exception as exc:  # noqa: BLE001
        errors.append("family/type name: {0}".format(exc))

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

    sub_ids = []
    try:
        if isinstance(element, FamilyInstance):
            sub_ids = list(element.GetSubComponentIds() or [])
            entry["has_sub_components"] = bool(sub_ids)
    except Exception as exc:  # noqa: BLE001
        errors.append("sub_components: {0}".format(exc))

    if errors:
        entry["_errors"] = errors

    return entry, sub_ids


selection_ids = list(uidoc.Selection.GetElementIds())
if not selection_ids:
    output.print_md(
        "### Nothing selected\n\nSelect one or more elements first — "
        "including at least one nested-component case — then run this again."
    )
    script.exit()

results = []
seen = set()
queue = [(doc.GetElement(eid), None) for eid in selection_ids]

while queue:
    element, host_id = queue.pop(0)
    if element is None:
        continue
    key = _eid(element.Id)
    if key in seen:
        continue
    seen.add(key)

    entry, sub_ids = _describe(element, host_id=host_id)
    results.append(entry)

    for sub_id in sub_ids:
        sub_element = doc.GetElement(sub_id)
        if sub_element is not None:
            queue.append((sub_element, key))


default_name = "{0}.inspect_elements.json".format(
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
    json.dump({"elements": results}, f, indent=2, sort_keys=True)

output.print_md("### Inspection written")
output.print_md("`{0}`".format(path))
output.print_md("")
output.print_md(
    "- {0} element(s) captured ({1} selected directly, {2} nested "
    "sub-component(s) walked automatically)".format(
        len(results),
        len(selection_ids),
        len(results) - len(selection_ids),
    )
)
output.print_md(
    "- Delete this file once you're done with it, and don't commit it — "
    "same caution as a real capture (PLANNING.md §2)."
)
