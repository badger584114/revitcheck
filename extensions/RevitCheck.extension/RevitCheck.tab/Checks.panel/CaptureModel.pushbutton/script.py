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

from pyrevit import forms, revit, script

from revitcheck import capture
from revitcheck.adapters.revit_source import read_model

output = script.get_output()
output.set_title("Capture Model")

doc = revit.doc
model = read_model(doc)

default_name = "{0}.capture.json".format(
    os.path.splitext(os.path.basename(doc.Title or "model"))[0]
)

path = forms.save_file(file_ext="json", default_name=default_name)
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
