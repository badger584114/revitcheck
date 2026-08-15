#!/usr/bin/env python3
"""Run Stage 2 drafting checks against a PDF and burn the flagged Issues
onto a marked-up copy (PLANNING.md §8). Drafting-only, same scope as
scripts/check.py — no Stage 3 CLI wrapper exists yet (CLAUDE.md: driving
DXF/geometry checks means calling checks.geometry directly), so a
geometry-inclusive markup set means calling render_markup() with a
project that already has DXF/IFC attached, same as tests/test_geometry.py
does, not this script.

Usage: python scripts/markup.py samples/BR06/T2DPAA-T2D-C3S-BR-DRG-101000.pdf [output.pdf]
       python scripts/markup.py samples/BR06/T2DPAA-T2D-C3S-BR-DRG-101000.pdf --config config/session_example.yaml
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks import RuleConfig, load_session_config, run_checks  # noqa: E402
from pdfchecker.extraction.pipeline import ingest_pdf  # noqa: E402
from pdfchecker.markup.pdf_markup import render_markup  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pdf_path")
    parser.add_argument("output_path", nargs="?", help="defaults to <pdf_path>_markup.pdf")
    parser.add_argument(
        "--config",
        help="session-level rules file (YAML/JSON, PLANNING.md §4/§9 step 5) — "
        "see config/session_example.yaml. Falls back to config/{firm,project}_glossary.json if omitted.",
    )
    args = parser.parse_args()

    output_path = args.output_path or str(Path(args.pdf_path).with_suffix("")) + "_markup.pdf"

    project = ingest_pdf(args.pdf_path)

    if args.config:
        loaded = load_session_config(args.config)
        for warning in loaded.warnings:
            print(f"[config warning] {warning}")
        config = loaded.rule_config
    else:
        config = RuleConfig(
            firm_glossary_path="config/firm_glossary.json",
            project_glossary_path="config/project_glossary.json",
        )
    issues = run_checks(project, config)

    report = render_markup(project, issues, output_path)
    rendered = sum(1 for r in report if r.rendered)

    print(f"Marked up {args.pdf_path}: {len(report)} issues ({rendered} drawn on a sheet, {len(report) - rendered} report-only)")
    print(f"-> {output_path}\n")

    for entry in report:
        location = f"page {entry.page_index + 1}" if entry.sheet_no is None else entry.sheet_no
        drawn = "" if entry.rendered else "  (not drawn — no location to mark)"
        print(f"  {entry.tag}  {location}  {entry.note}{drawn}")


if __name__ == "__main__":
    main()
