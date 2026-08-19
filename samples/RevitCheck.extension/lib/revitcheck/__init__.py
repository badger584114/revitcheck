"""revitcheck — drawing checks that run inside Revit.

This package lives at `<extension>/lib/` on purpose. pyRevit puts an
extension's `lib` folder on `sys.path` automatically, so this is the
single copy of the code: the same files the pyRevit buttons import on
the Revit machine are the files the tests import here. A `src/` layout
plus a sync step was the alternative and was rejected — editing in one
place and debugging stale code in another is a bad failure mode in
general, and a genuinely expensive one when the two places are two
different computers.

**Layering, which is what makes offline development possible:**

    adapters/revit_source.py   the ONLY module that imports the Revit API
        |                      (reads the open document -> RevitModel)
        v
    ir.py                      plain dataclasses, raw facts, millimetres
        |
        v
    checks/*.py                pure (RevitModel, RuleConfig) -> [Issue]

Nothing below the adapter knows Revit exists, so every rule is testable
on any machine. `capture.py` bridges the two: dump a real model to JSON
at work, develop against it anywhere.

Importing this package must never pull in the Revit API — that is why
`adapters` is not imported here.
"""

from revitcheck.catalog import RuleConfig, all_rule_ids, register, run_checks
from revitcheck.ir import Provenance, RevitModel
from revitcheck.issue import Issue, sort_issues

__all__ = [
    "Issue",
    "Provenance",
    "RevitModel",
    "RuleConfig",
    "all_rule_ids",
    "register",
    "run_checks",
    "sort_issues",
]
