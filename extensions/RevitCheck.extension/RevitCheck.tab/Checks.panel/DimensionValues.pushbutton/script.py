#! python3
"""Report typed-over dimension values that disagree with the model.

Thin on purpose, same as its neighbour: get the document, read it, run
the rules, render the result. All four live in `revitcheck`, which is
testable off a Revit machine — anything that grows in here instead is
logic that can only be debugged in Revit.
"""

from pyrevit import revit, script

from Autodesk.Revit.DB import ElementId

import revitcheck.checks  # noqa: F401 - registers the rules
from revitcheck import RuleConfig, run_checks
from revitcheck.adapters.revit_source import read_model
from revitcheck.report import to_markdown

output = script.get_output()
output.set_title("Dimension Values")

doc = revit.doc

# Read-only: no transaction is opened anywhere in the read path.
model = read_model(doc)

config = RuleConfig(
    enabled_rule_ids={
        "revit.dimension_override_consistency",
        "revit.capture_coverage",
    }
)
issues = run_checks(model, config)

output.print_md(
    to_markdown(
        issues,
        model_title=model.doc_title,
        linkify=lambda eid: output.linkify(ElementId(eid)),
    )
)

# The coverage line is the one to read before concluding anything from a
# short list. This rule can only check overrides that parse as a number,
# and on some drafting conventions that is almost none of them — in
# which case "no findings" means "nothing was in scope", not "clean".
output.print_md("")
output.print_md(
    "_Tolerances come from `RuleConfig` and are inherited placeholders, not "
    "yet calibrated against a real Revit model. If the findings above look "
    "wrong in bulk, suspect the grid before the drawings._"
)
