"""Ties the extraction modules together: PDF path -> Project IR.

This is Stage 1's actual deliverable (PLANNING.md §9 step 1 / §2's
pipeline up to "Normalization into common Intermediate Representation") —
everything after this (rule catalog, check engines) is later stages and
deliberately not touched here.
"""

from __future__ import annotations

import fitz
import pdfplumber

from pdfchecker.extraction.pdf_source import extract_paths, extract_words
from pdfchecker.extraction.references import build_references
from pdfchecker.extraction.revision_clouds import detect_revision_clouds
from pdfchecker.extraction.tables import (
    SETOUT_TABLE_KEYWORDS,
    extract_revision_schedule,
    extract_tables,
    page_may_hold_setout_table,
)
from pdfchecker.extraction.titleblock import extract_title_block
from pdfchecker.ir import Project, Sheet


def ingest_pdf(path: str, *, table_scan_keywords=SETOUT_TABLE_KEYWORDS) -> Project:
    """PDF -> `Project` IR.

    `table_scan_keywords` gates the (expensive) ruled-table detector: a
    sheet whose text lacks any of these words is skipped and marked
    `tables_scanned=False`. Defaults to `extraction/tables.py`'s
    `SETOUT_TABLE_KEYWORDS` (Easting/Northing — the one invariant of a
    setout table, per the user 2026-08-17); pass a wider set, or `()` to
    disable the gate entirely and scan every sheet as before.

    It's a keyword argument rather than a module constant so a caller can
    widen it without editing extraction code — the nearest thing to
    ingest-time configuration available today, since `ingest_pdf` has no
    config object to read from (a real gap `checks/session_config.py`
    already reports as accepted-but-unwired for `title_block.custom_fields`
    and `revision_schedule.column_mapping`)."""

    project = Project(source_path=path)

    with fitz.open(path) as doc, pdfplumber.open(path) as plumber_doc:
        if len(doc) != len(plumber_doc.pages):
            # Would indicate the two libraries disagree on page count for
            # this file — surface it loudly rather than silently
            # misaligning page indices between the two extraction passes.
            raise ValueError(
                f"page count mismatch: fitz={len(doc)} pdfplumber={len(plumber_doc.pages)}"
            )

        for page_index in range(len(doc)):
            fitz_page = doc[page_index]
            plumber_page = plumber_doc.pages[page_index]

            words = extract_words(fitz_page)
            paths = extract_paths(fitz_page)
            title_block = extract_title_block(fitz_page, words)
            # Revision schedule is extracted by word-clustering over its
            # own bottom-left region, not pdfplumber's table detector —
            # see tables.py for why (no reliable ruling-line grid there).
            revision_schedule = extract_revision_schedule(
                words, fitz_page.rect.width, fitz_page.rect.height
            )
            # `raw_text` is needed by the table gate below, so it's read
            # here rather than inline in the Sheet(...) call.
            raw_text = fitz_page.get_text("text")
            # By far the most expensive step in ingestion — gated on a
            # cheap text test. See tables.py's page_may_hold_setout_table
            # for the profiling, the two alternatives that were measured
            # and rejected, and what this costs in coverage.
            tables_scanned = not table_scan_keywords or page_may_hold_setout_table(
                raw_text, table_scan_keywords
            )
            tables = extract_tables(plumber_page) if tables_scanned else []
            # Per-sheet, unlike build_references() below — a cloud's tag
            # only needs matching against its own sheet's revision
            # schedule, no cross-sheet index required. See
            # extraction/revision_clouds.py's docstring for the real
            # vector convention this detects.
            revision_clouds = detect_revision_clouds(words, paths)

            sheet = Sheet(
                page_index=page_index,
                page_width=fitz_page.rect.width,
                page_height=fitz_page.rect.height,
                title_block=title_block,
                revision_schedule=revision_schedule,
                tables=tables,
                words=words,
                paths=paths,
                raw_text=raw_text,
                revision_clouds=revision_clouds,
                tables_scanned=tables_scanned,
            )
            project.sheets.append(sheet)

    # A whole-project pass, run after every sheet's words are in place —
    # see extraction/references.py's docstring for why this can't be done
    # per-sheet (resolution needs every sheet's view titles indexed first).
    project.references = build_references(project)

    return project
