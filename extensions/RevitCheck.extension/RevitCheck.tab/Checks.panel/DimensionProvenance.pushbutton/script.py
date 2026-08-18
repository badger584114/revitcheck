#! python3
"""Report dimensions that measure detail linework rather than the model.

Thin on purpose. A button's job is: get the document, read it, run the
rules, render the result. All four of those live in `revitcheck`, which
is testable off a Revit machine — anything that grows in here instead is
logic that can only be debugged in Revit.
"""

from pyrevit import revit, script

from Autodesk.Revit.DB import ElementId

import revitcheck.checks  # noqa: F401 - registers the rules
from revitcheck import RuleConfig, run_checks
from revitcheck.adapters.revit_source import read_model
from revitcheck.checks.dimensions import drafted_views
from revitcheck.report import to_markdown

output = script.get_output()
output.set_title("Dimension Provenance")

doc = revit.doc

# Read-only: no transaction is opened anywhere in the read path, so
# there is nothing here that can modify the model being reviewed.
model = read_model(doc)

config = RuleConfig(
    enabled_rule_ids={"revit.dimension_provenance", "revit.capture_coverage"}
)
issues = run_checks(model, config)

output.print_md(
    to_markdown(
        issues,
        model_title=model.doc_title,
        linkify=lambda eid: output.linkify(ElementId(eid)),
    )
)

# The views whose dimensions are *all* drafted are the handoff to the
# planned follow-up tool — the one that verifies drafted setout against
# the model. Listing them separately makes that scope visible now,
# rather than leaving it to be reconstructed from issue descriptions.
fully_drafted = drafted_views(model, config)
if fully_drafted:
    output.print_md("")
    output.print_md("### Views to verify against the model")
    output.print_md(
        "These {0} view(s) contain no model-derived dimensions at all, so "
        "nothing in the file can show whether they have drifted.".format(
            len(fully_drafted)
        )
    )
    for view in fully_drafted:
        output.print_md(
            "- {0} — {1} {2}".format(
                output.linkify(ElementId(view.element_id), title=view.name),
                view.view_type,
                "on sheet {0}".format(view.sheet_no) if view.sheet_no else "(no sheet)",
            )
        )
