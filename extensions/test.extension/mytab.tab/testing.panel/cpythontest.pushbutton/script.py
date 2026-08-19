#! python3
# -*- coding: utf-8 -*-

from pyrevit import script
from pyrevit import forms

forms.alert(
    "Hello from pyRevit (CPython 3)!",
    title="CPython Test",
    warn_icon=False
)

logger = script.get_logger()
logger.info("CPython Test executed successfully.")
