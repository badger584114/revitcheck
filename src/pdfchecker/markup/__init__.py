"""Markup & redline export (PLANNING.md §8) — Stage 4: turns a check
run's `Issue` list into marked-up sheet copies plus the terse
`Label: payload` note every markup carries (`notes.py`). PDF
(`pdf_markup.py`, native annotations via PyMuPDF) is the primary markup
target for every Issue, including geometry-check ones (§8, decided
2026-08-10). DXF/DWG redline export (`dxf_markup.py`, native
`LWPOLYLINE`/`CIRCLE` + `MULTILEADER`/`MTEXT` entities on a dedicated
`Z-QC-REDLINE` layer via `ezdxf`) is the secondary "edit natively in
CAD" target, built 2026-08-15 — see that module's docstring for why it
needs none of `extraction/dxf_pdf_transform.py`'s viewport/model-space
machinery, only its `pdf_to_paper` inverse. `tags.py`'s `assign_tags` is
the shared reference-tag numbering both formats use, so the same Issue
gets the same `#NNN` tag whichever markup output it appears in.
"""
