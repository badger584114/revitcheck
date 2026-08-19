#! python3
# -*- coding: utf-8 -*-
"""Diagnostic only — not a real check.

A smoke test for the CPython 3 engine: confirms a `#! python3` script
can launch, reach the Revit document, and import the `revitcheck`
package. Uses only `script.get_output()` — **not** `pyrevit.forms`,
which is IronPython-only and raises `PyRevitCPythonNotSupported` under
CPython for every name in the module.

Safe to delete once the real buttons are known good.
"""

import sys

from pyrevit import revit, script

import revitcheck
from revitcheck.adapters.revit_source import read_model

output = script.get_output()
output.set_title("Engine Test")

output.print_md("### CPython engine launched")
output.print_md("- `sys.version`: `{0}`".format(sys.version))
output.print_md("- open document: `{0}`".format(revit.doc.Title))
output.print_md("- `revitcheck` imported from: `{0}`".format(revitcheck.__file__))

model = read_model(revit.doc)
output.print_md("### Adapter read the document")
output.print_md(
    "- {0} sheet(s), {1} view(s), {2} dimension(s)".format(
        len(model.sheets), len(model.views), len(model.dimensions)
    )
)
if model.extraction_errors:
    output.print_md(
        "- {0} element(s) could not be read".format(len(model.extraction_errors))
    )
