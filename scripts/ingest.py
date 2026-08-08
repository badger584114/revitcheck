#!/usr/bin/env python3
"""Run Stage 1 ingestion against a PDF and print a summary.

Usage: python scripts/ingest.py samples/T2DPAA-T2D-C3S-BR-DRG-101000.pdf
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.extraction.pipeline import ingest_pdf  # noqa: E402


def main() -> None:
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(1)

    path = sys.argv[1]
    project = ingest_pdf(path)

    print(f"Ingested {path}: {len(project.sheets)} sheets\n")
    for sheet in project.sheets:
        tb = sheet.title_block.fields
        rev = sheet.revision_schedule[-1] if sheet.revision_schedule else None
        print(
            f"page {sheet.page_index + 1:>3}  "
            f"drawing_no={tb.get('drawing_no', '?'):<8} "
            f"sheet_no={tb.get('sheet_no', '?'):<9} "
            f"amend_no={tb.get('amend_no', '?'):<3} "
            f"latest_rev_id={(rev.rev_id if rev else '?'):<3} "
            f"latest_rev_desc={(rev.description if rev else '?')[:30]!r:<33} "
            f"tables={len(sheet.tables)} "
            f"words={len(sheet.words)} "
            f"paths={len(sheet.paths)}"
        )

    # Full IR dump for the first sheet with a revision schedule, as a
    # concrete artifact to eyeball.
    for sheet in project.sheets:
        if sheet.revision_schedule:
            print(f"\n--- full IR for page {sheet.page_index + 1} ---")
            print(json.dumps(sheet.to_dict(), indent=2)[:3000])
            break


if __name__ == "__main__":
    main()
