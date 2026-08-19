"""Format adapters.

Deliberately empty. `revit_source` is **not** imported here: it needs
the Revit API, and importing this package must stay safe on a machine
that has none. Import it explicitly, from a pyRevit script only:

    from revitcheck.adapters.revit_source import read_model
"""
