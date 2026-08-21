#! python3
"""Run every check and export the results as BCF 2.1.

This is the output half of PLANNING.md §12's proof-of-concept round
trip: Revit -> BCF -> Forma -> (back to Revit, via the element anchor).
Thin on purpose, same reasoning as every other button here — get the
document, read it, run the rules, render the result. The one thing
particular to this button is picking where the file(s) go, and even
that reuses the same WinForms-via-pythonnet pattern `CaptureModel` and
`revit_source.list_worksets`'s picker already established, for the same
reason: `pyrevit.forms` is IronPython-only under CPython.
"""

import os

from pyrevit import revit, script

from Autodesk.Revit.DB import ElementId

import revitcheck.checks  # noqa: F401 - registers the rules
from revitcheck import RuleConfig, run_checks
from revitcheck.adapters.revit_source import read_model
from revitcheck.report import to_bcf, to_markdown

output = script.get_output()
output.set_title("Export BCF")

doc = revit.doc

# Read-only: no transaction is opened anywhere in the read path.
model = read_model(doc)
issues = run_checks(model, RuleConfig())

if not issues:
    output.print_md("No issues found — nothing to export.")
    script.exit()


def _ask_where_to_save():
    """A folder picker that works on pyRevit's CPython engine.

    A folder, not a file: the export can be more than one `.bcfzip`
    (Forma's 100-issue cap), and `bcf.to_bcf_files` already names each
    one — the only decision left for the user is which folder they land
    in. Falls back to a default location rather than losing a
    already-computed export if the dialog itself is unavailable.
    """
    try:
        import clr

        clr.AddReference("System.Windows.Forms")
        from System.Windows.Forms import DialogResult, FolderBrowserDialog

        dialog = FolderBrowserDialog()
        dialog.Description = "Choose a folder for the exported .bcfzip file(s)"
        return dialog.SelectedPath if dialog.ShowDialog() == DialogResult.OK else None
    except Exception as exc:  # noqa: BLE001 - fall back, don't lose the export
        fallback = os.path.join(os.path.expanduser("~"), "Documents")
        output.print_md(
            "> Could not open a folder picker (`{0}`), so the export was "
            "written to the default location below.".format(exc)
        )
        return fallback


directory = _ask_where_to_save()
if not directory:
    script.exit()

os.makedirs(directory, exist_ok=True)
bcf_files = to_bcf(issues, model_title=model.doc_title)

written = []
for filename, data in bcf_files:
    path = os.path.join(directory, filename)
    with open(path, "wb") as handle:
        handle.write(data)
    written.append(path)

output.print_md("### BCF export written")
output.print_md(
    "{0} issue(s) across {1} file(s), in `{2}`".format(
        len(issues), len(written), directory
    )
)
for path in written:
    output.print_md("- `{0}`".format(os.path.basename(path)))

with_anchor = sum(1 for i in issues if i.unique_id)
if with_anchor < len(issues):
    output.print_md("")
    output.print_md(
        "> {0} of {1} finding(s) have no `unique_id` and so no pinned "
        "Component in their viewpoint — the Topic still exports, just "
        "without an anchor back to the element. This is expected on a "
        "capture taken before `unique_id` was added to the adapter.".format(
            len(issues) - with_anchor, len(issues)
        )
    )

output.print_md("")
output.print_md(
    to_markdown(
        issues,
        model_title=model.doc_title,
        linkify=lambda eid: output.linkify(ElementId(eid)),
        max_rows=50,
    )
)
