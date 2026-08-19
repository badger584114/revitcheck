#! python3
# -*- coding: utf-8 -*-
"""Diagnostic only. Same as cpythontest.pushbutton, plus touching
revit.doc.Title — the one variable that button doesn't cover."""

import sys

from pyrevit import revit, script, forms

output = script.get_output()
output.set_title("CPython Doc Test")
output.print_md("### CPython + revit.doc access, in test.extension")
output.print_md("- `sys.version`: `{0}`".format(sys.version))
output.print_md("- open document title: `{0}`".format(revit.doc.Title))

forms.alert("Reached the end without throwing.", title="CPython Doc Test", warn_icon=False)
