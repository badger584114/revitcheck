"""Intermediate Representation (IR) — see PLANNING.md §3.

Scoped to what Stage 1 (PDF ingestion) actually produces: Project -> Sheet,
with title block metadata, the revision schedule, generic tables, and raw
text/vector content. Entities are kept lightweight (word-level text,
bounding-box paths) for now; richer entity typing (dimensions with witness
lines, blocks/inserts, per-layer attribution) comes with the check engines
that consume it (§4, §5) — no point building it ahead of a consumer.

Deliberately NOT included yet, per PLANNING.md:
- References (§3's cross-sheet reference graph construct) — that's built by
  a later pass over the whole extracted sheet set (§4), not by per-sheet
  ingestion.
- Layers — this sample PDF carries no OCGs (confirmed against samples/), so
  there is nothing to attribute entities to yet. Revisit if a future sample
  does carry layers.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional


@dataclass(frozen=True)
class BBox:
    """Axis-aligned box in PDF page space (points, origin top-left)."""

    x0: float
    y0: float
    x1: float
    y1: float

    @property
    def width(self) -> float:
        return self.x1 - self.x0

    @property
    def height(self) -> float:
        return self.y1 - self.y0

    def to_dict(self) -> dict:
        return {"x0": self.x0, "y0": self.y0, "x1": self.x1, "y1": self.y1}


@dataclass
class TextWord:
    """A single extracted word with position — the raw unit text extraction
    produces; title-block/table extraction consumes these, not raw strings,
    so downstream logic always has a location to attach to an Issue later
    (CLAUDE.md: every Issue needs a precise location, not just a sheet)."""

    text: str
    bbox: BBox

    def to_dict(self) -> dict:
        return {"text": self.text, "bbox": self.bbox.to_dict()}


@dataclass
class PathEntity:
    """A vector drawing (line/polyline/curve/rect) as PyMuPDF reports it.
    Kept generic in stage 1 — classifying paths into dimensions, witness
    lines, revision clouds, etc. is check-engine-specific work (§4, §5),
    not an ingestion concern."""

    bbox: BBox
    kind: str  # "s" (stroke), "f" (fill), "fs" (both) — from PyMuPDF's drawing type
    stroke_width: Optional[float] = None

    def to_dict(self) -> dict:
        return {"bbox": self.bbox.to_dict(), "kind": self.kind, "stroke_width": self.stroke_width}


@dataclass
class Table:
    """A generic extracted table — pdfplumber's row/column output plus a
    location and a best-effort classification. Distinguishing "this is the
    revision schedule" vs "this is a setout/pile schedule" vs "this is some
    other table" is a classification step (see extraction/tables.py), not
    guessed permanently at extraction time — kind may be "unknown"."""

    bbox: BBox
    rows: list[list[Optional[str]]]
    kind: str = "unknown"  # "unknown" | "revision_schedule" | "schedule" | ...

    def to_dict(self) -> dict:
        return {"bbox": self.bbox.to_dict(), "kind": self.kind, "rows": self.rows}


@dataclass
class RevisionEntry:
    """One row of the bottom-left revision schedule (PLANNING.md §4
    'Revision consistency — mechanics'). Column mapping is project-
    configurable in general; this sample's columns (No., AMENDMENT
    DESCRIPTION, BY, CHECK, ACCEPTANCE, DATE) are used as the default."""

    rev_id: str
    description: str
    by: Optional[str] = None
    check: Optional[str] = None
    acceptance: Optional[str] = None
    date: Optional[str] = None

    def to_dict(self) -> dict:
        return {
            "rev_id": self.rev_id,
            "description": self.description,
            "by": self.by,
            "check": self.check,
            "acceptance": self.acceptance,
            "date": self.date,
        }


@dataclass
class TitleBlock:
    """Title-block field extraction result. `fields` holds whatever was
    found (label -> value text); §4's "Project rule configuration" schema
    lets a project extend which labels get looked for (e.g. a client's
    lat/long field) — this class doesn't hardcode a fixed field set."""

    fields: dict[str, str] = field(default_factory=dict)

    def get(self, name: str) -> Optional[str]:
        return self.fields.get(name)

    def to_dict(self) -> dict:
        return dict(self.fields)


@dataclass
class Sheet:
    """One page of the source PDF. `page_index` is 0-based into the source
    document — NOT the same as the sheet's own printed Sheet No., which
    lives in title_block.fields['sheet_no'] once extraction succeeds and is
    the identifier cross-sheet references (§4) and revision diffing (§7)
    actually key off."""

    page_index: int
    page_width: float
    page_height: float
    title_block: TitleBlock
    revision_schedule: list[RevisionEntry]
    tables: list[Table]
    words: list[TextWord]
    paths: list[PathEntity]
    raw_text: str

    @property
    def drawing_no(self) -> Optional[str]:
        return self.title_block.get("drawing_no")

    @property
    def sheet_no(self) -> Optional[str]:
        return self.title_block.get("sheet_no")

    def to_dict(self, *, include_words: bool = False, include_paths: bool = False) -> dict:
        d = {
            "page_index": self.page_index,
            "page_width": self.page_width,
            "page_height": self.page_height,
            "title_block": self.title_block.to_dict(),
            "revision_schedule": [r.to_dict() for r in self.revision_schedule],
            "tables": [t.to_dict() for t in self.tables],
            "word_count": len(self.words),
            "path_count": len(self.paths),
        }
        if include_words:
            d["words"] = [w.to_dict() for w in self.words]
        if include_paths:
            d["paths"] = [p.to_dict() for p in self.paths]
        return d


@dataclass
class Project:
    """Top-level IR container — PLANNING.md §3: Project -> Sheet."""

    source_path: str
    sheets: list[Sheet] = field(default_factory=list)

    def sheet_by_no(self, sheet_no: str) -> Optional[Sheet]:
        """Look up a sheet by its printed Sheet No. — the identifier that
        matters for cross-sheet references and revision diffing, not
        page_index/upload order."""
        for s in self.sheets:
            if s.sheet_no == sheet_no:
                return s
        return None

    def to_dict(self) -> dict:
        return {"source_path": self.source_path, "sheets": [s.to_dict() for s in self.sheets]}
