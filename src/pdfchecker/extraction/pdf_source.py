"""Low-level per-page extraction from a PDF via PyMuPDF (`fitz`).

This is the "vector entities / text (native)" step of the pipeline in
PLANNING.md §2 — raw content off the page, no normalization or check-engine
logic yet. Verified against samples/T2DPAA-T2D-C3S-BR-DRG-101000.pdf: it's a
true vector PDF (no raster images, no OCR needed on this sample), so this
module doesn't attempt OCR fallback yet — see PLANNING.md §1/§10 for where
that would plug in for scanned sheets.
"""

from __future__ import annotations

import fitz

from pdfchecker.ir import BBox, PathEntity, TextWord


def extract_words(page: fitz.Page) -> list[TextWord]:
    """Word-level text with position. PyMuPDF's word tuples are
    (x0, y0, x1, y1, text, block_no, line_no, word_no); position is what
    lets every downstream Issue carry a precise location, not just a page
    number (CLAUDE.md's Issue-location requirement)."""

    words = page.get_text("words")
    return [
        TextWord(text=w[4], bbox=BBox(x0=w[0], y0=w[1], x1=w[2], y1=w[3]))
        for w in words
    ]


def extract_paths(page: fitz.Page) -> list[PathEntity]:
    """Vector drawings (lines/curves/rects) on the page. Kept generic —
    classifying these into dimensions, witness lines, revision clouds, etc.
    is check-engine work (§4, §5), not ingestion. `page.get_drawings()`
    already gives each path's own bounding rect, which is what we store."""

    paths = []
    for d in page.get_drawings():
        rect = d.get("rect")
        if rect is None:
            continue
        paths.append(
            PathEntity(
                bbox=BBox(x0=rect.x0, y0=rect.y0, x1=rect.x1, y1=rect.y1),
                kind=d.get("type", "unknown"),
                stroke_width=d.get("width"),
            )
        )
    return paths
