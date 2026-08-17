"""Markup & redline export (PLANNING.md §8) — Stage 4: turns a check
run's `Issue` list into a marked-up sheet copy plus the terse
`Label: payload` note every markup carries (`notes.py`).

**PDF is the only markup target.** It was always the primary one for
every Issue including geometry-check ones (§8, decided 2026-08-10);
a second DXF/DWG redline exporter existed 2026-08-15 to 2026-08-17 as a
CAD-native option, and was removed once the user confirmed the real
workflow: all drafting is done in Revit, and the drafting team never
opens AutoCAD. DWG/DXF in this project is a Revit *export* consumed as
geometry-check input (`extraction/dxf_source.py`), never an editable
deliverable this tool hands back. See PLANNING.md §8.
"""
