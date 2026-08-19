#! python3
"""Diagnostic only — not a real check.

Deliberately imports nothing from `revitcheck`, to isolate whether the
CPython 3 engine can launch *any* `#! python3` script on this machine at
all, versus something specific to the real buttons' scripts. Delete this
whole EngineTest.pushbutton folder once the real buttons work.
"""

import sys

from pyrevit import revit, script

output = script.get_output()
output.set_title("Engine Test")
output.print_md("### CPython engine launched successfully")
output.print_md("- `sys.version`: `{0}`".format(sys.version))
output.print_md("- open document title: `{0}`".format(revit.doc.Title))
