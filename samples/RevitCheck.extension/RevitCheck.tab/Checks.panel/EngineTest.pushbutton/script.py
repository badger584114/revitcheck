# -*- coding: utf-8 -*-

from pyrevit import script
from pyrevit import forms

forms.alert(
    "Hello from pyRevit!",
    title="Test Button",
    warn_icon=False
)

logger = script.get_logger()
logger.info("Test Button executed successfully.")