"""Rule modules.

Importing this package imports every rule module for its `@register`
side-effects, so `catalog.all_rule_ids()` is complete after a single
`import revitcheck.checks`.

This list is load-bearing and easy to under-maintain. Omitting a module
here was a real bug on the PDF side (2026-08-17): `geometry` was missing,
so a geometry-scoped run executed zero geometry rules, with no error and
no warning, indistinguishable from a clean result. `tests/revit/` guards
the same failure by importing only this package and asserting the
catalog holds every expected rule id.
"""

from revitcheck.checks import coverage, dimensions  # noqa: F401

__all__ = ["coverage", "dimensions"]
