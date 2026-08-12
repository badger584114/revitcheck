#!/usr/bin/env python3
"""Run Stage 2 drafting checks against a PDF and print a summary.

Usage: python scripts/check.py samples/BR06/T2DPAA-T2D-C3S-BR-DRG-101000.pdf
"""

from __future__ import annotations

import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks import RuleConfig, run_checks  # noqa: E402
from pdfchecker.extraction.pipeline import ingest_pdf  # noqa: E402


def main() -> None:
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(1)

    path = sys.argv[1]
    project = ingest_pdf(path)

    config = RuleConfig(
        firm_glossary_path="config/firm_glossary.json",
        project_glossary_path="config/project_glossary.json",
    )
    issues = run_checks(project, config)

    print(f"Checked {path}: {len(project.sheets)} sheets, {len(issues)} issues\n")

    by_category = Counter(i.category for i in issues)
    for category, count in by_category.most_common():
        print(f"  {category}: {count}")

    for category in ("title_block", "revision", "cross_sheet"):
        cat_issues = [i for i in issues if i.category == category]
        if cat_issues:
            print(f"\n--- {category} issues ---")
            for i in cat_issues:
                print(f"  {i.sheet_no}: {i.description}")

    spelling_issues = [i for i in issues if i.category == "spelling"]
    if spelling_issues:
        by_word = Counter(i.description for i in spelling_issues)
        print(f"\n--- spelling: {len(by_word)} distinct flagged words, {len(spelling_issues)} occurrences ---")
        for desc, count in by_word.most_common(20):
            print(f"  {count:>3}x  {desc}")


if __name__ == "__main__":
    main()
