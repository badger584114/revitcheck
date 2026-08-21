"""Coverage reporting for the extraction step itself.

A rule rather than a report-formatting detail, deliberately. The
adapter isolates per-element failures so one unreadable dimension can't
abort a capture of five thousand — which is right, but it means a
capture that silently dropped a chunk of the model would otherwise be
indistinguishable from a clean one. CLAUDE.md's convention is explicit
about that class of bug ("report a coverage indicator, don't fail
silently"), and it has already bitten this project once: a whole check
scope ran zero rules and looked exactly like a clean result.

Making it a rule means it travels with the issue list wherever that
goes, instead of depending on whoever formats the output remembering to
print it.
"""

from __future__ import annotations

from typing import List

from revitcheck.catalog import RuleConfig, register
from revitcheck.ir import RevitModel
from revitcheck.issue import Issue

# Long error lists are usually one repeated cause. Show enough to
# recognise it, not enough to bury the real findings.
_MAX_LISTED = 5


@register("revit.capture_coverage")
def check_capture_coverage(model: RevitModel, config: RuleConfig) -> List[Issue]:
    issues: List[Issue] = []

    if model.extraction_errors:
        listed = model.extraction_errors[:_MAX_LISTED]
        remainder = len(model.extraction_errors) - len(listed)
        detail = "; ".join(listed)
        if remainder > 0:
            detail += " (+{0} more)".format(remainder)

        issues.append(
            Issue(
                rule_id="revit.capture_coverage",
                category="coverage",
                description=(
                    "{0} element(s) could not be read from the model and were "
                    "not checked: {1}"
                ).format(len(model.extraction_errors), detail),
                severity="medium",
            )
        )

    if model.excluded_worksets:
        listed = model.excluded_worksets[:_MAX_LISTED]
        remainder = len(model.excluded_worksets) - len(listed)
        names = ", ".join(listed)
        if remainder > 0:
            names += " (+{0} more)".format(remainder)

        issues.append(
            Issue(
                rule_id="revit.capture_coverage",
                category="coverage",
                description=(
                    "{0} workset(s) were excluded from this capture by user "
                    "selection and nothing on them was checked — not because "
                    "the model is clean there, but because it wasn't looked "
                    "at: {1}"
                ).format(len(model.excluded_worksets), names),
                severity="low",
            )
        )

    return issues
