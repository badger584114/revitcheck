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

from pdfchecker.extraction.pdf_source import extract_words  # noqa: E402
from pdfchecker.extraction.titleblock import (  # noqa: E402
    FieldSpec,
    _looks_like_a_label,
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
