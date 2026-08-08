"""Table extraction (pdfplumber) + classification, and revision-schedule
extraction (word-clustering, not pdfplumber's table detector).

PLANNING.md §3 lists three table kinds the IR cares about: setout/coordinate
tables, revision tables, and schedules. Generic table extraction pulls out
every ruled table pdfplumber finds on the page; the revision schedule is
handled separately (see below) because it doesn't have the ruling-line grid
pdfplumber's default detector expects.
"""

from __future__ import annotations

from collections import defaultdict

import pdfplumber

from pdfchecker.ir import BBox, RevisionEntry, Table, TextWord

# "AMENDMENT" is the identifying keyword, not "DESCRIPTION" — other tables
# on a sheet legitimately have their own DESCRIPTION column (e.g. a legend
# table), which produced a false-positive match in the first pass against
# samples/T2DPAA-T2D-C3S-BR-DRG-101000.pdf. "AMENDMENT" alone is specific
# enough to a revision schedule's header to use as the sole signal.
REVISION_HEADER_KEYWORD = "AMENDMENT"


def _classify(header_row: list[str | None]) -> str:
    joined = " ".join((c or "").strip().upper() for c in header_row)
    if "EASTING" in joined or "NORTHING" in joined:
        return "schedule"  # setout/coordinate-flavoured schedule, e.g. a pile schedule
    return "unknown"


# A full-sheet border/grid on a CAD-style drawing can fool pdfplumber's
# default line-based table finder into treating the *entire page* as one
# giant table (confirmed against page 15 of the sample — find_tables()
# returned a table spanning ~98% of the page, merging drawing body, title
# block, and revision row into one bogus row). A real setout/revision
# table is always a sub-region of the sheet, so anything covering nearly
# the whole page is discarded as this artifact rather than a genuine table.
_BOGUS_FULL_PAGE_FRACTION = 0.9


def extract_tables(pdf_page: pdfplumber.page.Page) -> list[Table]:
    """Generic ruled-table extraction — pile schedules, setout tables, etc.
    (anything with a proper ruling-line grid, which this handles fine).
    The revision schedule is deliberately excluded here; see
    extract_revision_schedule below for why it needs different handling."""

    tables = []
    for t in pdf_page.find_tables():
        rows = t.extract()
        if not rows:
            continue
        x0, top, x1, bottom = t.bbox
        if (x1 - x0) > pdf_page.width * _BOGUS_FULL_PAGE_FRACTION and (
            bottom - top
        ) > pdf_page.height * _BOGUS_FULL_PAGE_FRACTION:
            continue
        kind = _classify(rows[0])
        tables.append(
            Table(bbox=BBox(x0=x0, y0=top, x1=x1, y1=bottom), rows=rows, kind=kind)
        )
    return tables


# Revision schedule region: bottom-left of the sheet (PLANNING.md §4's
# "bottom-left convention"), as a fraction of page size.
REVISION_REGION = {"x1_frac": 0.40, "y0_frac": 0.90}

# Words on the same visual row can differ in y0 by a couple of points
# (font-metric noise, confirmed while calibrating the title block) —
# cluster rows by y0 rounded to this many points, not exact equality.
_ROW_CLUSTER_PX = 4

# Expected header columns, left to right — PLANNING.md §4 treats this as a
# project-configurable column mapping; this is the default, calibrated
# against samples/T2DPAA-T2D-C3S-BR-DRG-101000.pdf. Matched by exact word
# text against the header row, NOT by inferring column groups from x-gaps
# between header words — this table's header is tightly packed enough
# that inter-column gaps (e.g. "CHECK" -> "ACCEPTANCE" ~6.6pt) are smaller
# than intra-label word-spacing elsewhere (e.g. "AMENDMENT" ->
# "DESCRIPTION" ~2.8pt is close but not reliably smaller), so no single
# gap threshold separates "words in one label" from "adjacent short
# column labels" — confirmed by a first pass that merged
# CHECK+ACCEPTANCE+DATE into one column and silently dropped all three.
DEFAULT_REVISION_COLUMNS = ["NO.", "AMENDMENT DESCRIPTION", "BY", "CHECK", "ACCEPTANCE", "DATE"]


def _match_header_columns(
    header_row_words: list[TextWord], column_names: list[str]
) -> list[list]:
    """Match each expected column name to a contiguous run of header words,
    left to right, consuming words as they're matched so e.g. "AMENDMENT"
    and "DESCRIPTION" aren't each also considered for other columns."""

    normalized = [
        [tok.strip(".:").upper() for tok in name.split()] for name in column_names
    ]
    word_tokens = [w.text.strip(".:").upper() for w in header_row_words]

    columns = []
    cursor = 0
    for name, tokens in zip(column_names, normalized):
        n = len(tokens)
        match_start = None
        for start in range(cursor, len(word_tokens) - n + 1):
            if word_tokens[start : start + n] == tokens:
                match_start = start
                break
        if match_start is None:
            continue  # this column's label wasn't found — leave it unmatched
        matched_words = header_row_words[match_start : match_start + n]
        columns.append([name, matched_words[0].bbox.x0, matched_words[-1].bbox.x1])
        cursor = match_start + n
    return columns


def extract_revision_schedule(
    words: list[TextWord], page_width: float, page_height: float
) -> list[RevisionEntry]:
    """Word-clustering extraction, not pdfplumber's table detector — this
    region's ruling lines don't form the grid pdfplumber's default
    strategy needs (confirmed against the sample: it fragmented the row
    into five single-column mini-tables instead of one). This reuses the
    same label-anchored philosophy as titleblock.py: read words by
    position, not by assuming a rule-line grid exists."""

    band_y0 = page_height * REVISION_REGION["y0_frac"]
    band_x1 = page_width * REVISION_REGION["x1_frac"]
    region_words = [w for w in words if w.bbox.y0 >= band_y0 and w.bbox.x0 <= band_x1]
    if not region_words:
        return []

    header_words = [w for w in region_words if w.text.strip(".:").upper() == REVISION_HEADER_KEYWORD]
    if not header_words:
        return []  # no revision schedule found on this sheet
    header_y_key = round(header_words[0].bbox.y0 / _ROW_CLUSTER_PX)

    rows_by_y: dict[int, list[TextWord]] = defaultdict(list)
    for w in region_words:
        rows_by_y[round(w.bbox.y0 / _ROW_CLUSTER_PX)].append(w)

    header_row_words = sorted(rows_by_y.pop(header_y_key, []), key=lambda w: w.bbox.x0)
    if not header_row_words:
        return []

    columns = _match_header_columns(header_row_words, DEFAULT_REVISION_COLUMNS)
    if not columns:
        return []

    # The description column's header label sits well INSIDE its column
    # rather than flush against its left edge (confirmed against the
    # sample: "AMENDMENT DESCRIPTION" prints starting at x=197, but the
    # column's actual data — the free-text description — starts right
    # after "No." ends, around x=80). Using the label's own x0 for that
    # column's left boundary put a ~120pt no-man's-land in between and
    # misassigned it to "NO." instead. So the description column's left
    # boundary is anchored to the *previous* (narrow, label-aligned)
    # column's right edge instead of its own label position — this is a
    # description-specific fix, not a general one, because a free-text
    # column is exactly the case where "wide content, indented label" is
    # expected; the other columns here (No./By/Check/Acceptance/Date) are
    # all short values that do align with their labels.
    boundary_x0 = [x0 for _, x0, _ in columns]
    for i, (label, _, _) in enumerate(columns):
        if i > 0 and REVISION_HEADER_KEYWORD in label:
            boundary_x0[i] = columns[i - 1][2]

    boundaries = [float("-inf")]
    for (_, _, x1), next_x0 in zip(columns, boundary_x0[1:]):
        boundaries.append((x1 + next_x0) / 2)
    boundaries.append(float("inf"))

    def column_for_x(x: float) -> str | None:
        for i, (label, _, _) in enumerate(columns):
            if boundaries[i] <= x < boundaries[i + 1]:
                return label
        return None

    def get(cells: dict[str, list[str]], *names: str) -> str | None:
        for name in names:
            for label, _, _ in columns:
                if label == name and label in cells:
                    return " ".join(cells[label])
        return None

    entries = []
    for y_key in sorted(rows_by_y.keys()):
        row_words = sorted(rows_by_y[y_key], key=lambda w: w.bbox.x0)
        cells: dict[str, list[str]] = defaultdict(list)
        for w in row_words:
            col = column_for_x(w.bbox.x0)
            if col:
                cells[col].append(w.text)
        if not cells:
            continue
        rev_id = get(cells, "NO.", "NO")
        desc = get(cells, "AMENDMENT DESCRIPTION", "AMENDMENT", "DESCRIPTION")
        if not rev_id and not desc:
            continue
        entries.append(
            RevisionEntry(
                rev_id=rev_id or "",
                description=desc or "",
                by=get(cells, "BY"),
                check=get(cells, "CHECK"),
                acceptance=get(cells, "ACCEPTANCE"),
                date=get(cells, "DATE"),
            )
        )
    return entries
