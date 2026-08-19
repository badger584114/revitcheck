#! python3
# -*- coding: utf-8 -*-
"""Diagnostic only — not a real check.

Deliberately touches nothing from the Revit API or `revitcheck` — just
forms.alert, same as the working test button. Isolates whether CPython 3
itself fails inside RevitCheck.extension, or whether it's specifically
touching `revit.doc` (as the original version of this script, and the
three real buttons, all do) that triggers the failure.
"""

from pyrevit import script
from pyrevit import forms

forms.alert(
    "Hello from pyRevit (CPython 3, RevitCheck.extension, no revit.doc)!",
    title="Engine Test",
    warn_icon=False
)

logger = script.get_logger()
logger.info("Engine Test executed successfully.")
