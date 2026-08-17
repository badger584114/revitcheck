"""extraction/titleblock.py — label-anchored field extraction.

Synthetic PDFs rather than the real sample: these exercise *layout*
behaviour, and building a two-line title block with fitz is both faster
and lets the tricky geometry be stated explicitly. Real-sample coverage
lives in tests/test_ingest_sample.py, which asserts BR06's eight actual
title-block values.
"""

from __future__ import annotations

import sys
from pathlib import Path

import fitz

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.extraction.pdf_source import extract_paths, extract_words  # noqa: E402
from pdfchecker.extraction.titleblock import (  # noqa: E402
    FieldSpec,
    _looks_like_a_label,
    extract_prominent_sheet_id,
    extract_title_block,
)

_W, _H = 800.0, 600.0


def _page_with(tmp_path, entries):
    """A one-page PDF with text at given (x, y) positions. Coordinates are
    absolute so a test can place a label directly beneath another."""

    path = tmp_path / "tb.pdf"
    doc = fitz.open()
    page = doc.new_page(width=_W, height=_H)
    for text, x, y in entries:
        page.insert_text((x, y), text, fontsize=7)
    doc.save(str(path))
    doc.close()
    doc = fitz.open(str(path))
    return doc, doc[0]


class TestLabelGuard:
    """The `_looks_like_a_label` guard, added 2026-08-17.

    Found against a second client's title block (samples/Flinders/): its
    `DRAFTED:` sits 17.3pt directly below `DESIGNED:` with dx=0, well
    inside the `below` search window, so `designed_by` came out as the
    literal string `"DRAFTED:"` and `drafted_by` as `"CHECKED:"`. Nothing
    prevented a *label* being read as a *value*. On the T2DPAA sheets the
    fields sit far enough apart that it never triggered — two samples from
    one client couldn't expose it.

    This matters more than a missed field: an absent value is caught by
    `title_block.required_fields_present`, whereas a confidently wrong one
    flows into the revision cross-checks and reference graph unchallenged.
    """

    def test_recognises_colon_and_number_labels(self):
        for text in ("DRAFTED:", "REVISION:", "SHEET:", "No.", "No:"):
            assert _looks_like_a_label(text), text

    def test_ordinary_values_are_not_labels(self):
        for text in ("FLD", "8011", "2871051", "M. SHORT", "21/03/2019"):
            assert not _looks_like_a_label(text), text

    def test_stacked_labels_do_not_become_each_others_values(self, tmp_path):
        """The exact Flinders geometry: label, then another label 17pt
        directly below it, and no real value anywhere near."""

        doc, page = _page_with(
            tmp_path, [("DESIGNED:", 400, 560), ("DRAFTED:", 400, 577)]
        )
        specs = [FieldSpec("designed_by", "DESIGNED", direction="below", max_dx=45, max_dy=30)]
        fields = extract_title_block(page, extract_words(page), specs).fields
        doc.close()
        assert "designed_by" not in fields, f"took a label as a value: {fields}"

    def test_a_real_value_below_a_label_is_still_found(self, tmp_path):
        """The guard must not cost the normal case — this is the layout it
        has to keep working."""

        doc, page = _page_with(
            tmp_path, [("DESIGNED:", 400, 560), ("FLD", 400, 577)]
        )
        specs = [FieldSpec("designed_by", "DESIGNED", direction="below", max_dx=45, max_dy=30)]
        fields = extract_title_block(page, extract_words(page), specs).fields
        doc.close()
        assert fields.get("designed_by") == "FLD"

    def test_value_search_skips_a_label_to_reach_a_real_value(self, tmp_path):
        """A label between the anchor and its value shouldn't block it —
        the guard excludes the label from candidates rather than
        terminating the search."""

        doc, page = _page_with(
            tmp_path,
            [("DESIGNED:", 400, 560), ("CHECKED:", 460, 560), ("FLD", 400, 577)],
        )
        specs = [FieldSpec("designed_by", "DESIGNED", direction="below", max_dx=45, max_dy=30)]
        fields = extract_title_block(page, extract_words(page), specs).fields
        doc.close()
        assert fields.get("designed_by") == "FLD"


class TestCellBasedExtraction:
    """`extract_by_cells` — the layout-driven strategy added 2026-08-17 so
    a title block whose labels `DEFAULT_FIELD_SPECS` has never seen still
    yields fields.

    Built after a text-proximity version was tried and rejected: title
    blocks pack fields side by side, so "nearest text on the same row"
    can't tell where one value ends and the neighbour's label begins. It
    produced `amend_no = "0 6 OF 23"` on a real sheet. A ruled cell bounds
    the value exactly.
    """

    def _ruled_page(self, tmp_path, cells, texts, size=(700.0, 600.0)):
        """A page with a real ruled grid: `cells` are (x0, y0, x1, y1)
        rectangles drawn as lines, `texts` are (str, x, y)."""

        w, h = size
        path = tmp_path / "grid.pdf"
        doc = fitz.open()
        page = doc.new_page(width=w, height=h)
        xs = sorted({v for c in cells for v in (c[0], c[2])})
        ys = sorted({v for c in cells for v in (c[1], c[3])})
        # enough rules in both directions to count as a grid
        for x in xs:
            for y0, y1 in zip(ys, ys[1:]):
                page.draw_line(fitz.Point(x, y0), fitz.Point(x, y1))
        for y in ys:
            page.draw_line(fitz.Point(xs[0], y), fitz.Point(xs[-1], y))
        for text, tx, ty in texts:
            page.insert_text((tx, ty), text, fontsize=6)
        doc.save(str(path))
        doc.close()
        doc = fitz.open(str(path))
        return doc, doc[0]

    def _grid_cells(self):
        """A grid with >= MIN_GRID_LINES rules in both directions, inside
        the bottom 25% discovery band of a 700x600 page (y >= 450)."""

        xs = [40, 120, 200, 280, 360, 440, 520, 600, 660]   # 9 vertical
        ys = [460, 480, 500, 520, 540, 560, 575, 585, 595]  # 9 horizontal
        return [
            (xs[i], ys[j], xs[i + 1], ys[j + 1])
            for i in range(len(xs) - 1)
            for j in range(len(ys) - 1)
        ]

    def test_value_is_bounded_by_its_cell(self, tmp_path):
        """The whole point: a neighbouring field's text must not leak in.
        `REVISION:`/`0` and `SHEET:`/`6 OF 23` sit side by side, which is
        exactly the arrangement proximity got wrong."""

        doc, page = self._ruled_page(
            tmp_path,
            self._grid_cells(),
            [("REVISION:", 45, 475), ("0", 100, 475), ("SHEET:", 125, 475), ("6 OF 23", 205, 475)],
        )
        fields = extract_title_block(page, extract_words(page), paths=extract_paths(page)).fields
        doc.close()
        assert fields.get("amend_no") == "0"

    def test_foreign_label_maps_to_a_canonical_field(self, tmp_path):
        """`REVISION:` is this client's word for what T2DPAA calls
        `AMEND No.` — the synonym table is what makes them the same
        field."""

        doc, page = self._ruled_page(
            tmp_path, self._grid_cells(), [("REVISION:", 45, 475), ("3", 100, 475)]
        )
        fields = extract_title_block(page, extract_words(page), paths=extract_paths(page)).fields
        doc.close()
        assert fields.get("amend_no") == "3"

    def test_value_in_the_cell_below_a_label_only_cell(self, tmp_path):
        doc, page = self._ruled_page(
            tmp_path, self._grid_cells(), [("ACCEPTED:", 45, 475), ("PHIL AGNEW", 45, 495)]
        )
        fields = extract_title_block(page, extract_words(page), paths=extract_paths(page)).fields
        doc.close()
        assert fields.get("accepted_by") == "PHIL AGNEW"

    def test_no_grid_means_no_cell_extraction(self, tmp_path):
        """Not every title block is ruled — BR06's has 5 vertical rules in
        the whole band and is laid out positionally. Without a real grid
        this strategy must stand down rather than guess, leaving the
        calibrated specs in charge."""

        doc, page = _page_with(tmp_path, [("REVISION:", 400, 560), ("3", 440, 560)])
        fields = extract_title_block(page, extract_words(page), paths=extract_paths(page)).fields
        doc.close()
        assert "amend_no" not in fields

    def test_bare_colon_is_not_a_label(self, tmp_path):
        """A scale reads "1 : 100" — treating the separator as a label
        dropped it, giving "1 100"."""

        doc, page = self._ruled_page(
            tmp_path, self._grid_cells(), [("SCALE(S):", 45, 475), ("1 : 100", 100, 475)]
        )
        fields = extract_title_block(page, extract_words(page), paths=extract_paths(page)).fields
        doc.close()
        assert fields.get("scale") == "1 : 100"

    def test_specs_win_over_cells(self, tmp_path):
        """Calibrated specs are matched against a known layout; the cell
        reader infers one. Where both produce a value, the spec's wins."""

        doc, page = self._ruled_page(
            tmp_path, self._grid_cells(), [("DATE:", 45, 475), ("21/03/2019", 100, 475)]
        )
        specs = [FieldSpec("date", "DATE", direction="right", max_dx=80, max_dy=6)]
        fields = extract_title_block(page, extract_words(page), specs, paths=extract_paths(page)).fields
        doc.close()
        assert fields.get("date") == "21/03/2019"


class TestProminentSheetId:
    """`extract_prominent_sheet_id` — the last-resort strategy for a title
    block that doesn't label its sheet identifier at all.

    Confirmed on the Flinders / CS1-DRG-* set, where the sheet's own
    number is printed large in the bottom-right corner with no field label
    anywhere near it, so neither labelled strategy can reach it.

    It finds the sheet's *unique* identifier — `sheet_no`, not
    `drawing_no`. On BR06 those differ (`drawing_no` is `8011`, shared
    across the whole set), and the most prominent number is the per-sheet
    one on both clients.
    """

    def test_largest_font_wins(self, tmp_path):
        doc, page = _page_with(
            tmp_path, [("2871002", 100, 560), ("2871051", 600, 560)]
        )
        # same size here, so make the intended one bigger
        doc.close()
        path = tmp_path / "big.pdf"
        d = fitz.open()
        pg = d.new_page(width=_W, height=_H)
        pg.insert_text((100, 560), "2871002", fontsize=7)
        pg.insert_text((600, 560), "2871051", fontsize=20)
        d.save(str(path)); d.close()
        d = fitz.open(str(path)); pg = d[0]
        assert extract_prominent_sheet_id(extract_words(pg), _H * 0.75) == "2871051"
        d.close()

    def test_tie_on_size_breaks_to_the_rightmost(self, tmp_path):
        """The real BR06 case: sheet number `2871051` (22.4pt, x=0.919) is
        exactly tied on font size with an unrelated KNET reference
        `24467656` (22.4pt, x=0.811). Both clients put the identifier in
        the bottom-right corner."""

        path = tmp_path / "tie.pdf"
        d = fitz.open()
        pg = d.new_page(width=_W, height=_H)
        pg.insert_text((500, 560), "24467656", fontsize=16)
        pg.insert_text((680, 560), "2871051", fontsize=16)
        d.save(str(path)); d.close()
        d = fitz.open(str(path)); pg = d[0]
        assert extract_prominent_sheet_id(extract_words(pg), _H * 0.75) == "2871051"
        d.close()

    def _two_token_page(self, tmp_path, prefix_size, digits_size):
        path = tmp_path / f"split{prefix_size}_{digits_size}.pdf"
        d = fitz.open()
        pg = d.new_page(width=_W, height=_H)
        pg.insert_text((560, 560), "CS1-DRG-", fontsize=prefix_size)
        pg.insert_text((700, 560), "359895", fontsize=digits_size)  # clear of the prefix
        d.save(str(path)); d.close()
        d = fitz.open(str(path))
        return d, d[0]

    def test_equal_sized_split_tokens_resolve_to_the_number(self, tmp_path):
        """The real Flinders layout: `CS1-DRG-359895` is split into two
        words at the *same* size, so the rightmost tie-break lands on the
        digits directly — which is also what matches `359895.dwg`."""

        d, pg = self._two_token_page(tmp_path, 16, 16)
        assert extract_prominent_sheet_id(extract_words(pg), _H * 0.75) == "359895"
        d.close()

    def test_a_larger_prefix_is_joined_to_its_digits(self, tmp_path):
        """Defensive: where the prefix is set larger than its number, the
        prefix wins on size and would otherwise be returned alone, which
        is not an identifier."""

        d, pg = self._two_token_page(tmp_path, 16, 14)
        assert extract_prominent_sheet_id(extract_words(pg), _H * 0.75) == "CS1-DRG-359895"
        d.close()

    def test_a_prefix_with_no_number_is_not_an_identifier(self, tmp_path):
        doc, page = _page_with(tmp_path, [("CS1-DRG-", 600, 560)])
        assert extract_prominent_sheet_id(extract_words(page), _H * 0.75) is None
        doc.close()

    def test_nothing_numberlike_yields_none(self, tmp_path):
        doc, page = _page_with(tmp_path, [("ABUTMENT DETAILS", 600, 560)])
        assert extract_prominent_sheet_id(extract_words(page), _H * 0.75) is None
        doc.close()

    def test_only_consulted_when_labelled_strategies_found_nothing(self, tmp_path):
        """It must not second-guess a labelled extraction — on BR06 it
        happens to agree on all 37 pages, but specs remain authoritative."""

        doc, page = _page_with(
            tmp_path, [("SHEET No.", 100, 560), ("2871051", 100, 575), ("9999999", 700, 560)]
        )
        specs = [FieldSpec("sheet_no", "SHEET No.", direction="below", max_dx=45, max_dy=30)]
        fields = extract_title_block(page, extract_words(page), specs).fields
        doc.close()
        assert fields.get("sheet_no") == "2871051"
