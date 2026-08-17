"""PDF markup export — PLANNING.md §8. Burns a check run's Issues onto
the source PDF as native annotations (`PyMuPDF` `Square`/`Circle` for
the flagged content, `FreeText` with a built-in `callout` leader for the
terse note) — not a flattened raster overlay, so the drafting team can
toggle, reply to, or resolve them in Acrobat or Bluebeam. PDF is the
primary markup target for every Issue, including geometry-check ones
(§8, decided 2026-08-10) — every rule in the catalog already reports a
page-space `bbox` where one exists (`extraction/dxf_pdf_transform.py`
for the geometry rules), so this module doesn't need to know or care
which engine produced an Issue.

**Two markup shapes, from the bbox's own shape, not a per-rule lookup**
— §8: "Use a bounding-box-less marker (small circle at the point) only
when the issue is an *absence*." A zero-area `BBox` (`x0==x1`,
`y0==y1`) is exactly how `checks/geometry.py`'s `_setout_point_bbox`
already represents a single reconstructed point (PR #13) — reusing that
shape as the "this is a point, not a region" signal means no new Issue
field or per-rule flag is needed; every real zero-area bbox in the
catalog today already *is* a single-point location, and every non-zero
one already *is* a real flagged region (a word, a title-block band, a
dimension's witness-point span, a revision cloud, a cross-sheet marker).

**Issues with `bbox=None` are still counted and tagged, just not drawn**
— there's nothing to attach a marker to (a whole-table revision check,
or a geometry point whose transform didn't resolve), but the Issue
still belongs in the full report (§8: "each issue gets a short reference
tag... [and] the full report has the matching entry"), so it isn't
silently dropped from the numbering.

**Real limitation, left honest rather than half-solved:** note
placement is a fixed offset from the flagged bbox (right side, falling
back to the left near a page edge), not a real collision-avoidance
layout — two Issues flagged close together on a dense sheet can produce
overlapping notes. Worth revisiting once this runs against a real sheet
dense enough to show it, not guessed at ahead of that.

Tag numbering (`assign_tags`, below) briefly lived in its own
`markup/tags.py` module (2026-08-15), shared with a DXF/DWG redline
exporter so a tag would mean the same Issue in either format. That
exporter was removed 2026-08-17 — all drafting here is done in Revit and
the drafting team never opens AutoCAD, so a CAD-native redline had no
audience (see PLANNING.md §8). With one markup format again, the shared
module had one caller and nothing to stay in sync with, so it's folded
back in here.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

import fitz

from pdfchecker.checks.issue import Issue
from pdfchecker.ir import BBox, Project
from pdfchecker.markup.notes import markup_note

# PLANNING.md §8 doesn't specify a tag order; page-then-severity-then-
# rule_id groups a sheet's most actionable issues first, matching how an
# engineer would triage a printed set page by page.
_SEVERITY_ORDER = {"high": 0, "medium": 1, "low": 2}


def assign_tags(issues: list[Issue]) -> list[tuple[str, Issue]]:
    """`[(tag, issue), ...]` in a deterministic order — PLANNING.md §8:
    "each issue gets a short reference tag (e.g. #014) ... matching an
    entry in the full exported report."

    The sort ends on `issue_id` (2026-08-17). Without it the key
    (page, severity, rule_id) leaves genuine ties — a sheet with twenty
    spelling issues has twenty identical keys — which Python's stable
    sort then breaks by *input list order*. That made tags depend on the
    order the caller happened to accumulate issues in, so the same run
    could tag the same finding differently. Ending on a content-derived
    id makes this a total order, and the numbering reproducible.

    The tag itself is still positional and always will be: it's a
    human-readable index into one report ("see #014"), so a selected
    subset legitimately renumbers from #001. `Issue.issue_id` is the
    thing that identifies a finding across runs and selections; the tag
    identifies its row in one exported package."""

    ordered = sorted(
        issues,
        key=lambda i: (
            i.page_index,
            _SEVERITY_ORDER.get(i.severity, len(_SEVERITY_ORDER)),
            i.rule_id,
            i.issue_id,
        ),
    )
    return [(f"#{idx:03d}", issue) for idx, issue in enumerate(ordered, start=1)]


# PLANNING.md §8 doesn't specify a palette — high/medium/low mapped to a
# real red/orange/olive traffic-light scale, distinguishable at a glance
# and consistent with how the rest of this codebase treats severity.
_SEVERITY_COLOR = {
    "high": (0.86, 0.15, 0.15),
    "medium": (0.90, 0.55, 0.10),
    "low": (0.55, 0.55, 0.15),
}
_DEFAULT_COLOR = (0.5, 0.5, 0.5)

_POINT_MARKER_RADIUS_PT = 4.0
_NOTE_WIDTH_PT = 140.0
_NOTE_HEIGHT_PT = 16.0
_NOTE_OFFSET_PT = 18.0
_NOTE_FONTSIZE_PT = 8.0


@dataclass
class MarkupReportEntry:
    """One line of §8's full report — the reference tag every markup
    note carries, matched to the complete detail a drafter or engineer
    can look up if the terse sheet note isn't enough. `rendered=False`
    means this Issue has no `bbox` to draw (see module docstring) — it
    still has a tag and a report entry, just no mark on any sheet.

    Carries `issue_id` (2026-08-17) as well as `tag`: the tag indexes one
    exported package and renumbers with the list, while the id is stable
    across runs and selections, so anything joining this to other run
    output — `markup/report.py` does — should join on the id."""

    tag: str
    issue_id: str
    rule_id: str
    category: str
    severity: str
    sheet_no: Optional[str]
    page_index: int
    description: str
    note: str
    rendered: bool

    def to_dict(self) -> dict:
        return {
            "tag": self.tag,
            "issue_id": self.issue_id,
            "rule_id": self.rule_id,
            "category": self.category,
            "severity": self.severity,
            "sheet_no": self.sheet_no,
            "page_index": self.page_index,
            "description": self.description,
            "note": self.note,
            "rendered": self.rendered,
        }


def _is_point(bbox: BBox) -> bool:
    return bbox.width == 0 and bbox.height == 0


def _note_placement(bbox: BBox, page_width: float, page_height: float) -> tuple[fitz.Rect, fitz.Point]:
    """The note's own box and the leader's starting point on the flagged
    bbox, chosen together so the leader always points at the correct
    edge for whichever side the note actually landed on (module
    docstring's placement limitation)."""

    mid_y = (bbox.y0 + bbox.y1) / 2
    right_x0 = bbox.x1 + _NOTE_OFFSET_PT
    if right_x0 + _NOTE_WIDTH_PT <= page_width:
        note_x0 = right_x0
        leader_start = fitz.Point(bbox.x1, mid_y)
    else:
        note_x0 = max(0.0, bbox.x0 - _NOTE_OFFSET_PT - _NOTE_WIDTH_PT)
        leader_start = fitz.Point(bbox.x0, mid_y)
    note_y0 = max(0.0, min(bbox.y0 - _NOTE_HEIGHT_PT / 2, page_height - _NOTE_HEIGHT_PT))
    note_rect = fitz.Rect(note_x0, note_y0, note_x0 + _NOTE_WIDTH_PT, note_y0 + _NOTE_HEIGHT_PT)
    return note_rect, leader_start


def _draw_issue(page: fitz.Page, issue: Issue, note: str, tag: str) -> None:
    bbox = issue.bbox
    color = _SEVERITY_COLOR.get(issue.severity, _DEFAULT_COLOR)

    if _is_point(bbox):
        r = _POINT_MARKER_RADIUS_PT
        marker = page.add_circle_annot(fitz.Rect(bbox.x0 - r, bbox.y0 - r, bbox.x0 + r, bbox.y0 + r))
    else:
        marker = page.add_rect_annot(fitz.Rect(bbox.x0, bbox.y0, bbox.x1, bbox.y1))
    marker.set_colors(stroke=color)
    marker.set_border(width=1.2)
    marker.update()

    note_rect, leader_start = _note_placement(bbox, page.rect.width, page.rect.height)
    leader_end = fitz.Point(note_rect.x0, note_rect.y0 + note_rect.height / 2)
    text_annot = page.add_freetext_annot(
        note_rect,
        f"{note} {tag}",
        fontsize=_NOTE_FONTSIZE_PT,
        text_color=(0, 0, 0),
        fill_color=(1, 1, 0.82),
        callout=(leader_start, leader_end),
    )
    text_annot.set_border(width=0.75)
    text_annot.update()


def render_markup(project: Project, issues: list[Issue], output_path: str) -> list[MarkupReportEntry]:
    """Burns `issues` onto a fresh copy of `project.source_path`
    (opened independently of whatever extraction already did with it —
    ingestion's own `fitz.Document` isn't kept open past ingestion) and
    saves the result to `output_path`. `issues` is whatever subset the
    caller already selected (PLANNING.md §8 step 2 — "Engineer selects
    which issues to include"); this function doesn't filter by severity
    or category itself. Returns the full report (§8 step 4/§7) — one
    `MarkupReportEntry` per Issue, in the same tag order they were drawn,
    so a caller can render/export it as a table alongside the marked-up
    PDF without recomputing anything."""

    doc = fitz.open(project.source_path)

    report = []
    for tag, issue in assign_tags(issues):
        note = markup_note(issue)
        rendered = False
        if issue.bbox is not None and 0 <= issue.page_index < doc.page_count:
            _draw_issue(doc[issue.page_index], issue, note, tag)
            rendered = True
        report.append(
            MarkupReportEntry(
                tag=tag,
                issue_id=issue.issue_id,
                rule_id=issue.rule_id,
                category=issue.category,
                severity=issue.severity,
                sheet_no=issue.sheet_no,
                page_index=issue.page_index,
                description=issue.description,
                note=note,
                rendered=rendered,
            )
        )

    doc.save(output_path)
    doc.close()
    return report
