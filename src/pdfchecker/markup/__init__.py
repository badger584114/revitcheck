"""Markup & redline export (PLANNING.md §8) — Stage 4: turns a check
run's `Issue` list into a marked-up PDF (`pdf_markup.py`, native
annotations via PyMuPDF) plus the terse `Label: payload` note every
markup carries (`notes.py`). PDF is the primary markup target for every
Issue, including geometry-check ones (§8, decided 2026-08-10) — the
DXF/DWG redline-layer output §8 also describes isn't built yet.
"""
