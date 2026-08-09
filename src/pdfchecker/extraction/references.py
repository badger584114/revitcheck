"""Cross-sheet reference graph — PLANNING.md §3 "References" / §4
"Cross-sheet reference graph — mechanics". Built as its own pass across
the whole extracted sheet set (needs every sheet's words already
extracted first), not per-sheet ingestion — see ir.py's `Reference`
docstring.

Scoped to symbol-based references only (section markers, detail
bubbles) — general free-text note references ("see Detail 4 on Dwg
S-201") are explicitly deferred by PLANNING.md §4's scoping note, same
sequencing as §6's narrative-extraction deferral.

Calibrated against samples/T2DPAA-T2D-C3S-BR-DRG-101000.pdf (CLAUDE.md:
check real samples before assuming a convention, and PLANNING.md §4
says to build/tune this directly against real sheets). Two things this
sample forced a deviation from the originally-sketched mechanics:

1. **Text adjacency, not vector shape, is the reliable signal.** PLANNING
   §4 anticipated detecting a circle/hexagon/pentagon symbol first, then
   proximity-matching its text. On this sample, SECTION markers ARE a
   circular vector path, but DETAIL markers aren't (confirmed: no
   matching circular/curve path near "DETAIL A"'s tag+sheet-number text
   on page 10) — so keying off shape would miss a whole marker family.
   What both share, regardless of symbol: a short tag (a digit or single
   letter — "3", "A") stacked directly above or below a drawing/sheet
   number (e.g. "2871023"), a few points apart. That text pattern alone
   is what this module detects.

2. **A marker's printed sheet number names "where the counterpart is",
   not "where this marker sits".** Confirmed against the sample: the
   identical (tag, sheet number) text appears at BOTH ends of a
   reference — once at the callout (e.g. a section cut line in a plan
   view), pointing FORWARD to the sheet the section is actually drawn
   on; once beside the section/detail view's own title, pointing BACK to
   the sheet the callout was made from. Telling the two apart doesn't
   need classifying which is which up front: a marker sitting right next
   to a matching "SECTION <tag>"/"DETAIL <tag>" title on ITS OWN sheet is
   the view's own identifying marker (already a resolved target, nothing
   to look up); every other marker is a callout, resolved against the
   index of every view title found across the whole sheet set — this is
   PLANNING §4's resolution algorithm, steps 1-3, applied as-is once the
   self-marker case is filtered out first.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from pdfchecker.extraction.titleblock import TITLE_BLOCK_BAND_FRACTION
from pdfchecker.ir import BBox, Project, Reference, Sheet

# Real tags on the sample are a single digit ("1".."5") or a single
# letter ("A".."C") — never a multi-letter word, which is what lets this
# regex double as a filter against body-text words that happen to sit
# next to "SECTION"/"DETAIL" (see _STOPWORDS below for the rest of that
# filtering). Firms using richer tag formats (e.g. "A1", letter-prefixed
# sheet numbers) aren't covered yet — a documented limitation, not an
# oversight.
_TAG_RE = re.compile(r"^(?:[0-9]{1,2}|[A-Z])$")

# Sheet numbers on this sample are 7 digits ("2871023"); 6-8 gives slack
# without matching short dimension/scale values that also show up as
# bare digit tokens in a drawing body.
_SHEET_NO_RE = re.compile(r"^\d{6,8}$")

_VIEW_KEYWORDS = {"SECTION": "section", "DETAIL": "detail"}

# Words that precede a "SECTION"/"DETAIL" mention when it's a body-text
# cross-reference ("REFER TO SECTION 1", "SEE DETAIL 4") rather than a
# view's own title ("HEADSTOCK SECTION 3", "SECTION 1 SECTION 2" — two
# titles sharing a line). This is the general-note-reference pattern
# PLANNING §4 explicitly defers (see module docstring) — the stoplist
# exists to keep those sentences from being misread as titles, not to
# extract them as references themselves.
_STOPWORDS = {"REFER", "SEE", "TO", "PER", "SHOWN", "NOTE", "NOTES", "WITH", "ACCORDANCE"}

# How close a tag word has to sit to a sheet-number word to be read as
# one marker (stacked, small horizontal offset) — calibrated against the
# sample's ~4pt real vertical gaps between a marker's tag and its sheet
# number.
_MARKER_MAX_DX = 15.0
_MARKER_MAX_DY = 20.0

# How close a title's inline tag has to sit to a marker's own tag word,
# on the same sheet, to treat that marker as the view's own identifying
# marker rather than an unrelated callout — generous since both sit in
# the same small graphic cluster beside the title on the sample.
_SELF_MARKER_MAX_DIST = 120.0


@dataclass
class _ViewTitle:
    kind: str  # "section" | "detail"
    tag: str
    bbox: BBox
    sheet_no: str
    page_index: int


@dataclass
class _Marker:
    tag: str
    sheet_hint: str
    bbox: BBox
    sheet_no: str | None
    page_index: int


def _norm_tag(text: str) -> str:
    return text.strip().upper()


def _extract_view_titles(sheet: Sheet) -> list[_ViewTitle]:
    if not sheet.sheet_no:
        return []  # nothing to index a title under without a sheet identifier
    titles = []
    for w in sheet.words:
        kind = _VIEW_KEYWORDS.get(w.text.strip().upper())
        if kind is None:
            continue
        same_line = sorted(
            (ww for ww in sheet.words if ww is not w and abs(ww.bbox.y0 - w.bbox.y0) < 3),
            key=lambda ww: ww.bbox.x0,
        )
        right = [ww for ww in same_line if ww.bbox.x0 > w.bbox.x1]
        left = [ww for ww in same_line if ww.bbox.x1 <= w.bbox.x0]
        if not right:
            continue
        tag = _norm_tag(right[0].text)
        if not _TAG_RE.match(tag):
            continue
        if left and _norm_tag(left[-1].text) in _STOPWORDS:
            continue  # "REFER SECTION 1" etc. — a note reference, not a title
        titles.append(
            _ViewTitle(
                kind=kind,
                tag=tag,
                bbox=BBox(w.bbox.x0, w.bbox.y0, right[0].bbox.x1, right[0].bbox.y1),
                sheet_no=sheet.sheet_no,
                page_index=sheet.page_index,
            )
        )
    return titles


def _extract_markers(sheet: Sheet) -> list[_Marker]:
    # Restrict to the drawing body — the title block band carries its own
    # dense short numeric fields (PROJECT No., DESIGN No., dates) that
    # would otherwise false-positive-pair with each other.
    band_y0 = sheet.page_height * (1 - TITLE_BLOCK_BAND_FRACTION)
    body_words = [w for w in sheet.words if w.bbox.y0 < band_y0]
    tag_words = [w for w in body_words if _TAG_RE.match(_norm_tag(w.text))]

    # A sheet number cited in a general-note cross-reference sentence
    # ("... REFER TO SHEET No. 2871005 TO 2871008.") reads identically to
    # a marker's own sheet-number word, but sits inline in a sentence
    # rather than stacked with a tag — confirmed against the sample, this
    # exact note appears on most sheets and, without this filter, its
    # digits false-pair with any scale-bar tick label or note number that
    # happens to land within the stacking threshold nearby. Excluding a
    # sheet-number word immediately preceded by "No."/"No" or immediately
    # followed by "TO" on the same line removes that whole class rather
    # than chasing each coincidental pairing individually.
    def _is_note_reference(sw) -> bool:
        same_line = [w for w in body_words if w is not sw and abs(w.bbox.y0 - sw.bbox.y0) < 3]
        left = [w for w in same_line if w.bbox.x1 <= sw.bbox.x0]
        right = [w for w in same_line if w.bbox.x0 >= sw.bbox.x1]
        if left and _norm_tag(left[-1].text.rstrip(".")) == "NO":
            return True
        if right and _norm_tag(right[0].text) == "TO":
            return True
        return False

    sheet_no_words = [
        w for w in body_words if _SHEET_NO_RE.match(w.text) and not _is_note_reference(w)
    ]

    markers = []
    for sw in sheet_no_words:
        best, best_dist = None, None
        for tw in tag_words:
            dx = abs(tw.bbox.x0 - sw.bbox.x0)
            if tw.bbox.y1 <= sw.bbox.y0:
                dy = sw.bbox.y0 - tw.bbox.y1
            elif sw.bbox.y1 <= tw.bbox.y0:
                dy = tw.bbox.y0 - sw.bbox.y1
            else:
                continue  # vertically overlapping — not a stacked pair
            if dx > _MARKER_MAX_DX or dy > _MARKER_MAX_DY:
                continue
            dist = dx + dy
            if best is None or dist < best_dist:
                best, best_dist = tw, dist
        if best is not None:
            bbox = BBox(
                min(sw.bbox.x0, best.bbox.x0),
                min(sw.bbox.y0, best.bbox.y0),
                max(sw.bbox.x1, best.bbox.x1),
                max(sw.bbox.y1, best.bbox.y1),
            )
            markers.append(
                _Marker(
                    tag=_norm_tag(best.text),
                    sheet_hint=sw.text,
                    bbox=bbox,
                    sheet_no=sheet.sheet_no,
                    page_index=sheet.page_index,
                )
            )
    return markers


def build_references(project: Project) -> list[Reference]:
    """Whole-project pass: index every view title across the set (step 1
    of PLANNING.md §4's resolution algorithm), then resolve every marker
    that isn't itself sitting beside a matching title on its own sheet
    (steps 2-5). Both resolved and unresolved references are returned —
    §4 step 5 turns only the unresolved ones into Issues; the resolved
    set is kept for reuse by the geometry engine's cross-view matching
    (§5a), same as the rest of this IR."""

    all_titles: list[_ViewTitle] = []
    all_markers: list[_Marker] = []
    for sheet in project.sheets:
        all_titles.extend(_extract_view_titles(sheet))
        all_markers.extend(_extract_markers(sheet))

    title_index: dict[tuple[str, str], _ViewTitle] = {
        (t.tag, t.sheet_no): t for t in all_titles
    }
    known_sheet_nos = {s.sheet_no for s in project.sheets if s.sheet_no}

    references: list[Reference] = []
    for m in all_markers:
        is_self_marker = any(
            t.tag == m.tag
            and t.sheet_no == m.sheet_no
            and abs(t.bbox.x0 - m.bbox.x0) < _SELF_MARKER_MAX_DIST
            and abs(t.bbox.y0 - m.bbox.y0) < _SELF_MARKER_MAX_DIST
            for t in all_titles
        )
        if is_self_marker:
            continue  # the view's own identifying marker, not a callout

        target = title_index.get((m.tag, m.sheet_hint))
        if target is not None:
            references.append(
                Reference(
                    ref_type=target.kind,
                    tag=m.tag,
                    source_sheet_no=m.sheet_no,
                    source_page_index=m.page_index,
                    source_bbox=m.bbox,
                    target_sheet_hint=m.sheet_hint,
                    resolved=True,
                    target_sheet_no=target.sheet_no,
                    target_bbox=target.bbox,
                    confidence=1.0,
                )
            )
        else:
            # §4 step 4: no exact match found. The referenced sheet not
            # existing at all in the set is a stronger, more confident
            # signal of a real error than the sheet existing but this
            # particular tag not being found on it (which could still be
            # this heuristic's own extraction miss) — reflected in
            # confidence, never silently upgraded to resolved either way.
            confidence = 0.0 if m.sheet_hint not in known_sheet_nos else 0.3
            references.append(
                Reference(
                    ref_type="unknown",
                    tag=m.tag,
                    source_sheet_no=m.sheet_no,
                    source_page_index=m.page_index,
                    source_bbox=m.bbox,
                    target_sheet_hint=m.sheet_hint,
                    resolved=False,
                    confidence=confidence,
                )
            )
    return references
