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
from pdfchecker.extraction.tables import extract_revision_schedule, extract_tables
from pdfchecker.extraction.titleblock import extract_title_block
from pdfchecker.ir import Project, Sheet


def ingest_pdf(path: str) -> Project:
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
            tables = extract_tables(plumber_page)

            sheet = Sheet(
                page_index=page_index,
                page_width=fitz_page.rect.width,
                page_height=fitz_page.rect.height,
                title_block=title_block,
                revision_schedule=revision_schedule,
                tables=tables,
                words=words,
                paths=paths,
                raw_text=fitz_page.get_text("text"),
            )
            project.sheets.append(sheet)

    return project
