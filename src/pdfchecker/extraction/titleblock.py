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

# Band scanned by the cell-based reader below. Wider than the spec band
# because title blocks vary in depth — T2DPAA's occupies ~7% of the sheet,
# Flinders' closer to 20%.
DISCOVERY_BAND_FRACTION = 0.25


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


def _looks_like_a_label(text: str) -> bool:
    """Is this token itself a field label rather than a value?

    Real bug this exists for (found 2026-08-17 against samples/Flinders/,
    a different client's title block): `DRAFTED:` sits 17.3pt directly
    below `DESIGNED:` with dx=0, comfortably inside the `below` search
    window, so `designed_by` came out as the string `"DRAFTED:"`. The
    same layout gave `drafted_by == "CHECKED:"`. Nothing had stopped a
    *label* being taken as a *value* — on the T2DPAA sheets the fields
    are spaced far enough apart that it never happened, so two samples
    from one client never exposed it.

    Silently-wrong data is worse than no data here: a missing field is
    caught by `title_block.required_fields_present`, whereas a plausible
    wrong value flows into revision cross-checks and the cross-sheet
    reference graph unchallenged."""

    stripped = text.strip()
    # A bare ":" is punctuation, not a label — it appears inside real
    # values (a scale reads "1 : 100"), and treating it as a label dropped
    # the separator, giving "1 100".
    if not any(c.isalnum() for c in stripped):
        return False
    return stripped.endswith(":") or stripped.upper().rstrip(".:") == "NO"


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
        and not _looks_like_a_label(w.text)
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
    paths=None,
) -> TitleBlock:
    """Title-block fields, by two strategies in order of precision.

    1. `field_specs` — calibrated label + direction + window per field.
       Precise where the labels match, which is every T2DPAA sheet.
    2. `extract_by_cells` — reads whatever labels the sheet has and bounds
       each value with the title block's own ruled grid. This is what
       covers a client whose labels the spec list has never seen, and it
       only runs where a real grid exists (see that function).

    Specs win on conflict: they are calibrated against a known layout,
    while the cell reader infers one. Cells fill in whatever specs
    missed — which on a foreign title block is everything.

    `paths` is the page's vector paths (`extraction/pdf_source.py`); the
    caller already has them, so they're passed rather than re-extracted.
    Omitted, strategy 2 is skipped and behaviour is exactly as before."""

    band_y0 = page.rect.height * (1 - TITLE_BLOCK_BAND_FRACTION)
    fields: dict[str, str] = {}
    for spec in field_specs:
        label_rect = _label_bbox_in_band(page, spec.label, band_y0)
        if label_rect is None:
            continue
        value = _value_near(words, label_rect, spec)
        if value:
            fields[spec.name] = value

    if paths:
        # A wider band than the spec path uses: title blocks vary in depth
        # (T2DPAA's is ~7% of sheet height, Flinders' closer to 20%), and
        # the grid requirement in extract_by_cells is what keeps this from
        # wandering into drawing-body linework.
        discovery_band = page.rect.height * (1 - DISCOVERY_BAND_FRACTION)
        canonical, _discovered = extract_by_cells(words, paths, discovery_band)
        for name, value in canonical.items():
            fields.setdefault(name, value)

    return TitleBlock(fields=fields)


# --- Cell-based extraction (dynamic, layout-driven) ---------------------
#
# `DEFAULT_FIELD_SPECS` above says *which labels to look for*, which only
# covers title blocks that use those exact labels. Confirmed inadequate
# 2026-08-17 against a second client (Flinders / CS1-DRG-*): six of the
# eight expected labels don't appear on those sheets at all, including all
# three fields `RuleConfig` requires — that client writes `REVISION:`
# where T2DPAA writes `AMEND No.`, and `SHEET: 6 OF 23` where T2DPAA
# writes `SHEET No.`.
#
# This half works the other way round: read whatever labels the sheet
# actually has, and use the title block's **own ruled cells** to bound
# each value.
#
# **Why cells rather than proximity.** A text-proximity version was built
# first and rejected: title blocks pack fields side by side, so a
# "nearest text, same row" rule cannot tell where one field's value ends
# and the neighbour's label begins. It produced `amend_no = "0 6 OF 23"`
# and `date = "28.03.26 IN ACCORDANCE WITH DP013 SHEET LATITUDE ..."` —
# roughly half the fields right, which is worse than useless for feeding
# checks. A cell bounds the value exactly: the same sheet gives
# `REVISION: -> "0"`, `SIZE: -> "A1"`, `DATE: -> "21/03/2019"`.
#
# **Not universally available, which is why this is a strategy and not a
# replacement.** Flinders' title block is a real ruled grid (51 vertical
# and 40 horizontal rules on a sheet). BR06's is not — it has 5 vertical
# rules in the whole band, and its box-shaped paths are full-width bands
# (the smallest one containing both the "DRAWING No." label and its
# "8011" value is 2130x40pt, i.e. the entire block) plus small text
# background fills. So BR06 is laid out positionally, and the calibrated
# specs above remain the right mechanism there. `extract_title_block`
# tries specs first and falls back to cells for whatever they missed.

# A grid needs enough rules in both directions to be a grid rather than a
# few decorative lines. BR06 has 5 vertical rules (not a grid), Flinders
# 43 (clearly one) — 8 sits well clear of both.
MIN_GRID_LINES = 8

# Rules drawn as separate segments can be a fraction of a point apart;
# snap coordinates together before treating them as one grid line.
_GRID_SNAP_PT = 2.0

# Discovered label text (normalised) -> canonical field name. The one
# place client vocabulary lives, and deliberately data rather than logic
# so a new client's wording is a one-line addition rather than new code.
# Sources: T2DPAA (BR06/BR08) and CS1 (Flinders) real sheets, plus common
# industry variants that cost nothing to accept.
LABEL_SYNONYMS: dict[str, str] = {
    "DRAWING NO": "drawing_no", "DRG NO": "drawing_no", "DWG NO": "drawing_no",
    "DRAWING NUMBER": "drawing_no",
    "SHEET NO": "sheet_no", "SHEET NUMBER": "sheet_no",
    "AMEND NO": "amend_no", "AMENDMENT NO": "amend_no", "REVISION": "amend_no",
    "REV": "amend_no", "REV NO": "amend_no", "ISSUE": "amend_no",
    "DESIGNED": "designed_by", "DESIGNED BY": "designed_by",
    "ORIGINATE/DESIGN": "designed_by",
    "DRAFTED": "drafted_by", "DRAWN": "drafted_by", "DRAFTED BY": "drafted_by",
    "CHECKED": "checked_by", "CHECK": "checked_by",
    "ACCEPTED": "accepted_by", "ACCEPTED FOR USE": "accepted_by",
    "SCALE": "scale", "SCALES": "scale", "SCALE(S)": "scale",
    "DATE": "date",
    "SIZE": "sheet_size",
    "SHEET": "sheet_of",  # Flinders' "SHEET: 6 OF 23" — an index, not a sheet id
}


def _normalise_label(text: str) -> str:
    return " ".join(text.replace(":", " ").split()).upper().rstrip(".")


def _snap(values: list[float]) -> list[float]:
    """Collapses near-identical coordinates into single grid lines."""

    out: list[float] = []
    for v in sorted(values):
        if not out or v - out[-1] > _GRID_SNAP_PT:
            out.append(v)
    return out


def build_grid(paths, band_y0: float) -> tuple[list[float], list[float]]:
    """`(horizontal_ys, vertical_xs)` for the title block's ruled grid.

    Rules are recognised by shape — a path far longer in one axis than the
    other — rather than by any per-client convention."""

    hs = [p.bbox.y0 for p in paths if p.bbox.y0 >= band_y0 and p.bbox.height < 2 and p.bbox.width > 20]
    vs = [p.bbox.x0 for p in paths if p.bbox.y0 >= band_y0 and p.bbox.width < 2 and p.bbox.height > 8]
    return _snap(hs), _snap(vs)


def has_usable_grid(hs: list[float], vs: list[float]) -> bool:
    return len(hs) >= MIN_GRID_LINES and len(vs) >= MIN_GRID_LINES


def _cell_bounds(cx: float, cy: float, hs: list[float], vs: list[float]):
    """The grid cell containing a point, or `None` at the grid's edge."""

    left = max((x for x in vs if x <= cx), default=None)
    right = min((x for x in vs if x >= cx), default=None)
    top = max((y for y in hs if y <= cy), default=None)
    bottom = min((y for y in hs if y >= cy), default=None)
    if None in (left, right, top, bottom):
        return None
    return left, top, right, bottom


def _text_in_cell(words: list[TextWord], cell, exclude_ids: set) -> str | None:
    left, top, right, bottom = cell
    inside = [
        w for w in words
        if id(w) not in exclude_ids
        and left <= (w.bbox.x0 + w.bbox.x1) / 2 <= right
        and top <= (w.bbox.y0 + w.bbox.y1) / 2 <= bottom
        and w.text.strip()
        and not _looks_like_a_label(w.text)
    ]
    if not inside:
        return None
    inside.sort(key=lambda w: (round(w.bbox.y0 / 4), w.bbox.x0))
    return " ".join(w.text for w in inside).strip() or None


def extract_by_cells(words: list[TextWord], paths, band_y0: float) -> tuple[dict, dict]:
    """`(canonical_fields, all_discovered)` read off the ruled grid.

    A label's value is normally in its own cell; where the cell holds only
    the label, the cell directly below and then to the right are tried,
    which is the other common title-block arrangement (confirmed on
    Flinders, where `ACCEPTED:` labels a cell whose value sits beneath).

    `all_discovered` keeps every label/value pair found, keyed by the
    label as printed — an unrecognised label is real information (it is
    how you learn a client writes `ORIGINATE/DESIGN:`) and dropping it
    would hide exactly what's needed to extend `LABEL_SYNONYMS`."""

    hs, vs = build_grid(paths, band_y0)
    if not has_usable_grid(hs, vs):
        return {}, {}

    label_words = [
        w for w in words
        if w.bbox.y0 >= band_y0 and w.text.strip() and _looks_like_a_label(w.text)
    ]
    label_ids = {id(w) for w in label_words}

    canonical: dict[str, str] = {}
    discovered: dict[str, str] = {}
    for w in label_words:
        cx, cy = (w.bbox.x0 + w.bbox.x1) / 2, (w.bbox.y0 + w.bbox.y1) / 2
        cell = _cell_bounds(cx, cy, hs, vs)
        if cell is None:
            continue
        value = _text_in_cell(words, cell, label_ids)
        if value is None:  # label-only cell — look below, then right
            below = _cell_bounds(cx, cell[3] + 1, hs, vs)
            right = _cell_bounds(cell[2] + 1, cy, hs, vs)
            for neighbour in (below, right):
                if neighbour is not None:
                    value = _text_in_cell(words, neighbour, label_ids)
                    if value:
                        break
        if not value:
            continue
        label = _normalise_label(w.text)
        discovered.setdefault(label, value)
        name = LABEL_SYNONYMS.get(label)
        if name and name not in canonical:
            canonical[name] = value
    return canonical, discovered
