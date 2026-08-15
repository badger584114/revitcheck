"""DXF/DWG redline export — PLANNING.md §8's secondary "edit natively in
CAD" markup target, alongside `pdf_markup.py`'s primary PDF output (built
2026-08-14, still the primary target per that date's decision). Burns a
check run's Issues onto a dedicated `Z-QC-REDLINE` layer (§8: red, ACI
color 1) in a copy of each sheet's own source DXF — an `LWPOLYLINE`
rectangle (or a `CIRCLE` for a point marker, same zero-area-`bbox`
convention `pdf_markup.py` already uses) plus a `MULTILEADER`+`MTEXT`
note, exactly the entity types §8 names. `extraction.dxf_source.
convert_dxf_to_dwg` is the separate last step to hand the drafting team
an actual `.dwg`, if the original sheet was one.

**Placement needs none of `extraction/dxf_pdf_transform.py`'s viewport/
model-space machinery — only its new `pdf_to_paper`.** A redline marker
only ever has to land in the right spot on the *printed* sheet, and
paper space *is* the printed sheet (that module's docstring: a fixed,
universal, no-offset scale to PDF page space, confirmed against real
data) — so any Issue's already-computed PDF-page-space `bbox` converts
straight to a paper-space location, with no "which viewport" lookup at
all. This is also why this module can burn *every* Issue onto its DXF,
not just geometry-check ones: `pdf_to_paper` doesn't care whether the
`bbox` came from a `DIMENSION`'s witness points or a misspelled word's
`TextWord` — a page-space box is a page-space box.

**One DXF file per sheet, not one per project** — unlike `pdf_markup.py`
(one PDF, many pages), `Sheet.dxf_sheet.source_path` is a different file
per sheet (`extraction/dxf_source.py`'s numeric-suffix join, §8), so
`render_dxf_markup` returns one report covering every Issue (same shared
tag numbering as the PDF export, `markup/tags.py`) but writes a separate
output file per sheet that actually has both a `dxf_sheet` attached and
at least one Issue landing on it — a sheet with zero flagged Issues (or
no DXF counterpart at all, e.g. this project's real index/notes sheets)
gets no output file, not an untouched copy nobody asked for.

**Which paper-space layout to draw onto** — a real sheet's DXF can carry
more than one paper-space `LAYOUT` (`extraction/dxf_pdf_transform.py`'s
docstring: the real sample has one real content layout plus an empty
leftover `Layout2`). `_content_layout` picks the paper-space layout with
the most entities, confirmed against sheet 2871051's real DXF: `Layout1`
carries 1430 entities, `Layout2` carries 0 — an unambiguous real signal,
not a guess, though only confirmed on the one real multi-layout sheet
inspected so far.

**Redline geometry sizes are real-world physical defaults on an A1-scale
sheet, not yet confirmed against a real plot** — `_POINT_MARKER_RADIUS_M`/
`_NOTE_CHAR_HEIGHT_M`/`_LEADER_SEGMENT_M` below are reasonable, legible-
looking values (same "placeholder, not yet calibrated" honesty as
`RuleConfig`'s original rounding-grid defaults), not measured off a real
printed/plotted sheet the way §8's PDF markup sizes implicitly are (PDF
points are a real, fixed physical unit already). Worth confirming once
this runs against a real plot, not guessed at further ahead of that.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Optional

import ezdxf
from ezdxf.math import Vec2

from pdfchecker.checks.issue import Issue
from pdfchecker.ir import BBox, Project, Sheet
from pdfchecker.extraction.dxf_pdf_transform import pdf_to_paper
from pdfchecker.markup.notes import markup_note
from pdfchecker.markup.tags import assign_tags

_REDLINE_LAYER = "Z-QC-REDLINE"
_REDLINE_ACI_COLOR = 1  # red — PLANNING.md §8's format-specific-rendering table
_MLEADER_STYLE = "Standard"  # present by default on both a fresh ezdxf.new() doc and every real sample DXF

_POINT_MARKER_RADIUS_M = 0.015
_NOTE_CHAR_HEIGHT_M = 0.006
_LEADER_SEGMENT_M = 0.03


@dataclass
class DxfMarkupReportEntry:
    """One line of the shared §8 report — same shape as `pdf_markup.py`'s
    `MarkupReportEntry` plus `output_path` (which sheet's marked-up DXF
    file this Issue actually landed in, since this module produces many
    files, not one). `rendered=False` covers every reason there's
    nothing to draw: no `bbox` (same as PDF), no `dxf_sheet` for this
    sheet at all, or no sheet matching this Issue's `page_index`."""

    tag: str
    rule_id: str
    category: str
    severity: str
    sheet_no: Optional[str]
    page_index: int
    description: str
    note: str
    rendered: bool
    output_path: Optional[str]

    def to_dict(self) -> dict:
        return {
            "tag": self.tag,
            "rule_id": self.rule_id,
            "category": self.category,
            "severity": self.severity,
            "sheet_no": self.sheet_no,
            "page_index": self.page_index,
            "description": self.description,
            "note": self.note,
            "rendered": self.rendered,
            "output_path": self.output_path,
        }


def _is_point(bbox: BBox) -> bool:
    return bbox.width == 0 and bbox.height == 0


def _content_layout(doc):
    """The paper-space `Layout` with the most entities — see module
    docstring for the real basis (sheet 2871051: 1430 vs. 0). `None` if
    the DXF has no paper-space layout at all (not seen on real data, but
    a real DXF file isn't guaranteed to have one)."""

    paperspace = [layout for layout in doc.layouts if layout.is_any_paperspace]
    if not paperspace:
        return None
    return max(paperspace, key=lambda layout: len(list(layout)))


def _ensure_redline_layer(doc) -> None:
    if _REDLINE_LAYER not in doc.layers:
        doc.layers.add(_REDLINE_LAYER, color=_REDLINE_ACI_COLOR)


def _draw_issue(layout, page_height_pt: float, issue: Issue, note: str, tag: str) -> None:
    bbox = issue.bbox
    x0, y0 = pdf_to_paper(bbox.x0, bbox.y0, page_height_pt)
    x1, y1 = pdf_to_paper(bbox.x1, bbox.y1, page_height_pt)
    # pdf_to_paper flips Y, so (x0,y0)/(x1,y1) don't preserve min/max
    # ordering the way the source bbox's did — resolve fresh rather than
    # assume.
    px0, px1 = sorted((x0, x1))
    py0, py1 = sorted((y0, y1))
    attribs = {"layer": _REDLINE_LAYER}

    if _is_point(bbox):
        layout.add_circle((px0, py0), radius=_POINT_MARKER_RADIUS_M, dxfattribs=attribs)
        target = Vec2(px0, py0)
    else:
        layout.add_lwpolyline(
            [(px0, py0), (px1, py0), (px1, py1), (px0, py1)], close=True, dxfattribs=attribs
        )
        # Right-edge midpoint — mirrors pdf_markup.py's own default
        # leader-start side (falls back to the left only near a page
        # edge there; not replicated here, same real limitation noted
        # in that module's docstring, left honest rather than half-solved
        # a second time).
        target = Vec2(px1, (py0 + py1) / 2)

    content = f"{note} {tag}"
    ml = layout.add_multileader_mtext(_MLEADER_STYLE, dxfattribs=attribs)
    ml.set_content(content, char_height=_NOTE_CHAR_HEIGHT_M)
    ml.quick_leader(content, target=target, segment1=Vec2(_LEADER_SEGMENT_M, 0.0))


def render_dxf_markup(project: Project, issues: list[Issue], output_dir: str) -> list[DxfMarkupReportEntry]:
    """Burns `issues` onto a fresh copy of each flagged sheet's own
    source DXF (`Sheet.dxf_sheet.source_path`) and saves the results into
    `output_dir` as `<original stem>_markup.dxf`. Same caller contract as
    `pdf_markup.render_markup` — `issues` is whatever subset the engineer
    already selected (PLANNING.md §8 step 2), not filtered here — but
    returns one report covering the *whole* `issues` list (not just the
    ones with a DXF to land in), using `markup/tags.py`'s shared tag
    order so a tag means the same Issue in both this report and
    `render_markup`'s."""

    Path(output_dir).mkdir(parents=True, exist_ok=True)
    sheets_by_page: dict[int, Sheet] = {sheet.page_index: sheet for sheet in project.sheets}

    # Lazily opened, one ezdxf.Drawing per sheet that actually gets
    # something drawn onto it — most sheets in a real project have zero
    # flagged Issues, and opening+resaving a DXF nothing changed about is
    # both wasted work and a spurious diff for the drafting team.
    open_docs: dict[int, tuple] = {}  # page_index -> (doc, layout, out_path)

    def _doc_for(sheet: Sheet):
        if sheet.page_index in open_docs:
            return open_docs[sheet.page_index]
        doc = ezdxf.readfile(sheet.dxf_sheet.source_path)
        layout = _content_layout(doc)
        if layout is not None:
            _ensure_redline_layer(doc)
        stem = Path(sheet.dxf_sheet.source_path).stem
        out_path = str(Path(output_dir) / f"{stem}_markup.dxf")
        entry = (doc, layout, out_path)
        open_docs[sheet.page_index] = entry
        return entry

    report = []
    for tag, issue in assign_tags(issues):
        note = markup_note(issue)
        sheet = sheets_by_page.get(issue.page_index)
        rendered = False
        out_path = None

        if issue.bbox is not None and sheet is not None and sheet.dxf_sheet is not None:
            doc, layout, out_path = _doc_for(sheet)
            if layout is not None:
                _draw_issue(layout, sheet.page_height, issue, note, tag)
                rendered = True
            else:
                out_path = None  # nothing to draw onto — don't claim a file that won't get saved

        report.append(
            DxfMarkupReportEntry(
                tag=tag,
                rule_id=issue.rule_id,
                category=issue.category,
                severity=issue.severity,
                sheet_no=issue.sheet_no,
                page_index=issue.page_index,
                description=issue.description,
                note=note,
                rendered=rendered,
                output_path=out_path if rendered else None,
            )
        )

    for doc, layout, out_path in open_docs.values():
        if layout is not None:
            doc.saveas(out_path)

    return report
