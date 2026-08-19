#! python3
"""Dump the extracted IR to JSON, for developing checks off a Revit machine.

Run this once on a real project, take the JSON away, and every rule can
then be written and tested anywhere — the same loop this project already
uses with committed PDF and DXF samples, and the reason the checks are
not stuck being debugged only where Revit runs.

The capture contains sheet numbers, view names and coordinates. Treat it
the way the rest of this project treats uploaded drawings (PLANNING.md
§2) and check before it leaves a machine or lands in git.
"""

import os

from pyrevit import revit, script

from revitcheck import capture
from revitcheck.adapters.revit_source import read_model

output = script.get_output()
output.set_title("Capture Model")

doc = revit.doc
model = read_model(doc)

default_name = "{0}.capture.json".format(
    os.path.splitext(os.path.basename(doc.Title or "model"))[0]
)


def _ask_where_to_save(suggested_name):
    """A save dialog that works on pyRevit's CPython engine.

    `pyrevit.forms` is IronPython-only — its module-level `__getattr__`
    raises `PyRevitCPythonNotSupported` for every name under CPython, so
    `forms.save_file()` cannot be used from a `#! python3` script. WPF's
    own dialog is already in-process (Revit is a WPF app) and reachable
    through pythonnet, so it needs no extra dependency.

    Returns None if the user cancels. Falls back to a default path if the
    dialog itself is unavailable, rather than losing a capture that may
    have taken a while to read — a capture written somewhere predictable
    beats no capture at all.
    """
    try:
        import clr

        clr.AddReference("PresentationFramework")
        from Microsoft.Win32 import SaveFileDialog

        dialog = SaveFileDialog()
        dialog.FileName = suggested_name
        dialog.DefaultExt = ".json"
        dialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        # WPF returns Nullable<bool>: True only on an explicit Save.
        return dialog.FileName if dialog.ShowDialog() else None
    except Exception as exc:  # noqa: BLE001 - fall back, don't lose the read
        fallback = os.path.join(
            os.path.expanduser("~"), "Documents", suggested_name
        )
        output.print_md(
            "> Could not open a save dialog (`{0}`), so the capture was "
            "written to the default location below.".format(exc)
        )
        return fallback


path = _ask_where_to_save(default_name)
if not path:
    script.exit()

capture.save(model, path)

output.print_md("### Capture written")
output.print_md("`{0}`".format(path))
output.print_md("")
output.print_md(
    "- {0} sheet(s), {1} view(s), {2} dimension(s)".format(
        len(model.sheets), len(model.views), len(model.dimensions)
    )
)
if model.extraction_errors:
    output.print_md(
        "- **{0} element(s) could not be read** — they are listed in the "
        "capture's `extraction_errors` and reported by "
        "`revit.capture_coverage`.".format(len(model.extraction_errors))
    )
