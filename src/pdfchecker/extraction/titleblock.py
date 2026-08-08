"""Title-block field extraction — label-anchored, not fixed-position.

PLANNING.md §4 ("Project rule configuration — consolidated schema") treats
the title-block field list as project-extensible, not fixed — a client can
have a field like lat/long that isn't one of the standard ones. This module
implements that: each field is a (label text -> search rule), and a project
config can add more without touching extraction code.

Calibrated against samples/T2DPAA-T2D-C3S-BR-DRG-101000.pdf (see PLANNING.md
CLAUDE.md rule: check real samples before assuming a layout). That sheet set
uses a South Australian DIT-style title block: revision schedule bottom-left,
main block bottom-right, with per-field values sitting either just below or
immediately to the right of their label — both patterns are common across
firms' templates, so both are supported.

IMPORTANT: label text like "SHEET No." also appears elsewhere on a sheet
(e.g. a "FOR PILING NOTES, REFER TO SHEET No. 2871056" cross-reference in
body notes) — so search is restricted to the title-block band at the bottom
of the page, not the whole page, or a note referencing another sheet would
get misread as this sheet's own Sheet No.
"""

from __future__ import annotations

from dataclasses import dataclass

import fitz

from pdfchecker.ir import TextWord, TitleBlock

# Fraction of page height, from the bottom, that the title block band
# occupies. Calibrated at ~7.3% on the sample; 10% gives slack without
# risking picking up body-text cross-references (§4's "No match: <ref>"
# reference graph territory — deliberately not this module's job).
TITLE_BLOCK_BAND_FRACTION = 0.10


@dataclass(frozen=True)
class FieldSpec:
    name: str
    label: str
    direction: str  # "below" | "right"
    max_dx: float = 90.0
    max_dy: float = 30.0


# Default field set for this project's title block convention. A project
# config can append more (PLANNING.md §4's `title_block.custom_fields`) —
# this list is the starting point, not a hardcoded ceiling.
DEFAULT_FIELD_SPECS: list[FieldSpec] = [
    FieldSpec("drawing_no", "DRAWING No.", direction="below", max_dx=45, max_dy=15),
    FieldSpec("sheet_no", "SHEET No.", direction="below", max_dx=45, max_dy=15),
    FieldSpec("amend_no", "AMEND No.", direction="below", max_dx=45, max_dy=15),
    FieldSpec("designed_by", "DESIGNED", direction="below", max_dx=45, max_dy=30),
    FieldSpec("drafted_by", "DRAFTED", direction="below", max_dx=45, max_dy=30),
    FieldSpec("accepted_by", "ACCEPTED FOR USE", direction="below", max_dx=90, max_dy=30),
    FieldSpec("sheet_latitude", "SHEET LATITUDE", direction="right", max_dx=80, max_dy=6),
    FieldSpec("sheet_longitude", "SHEET LONGITUDE", direction="right", max_dx=80, max_dy=6),
]


def _label_bbox_in_band(page: fitz.Page, label: str, band_y0: float) -> fitz.Rect | None:
    hits = [r for r in page.search_for(label) if r.y0 >= band_y0]
    if not hits:
        return None
    # If a label legitimately appears more than once within the band, take
    # the leftmost occurrence — title blocks read left-to-right/top-to-
    # bottom and the leftmost hit is consistently the field label itself
    # rather than a value that happens to echo the label text.
    hits.sort(key=lambda r: r.x0)
    return hits[0]


def _label_tokens(label: str) -> set[str]:
    return {tok.strip(".:").upper() for tok in label.split()}


def _value_near(words: list[TextWord], label_rect: fitz.Rect, spec: FieldSpec) -> str | None:
    if spec.direction == "below":
        x_lo, x_hi = label_rect.x0 - 10, label_rect.x0 + spec.max_dx
        # A large-font value's glyph bounding box can start well above the
        # small label's baseline (its ascender reaches higher) and can
        # spatially overlap the label's own bbox — calibrated against
        # "DRAWING No." -> "8011" on the sample, where the value's box
        # overlaps the label's box on both axes despite being the correct
        # value. So the window has to start at the label's own top, and the
        # label's own words are excluded by matching text tokens, not by
        # bounding-box overlap (which would wrongly exclude this case too).
        y_lo, y_hi = label_rect.y0, label_rect.y1 + spec.max_dy
    else:  # "right"
        x_lo, x_hi = label_rect.x1, label_rect.x1 + spec.max_dx
        y_lo, y_hi = label_rect.y0 - spec.max_dy, label_rect.y1 + spec.max_dy

    label_tokens = _label_tokens(spec.label)
    candidates = [
        w for w in words
        if x_lo <= w.bbox.x0 <= x_hi
        and y_lo <= w.bbox.y0 <= y_hi
        and w.text.strip(".:").upper() not in label_tokens
    ]
    if not candidates:
        return None

    # Nearest line first: group by y0 (rounded), take the closest group to
    # the label, then read left-to-right within it.
    candidates.sort(key=lambda w: (round(w.bbox.y0 / 4), w.bbox.x0))
    nearest_y = round(candidates[0].bbox.y0 / 4)
    line = [w for w in candidates if round(w.bbox.y0 / 4) == nearest_y]
    line.sort(key=lambda w: w.bbox.x0)
    return " ".join(w.text for w in line)


def extract_title_block(
    page: fitz.Page,
    words: list[TextWord],
    field_specs: list[FieldSpec] = DEFAULT_FIELD_SPECS,
) -> TitleBlock:
    band_y0 = page.rect.height * (1 - TITLE_BLOCK_BAND_FRACTION)
    fields: dict[str, str] = {}
    for spec in field_specs:
        label_rect = _label_bbox_in_band(page, spec.label, band_y0)
        if label_rect is None:
            continue
        value = _value_near(words, label_rect, spec)
        if value:
            fields[spec.name] = value
    return TitleBlock(fields=fields)
