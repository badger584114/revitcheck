#!/usr/bin/env python3
"""Run Stage 2 drafting checks against a PDF and burn the flagged Issues
onto a marked-up copy (PLANNING.md §8). Drafting-only, same scope as
scripts/check.py — no Stage 3 CLI wrapper exists yet (CLAUDE.md: driving
DXF/geometry checks means calling checks.geometry directly), so a
geometry-inclusive markup set means calling render_markup() with a
project that already has DXF/IFC attached, same as tests/test_geometry.py
does, not this script.

Usage: python scripts/markup.py samples/BR06/T2DPAA-T2D-C3S-BR-DRG-101000.pdf [output.pdf]
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks import RuleConfig, run_checks  # noqa: E402
from pdfchecker.extraction.pipeline import ingest_pdf  # noqa: E402
from pdfchecker.markup.pdf_markup import render_markup  # noqa: E402


def main() -> None:
    if len(sys.argv) not in (2, 3):
        print(__doc__)
        sys.exit(1)

    path = sys.argv[1]
    output_path = sys.argv[2] if len(sys.argv) == 3 else str(Path(path).with_suffix("")) + "_markup.pdf"

    project = ingest_pdf(path)
    config = RuleConfig(
        firm_glossary_path="config/firm_glossary.json",
        project_glossary_path="config/project_glossary.json",
    )
    issues = run_checks(project, config)

    report = render_markup(project, issues, output_path)
    rendered = sum(1 for r in report if r.rendered)

    print(f"Marked up {path}: {len(report)} issues ({rendered} drawn on a sheet, {len(report) - rendered} report-only)")
    print(f"-> {output_path}\n")

    for entry in report:
        location = f"page {entry.page_index + 1}" if entry.sheet_no is None else entry.sheet_no
        drawn = "" if entry.rendered else "  (not drawn — no location to mark)"
        print(f"  {entry.tag}  {location}  {entry.note}{drawn}")


if __name__ == "__main__":
    main()
