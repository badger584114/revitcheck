"""Revision consistency — PLANNING.md §4 "Revision consistency —
mechanics", the three-way cross-check (schedule / title-block parameter /
revision clouds). Two of the three are implemented here; the third
(cloud/triangle -> schedule row) needs new geometric detection work
(closed scalloped-curve + triangle/tag symbol recognition) not yet built,
so it's deliberately out of scope for this pass — see the module docstring
note below rather than a half-built detector.

1. Schedule <-> current-revision title-block parameter (implemented)
2. Cloud/triangle -> schedule row (NOT implemented — needs revision-cloud
   detection, a real computer-vision-ish task on vector paths; §4 already
   anticipated the label for this: `Rev: cloud has no table row`)
3. Sequential numbering within the schedule (implemented)
"""

from __future__ import annotations

from pdfchecker.checks.catalog import RuleConfig, register
from pdfchecker.checks.issue import Issue
from pdfchecker.ir import Project, Sheet


def _latest_revision(sheet: Sheet):
    """The most recent revision on a sheet. Rev IDs are the authoritative
    signal when parseable as integers (unambiguous); this sample has only
    one revision row so spatial stacking order can't be empirically
    confirmed as which end is newest — falls back to the row closest to
    the header (last in the list, see extraction/tables.py) rather than
    guessing a spatial convention from a single data point."""

    if not sheet.revision_schedule:
        return None
    numeric = [(r, int(r.rev_id)) for r in sheet.revision_schedule if r.rev_id.isdigit()]
    if numeric:
        return max(numeric, key=lambda pair: pair[1])[0]
    return sheet.revision_schedule[-1]


@register("revision.schedule_matches_title_block")
def check_schedule_matches_title_block(project: Project, config: RuleConfig) -> list[Issue]:
    issues = []
    for sheet in project.sheets:
        latest = _latest_revision(sheet)
        amend_no = sheet.title_block.get("amend_no")
        if latest is None or amend_no is None:
            continue  # can't cross-check what wasn't extracted; title_block.required_fields_present already flags a missing amend_no
        if latest.rev_id.strip() != amend_no.strip():
            issues.append(
                Issue(
                    rule_id="revision.schedule_matches_title_block",
                    category="revision",
                    sheet_no=sheet.sheet_no,
                    page_index=sheet.page_index,
                    description=(
                        f"Title block AMEND No. is '{amend_no}' but the revision "
                        f"schedule's latest row is '{latest.rev_id}' ({latest.description})"
                    ),
                    severity="high",
                    suggested_fix={"title_block_amend_no": amend_no, "schedule_latest_rev_id": latest.rev_id},
                )
            )
    return issues


@register("revision.sequential_numbering")
def check_sequential_numbering(project: Project, config: RuleConfig) -> list[Issue]:
    issues = []
    for sheet in project.sheets:
        rev_ids = [r.rev_id for r in sheet.revision_schedule if r.rev_id]
        numeric_ids = [int(r) for r in rev_ids if r.isdigit()]
        if len(numeric_ids) != len(rev_ids):
            continue  # non-numeric scheme (e.g. letters A/B/C) — not handled by this rule yet
        if len(numeric_ids) != len(set(numeric_ids)):
            dupes = sorted({n for n in numeric_ids if numeric_ids.count(n) > 1})
            issues.append(
                Issue(
                    rule_id="revision.sequential_numbering",
                    category="revision",
                    sheet_no=sheet.sheet_no,
                    page_index=sheet.page_index,
                    description=f"Revision schedule has duplicate revision number(s): {dupes}",
                    severity="high",
                )
            )
            continue
        expected = list(range(min(numeric_ids), max(numeric_ids) + 1)) if numeric_ids else []
        missing = sorted(set(expected) - set(numeric_ids))
        if missing:
            issues.append(
                Issue(
                    rule_id="revision.sequential_numbering",
                    category="revision",
                    sheet_no=sheet.sheet_no,
                    page_index=sheet.page_index,
                    description=f"Revision schedule is missing revision number(s): {missing}",
                    severity="medium",
                )
            )
    return issues
