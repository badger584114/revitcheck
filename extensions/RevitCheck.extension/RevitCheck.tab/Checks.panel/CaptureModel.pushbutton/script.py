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
from revitcheck.adapters.revit_source import list_worksets, read_model

output = script.get_output()
output.set_title("Capture Model")

doc = revit.doc


def _ask_which_worksets(worksets):
    """A workset multi-select, for a workshared model.

    `worksets` is the `(id, name)` list from `revit_source.list_worksets`
    — already empty when the model isn't workshared, so this is only
    called when there is something to ask about. Returns the set of
    names to keep, or None to mean "keep everything" — both when the
    user leaves every box checked and when the dialog can't be shown, so
    a picker that fails closed never silently produces a narrower
    capture than the user asked for.

    WinForms rather than the WPF `SaveFileDialog` used below: a
    `CheckedListBox` is one control with built-in check state, versus
    assembling a checkbox stack by hand in WPF for the same result.
    Reached through pythonnet exactly the same way — Revit hosts both
    toolkits already, so this needs no extra dependency either.
    """
    try:
        import clr

        clr.AddReference("System.Windows.Forms")
        clr.AddReference("System.Drawing")
        from System.Drawing import Point, Size
        from System.Windows.Forms import (
            Button,
            CheckedListBox,
            DialogResult,
            Form,
            FormStartPosition,
            Label,
        )

        form = Form()
        form.Text = "Capture Model — worksets to include"
        form.StartPosition = FormStartPosition.CenterScreen
        form.Width = 420
        form.Height = 460

        label = Label()
        label.Text = (
            "Every dimension and view on an unchecked workset is skipped "
            "entirely — not read, not checked, not in the capture."
        )
        label.Location = Point(10, 10)
        label.Size = Size(380, 40)
        form.Controls.Add(label)

        box = CheckedListBox()
        box.Location = Point(10, 55)
        box.Size = Size(380, 300)
        box.CheckOnClick = True
        for _workset_id, name in worksets:
            box.Items.Add(name, True)  # every workset starts checked
        form.Controls.Add(box)

        ok = Button()
        ok.Text = "Capture checked worksets"
        ok.Location = Point(10, 365)
        ok.Width = 220
        ok.DialogResult = DialogResult.OK
        form.Controls.Add(ok)
        form.AcceptButton = ok

        result = form.ShowDialog()
        if result != DialogResult.OK:
            return None
        checked = set(box.CheckedItems)
        return checked if len(checked) < len(worksets) else None
    except Exception as exc:  # noqa: BLE001 - fall back, don't lose the read
        output.print_md(
            "> Could not open the workset picker (`{0}`), so every workset "
            "was captured.".format(exc)
        )
        return None


worksets = list_worksets(doc)
include_worksets = _ask_which_worksets(worksets) if worksets else None

model = read_model(doc, include_worksets=include_worksets)

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
if model.sheets and not model.dimensions:
    # Happened for real, 2026-08-22: a workset deselection wiped every
    # view (and therefore every dimension) from a capture that still
    # looked superficially fine -- 134 sheets, seemingly normal output
    # -- and it went to GitHub before anyone noticed. Sheets are never
    # workset-filtered, so a capture with sheets but nothing else is a
    # near-certain sign something upstream (usually the workset
    # selection) excluded everything, not that the model is genuinely
    # empty.
    output.print_md(
        "- **⚠ 0 dimensions captured despite {0} sheet(s).** This "
        "usually means the workset selection excluded everything with "
        "content in it. Check your selection before using this "
        "capture.".format(len(model.sheets))
    )
if model.excluded_worksets:
    output.print_md(
        "- **{0} workset(s) excluded** by your selection — nothing on them "
        "was read: {1}".format(
            len(model.excluded_worksets), ", ".join(model.excluded_worksets)
        )
    )
if model.extraction_errors:
    output.print_md(
        "- **{0} element(s) could not be read** — they are listed in the "
        "capture's `extraction_errors` and reported by "
        "`revit.capture_coverage`.".format(len(model.extraction_errors))
    )
