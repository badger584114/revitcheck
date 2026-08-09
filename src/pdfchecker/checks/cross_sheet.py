"""Cross-sheet consistency — PLANNING.md §4's "Cross-sheet consistency"
row: "verify every reference resolves ... all callouts resolve to
something that exists." The reference graph itself (extraction/
references.py) does the actual detection/resolution work as a whole-
project pass during ingestion; this rule is the thin §4-step-5 layer on
top — every unresolved `Reference` on `project.references` becomes an
Issue, using the `No match: <ref>` markup label PLANNING.md §8 already
defines for this category.
"""

from __future__ import annotations

from pdfchecker.checks.catalog import RuleConfig, register
from pdfchecker.checks.issue import Issue
from pdfchecker.ir import Project

_KIND_LABEL = {"section": "Section", "detail": "Detail", "unknown": "Reference"}


def _ref_string(ref) -> str:
    kind = _KIND_LABEL.get(ref.ref_type, "Reference")
    return f"{kind} {ref.tag}/{ref.target_sheet_hint}"


@register("cross_sheet.reference_resolves")
def check_reference_resolves(project: Project, config: RuleConfig) -> list[Issue]:
    issues = []
    for ref in project.references:
        if ref.resolved:
            continue
        ref_str = _ref_string(ref)
        # A referenced sheet number that doesn't exist anywhere in the set
        # is unambiguous — something is genuinely wrong. A sheet that
        # exists but doesn't carry a matching view title is a weaker
        # signal (could be this heuristic's own extraction miss, per
        # extraction/references.py's docstring), so it's flagged at lower
        # severity rather than treated the same as a confirmed dead
        # reference — same "confidence, not silent" principle as the
        # reference graph's own resolution step.
        severity = "high" if ref.confidence == 0.0 else "medium"
        issues.append(
            Issue(
                rule_id="cross_sheet.reference_resolves",
                category="cross_sheet",
                sheet_no=ref.source_sheet_no,
                page_index=ref.source_page_index,
                description=(
                    f"{ref_str} does not resolve — sheet "
                    f"'{ref.target_sheet_hint}' has no matching view titled for tag '{ref.tag}'"
                    if ref.confidence > 0.0
                    else f"{ref_str} does not resolve — sheet '{ref.target_sheet_hint}' does not exist in this set"
                ),
                bbox=ref.source_bbox,
                severity=severity,
                suggested_fix={"ref": ref_str},
            )
        )
    return issues
