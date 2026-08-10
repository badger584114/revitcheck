"""Intermediate Representation (IR) — see PLANNING.md §3.

Scoped to what Stage 1 (PDF ingestion) actually produces: Project -> Sheet,
with title block metadata, the revision schedule, generic tables, and raw
text/vector content. Entities are kept lightweight (word-level text,
bounding-box paths) for now; richer entity typing (dimensions with witness
lines, blocks/inserts, per-layer attribution) comes with the check engines
that consume it (§4, §5) — no point building it ahead of a consumer.

Deliberately NOT included yet, per PLANNING.md:
- Layers — this sample PDF carries no OCGs (confirmed against samples/), so
  there is nothing to attribute entities to yet. Revisit if a future sample
  does carry layers.

References (§3's cross-sheet reference graph construct) ARE included below
(`Reference`) — built by extraction/references.py, a pass over the whole
extracted sheet set run after per-sheet ingestion, not part of it.

Revision clouds (`RevisionCloud`) are also included below — built by
extraction/revision_clouds.py, a per-sheet pass (unlike References, this
needs no cross-sheet data). This is the piece PLANNING.md §4's revision
cross-check originally flagged as needing new geometric detection work,
paused until a sample with real clouds existed to calibrate against; see
that module's docstring for the real convention found on
samples/T2DPAA-T2D-C3S-BR-DRG-101000_1.pdf.

Stage 3 (geometry checks, §5) starts here too, 2026-08-10: `Point3D`,
`DimensionEntity`, `ViewportEntity`, and `DxfSheet` are DXF-only
constructs, built by extraction/dxf_source.py from real converted DXF
(samples/dwg/ via ODA File Converter + `ezdxf`) — deliberately a separate
container from `Project`/`Sheet` rather than merged into one PDF+DXF
schema yet, since the check engine that needs both together doesn't
exist yet either. See `DxfSheet`'s own docstring for why, and
extraction/dxf_source.py's docstring for what real DXF structure
confirmed vs. corrected from PLANNING.md's original plan.
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
    not an ingestion concern.

    `color` and `curved` were added once revision-cloud detection
    (extraction/revision_clouds.py) needed them: a cloud's scalloped
    outline is only distinguishable from ordinary drafting linework by
    its stroke color and its curve/line shape, neither of which the
    original bbox-only shape carried. Both are cheap to keep — PyMuPDF's
    `get_drawings()` already reports them per path — so this isn't a new
    extraction pass, just retaining more of what was already there."""

    bbox: BBox
    kind: str  # "s" (stroke), "f" (fill), "fs" (both) — from PyMuPDF's drawing type
    stroke_width: Optional[float] = None
    color: Optional[tuple] = None  # stroke RGB, 0-1 per channel, as PyMuPDF reports it; None if none set
    curved: bool = False  # True if any drawing item is a bezier curve ("c"), vs. straight lines only

    def to_dict(self) -> dict:
        return {
            "bbox": self.bbox.to_dict(),
            "kind": self.kind,
            "stroke_width": self.stroke_width,
            "color": self.color,
            "curved": self.curved,
        }


@dataclass(frozen=True)
class Point3D:
    """A 3D point in DXF model or paper space — DXF entities carry real
    3D coordinates even for nominally-2D civil drawings (elevation via Z),
    unlike PDF's page-space `BBox` which has no third dimension. Added
    for extraction/dxf_source.py; kept separate from `BBox` rather than
    reusing it for a single point, matching how the rest of this IR keeps
    distinct shapes for distinct things."""

    x: float
    y: float
    z: float = 0.0

    def to_dict(self) -> dict:
        return {"x": self.x, "y": self.y, "z": self.z}


@dataclass
class DimensionEntity:
    """A DXF `DIMENSION` entity — PLANNING.md §5's geometry-check input.
    Confirmed 2026-08-10 against real converted DXF (see
    extraction/dxf_source.py's docstring): carries its measured value and
    both witness-line origins directly, no proximity inference needed,
    exactly as §5 assumed. `stated_text` is the drafter's manual override
    (confirmed common — 54% of real dimensions inspected carry one), the
    actual "drawn vs. stated" comparison target; `None` means the sheet
    displays the auto-computed `measurement` with no override, i.e.
    nothing to disagree with, not a missing-data case."""

    measurement: float  # raw geometric distance between the witness-line origins, in the sheet's real-world units (DxfSheet.units)
    stated_text: Optional[str]  # manual text override, if any; None if none set
    dim_line_point: Point3D  # DXF `defpoint` — where the dimension line/text sits
    ext_line1_origin: Point3D  # DXF `defpoint2` — first witness line's origin on the dimensioned geometry
    ext_line2_origin: Point3D  # DXF `defpoint3` — second witness line's origin
    dimstyle: str
    layer: str

    def to_dict(self) -> dict:
        return {
            "measurement": self.measurement,
            "stated_text": self.stated_text,
            "dim_line_point": self.dim_line_point.to_dict(),
            "ext_line1_origin": self.ext_line1_origin.to_dict(),
            "ext_line2_origin": self.ext_line2_origin.to_dict(),
            "dimstyle": self.dimstyle,
            "layer": self.layer,
        }


@dataclass
class ViewportEntity:
    """A DXF paper-space `VIEWPORT` — a window into model space at its
    own scale/center. Confirmed 2026-08-10 against real converted DXF: a
    sheet's paper-space layout typically has *several* of these (4-11
    seen across the real sample), not one — a sheet showing plan +
    elevation + section views has one viewport per view, each its own
    scale. This is why the PDF-markup coordinate transform (PLANNING.md
    §8) has to be per-viewport, not per-sheet: `ps_center`/`ps_width`/
    `ps_height` locate this viewport's window in paper space,
    `view_center_point`/`view_height` say what model-space point/extent
    it's showing — `ps_height / view_height` is the scale factor."""

    id: int
    ps_center: Point3D  # paper-space (page) location of this viewport's center
    ps_width: float
    ps_height: float
    view_center_point: Point3D  # model-space point this viewport is centered on
    view_height: float  # model-space vertical extent shown — scale = ps_height / view_height
    view_twist_angle: float = 0.0  # rotation of the view within the viewport, degrees

    def to_dict(self) -> dict:
        return {
            "id": self.id,
            "ps_center": self.ps_center.to_dict(),
            "ps_width": self.ps_width,
            "ps_height": self.ps_height,
            "view_center_point": self.view_center_point.to_dict(),
            "view_height": self.view_height,
            "view_twist_angle": self.view_twist_angle,
        }


@dataclass
class DxfSheet:
    """One DWG/DXF sheet's extracted geometry-check inputs — deliberately
    a separate container from `Sheet` (PDF) rather than merged into one
    schema yet. PLANNING.md §3 says PDF and DXF extraction should
    eventually converge on one IR, but that merge (matching a `DxfSheet`
    to its counterpart `Sheet` by sheet number — PLANNING.md §8 already
    confirmed the real join: the DWG filename's/DXF's numeric suffix
    matches the PDF's `sheet_no` on the last 4 digits) is work the
    geometry check engine needs when it actually consumes both sources
    together, not an ingestion concern yet — see CLAUDE.md's "no point
    building it ahead of a consumer" convention. `source_path` is the
    converted DXF's path, not the original DWG's.

    Title-block extraction from DXF isn't attempted yet even though the
    real geometry was inspected for it (see extraction/dxf_source.py's
    docstring) — confirmed feasible via fixed paper-space text position
    (no ATTRIB attributes exist to key off, unlike PLANNING.md §4
    originally assumed) but not yet needed by the first geometry check
    (single-sheet dimensional consistency, PLANNING.md §9 step 3), which
    only needs `dimensions` and `units`."""

    source_path: str
    dimensions: list[DimensionEntity]
    viewports: list[ViewportEntity]
    units: str  # e.g. "m" — resolved from the DXF header's $INSUNITS

    def to_dict(self) -> dict:
        return {
            "source_path": self.source_path,
            "dimensions": [d.to_dict() for d in self.dimensions],
            "viewports": [v.to_dict() for v in self.viewports],
            "units": self.units,
        }


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
class RevisionCloud:
    """A drawn revision cloud (scalloped closed curve) on a sheet body,
    paired with its triangle revision-tag symbol where one was found
    nearby. Built by extraction/revision_clouds.py — see that module's
    docstring for the real vector convention this was calibrated
    against (samples/T2DPAA-T2D-C3S-BR-DRG-101000_1.pdf).

    `tag` is None when a qualifying cloud cluster was found but no
    matching triangle+digit could be paired with it — surfaced as its
    own low-confidence case by the check that consumes this (PLANNING.md
    §4's revision cross-check), not silently dropped, same "confidence,
    not silent" principle as the cross-sheet reference graph."""

    bbox: BBox  # union of the cloud's own scallop-arc bounding boxes
    tag: Optional[str] = None  # revision number/letter read from the paired triangle, if found
    triangle_bbox: Optional[BBox] = None
    arc_count: int = 0  # number of scallop-arc path objects that made up this cloud

    def to_dict(self) -> dict:
        return {
            "bbox": self.bbox.to_dict(),
            "tag": self.tag,
            "triangle_bbox": self.triangle_bbox.to_dict() if self.triangle_bbox else None,
            "arc_count": self.arc_count,
        }


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
    revision_clouds: list[RevisionCloud] = field(default_factory=list)

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
            "revision_clouds": [c.to_dict() for c in self.revision_clouds],
        }
        if include_words:
            d["words"] = [w.to_dict() for w in self.words]
        if include_paths:
            d["paths"] = [p.to_dict() for p in self.paths]
        return d


@dataclass
class Reference:
    """One cross-sheet reference edge (PLANNING.md §3 "References" / §4
    "Cross-sheet reference graph — mechanics"). Built by extraction/
    references.py as a pass over the whole Project after every sheet's
    words are extracted — not per-sheet ingestion, since resolution needs
    every sheet's view titles indexed first (see that module's docstring
    for the resolution algorithm and what this sample's real convention
    turned out to need).

    Scoped to symbol-based references only (section markers, detail
    bubbles) for now — general free-text note references ("see Detail 4
    on Dwg S-201") are explicitly deferred by PLANNING.md §4's scoping
    note, so `ref_type` is "section" | "detail" | "unknown" (unresolved,
    kind undetermined), not the full four-type list §3 lists as the
    eventual target."""

    ref_type: str  # "section" | "detail" | "unknown"
    tag: str
    source_sheet_no: Optional[str]
    source_page_index: int
    source_bbox: BBox
    target_sheet_hint: str  # sheet number text printed on the marker itself, pre-resolution
    resolved: bool
    target_sheet_no: Optional[str] = None
    target_bbox: Optional[BBox] = None
    confidence: float = 1.0

    def to_dict(self) -> dict:
        return {
            "ref_type": self.ref_type,
            "tag": self.tag,
            "source_sheet_no": self.source_sheet_no,
            "source_page_index": self.source_page_index,
            "source_bbox": self.source_bbox.to_dict(),
            "target_sheet_hint": self.target_sheet_hint,
            "resolved": self.resolved,
            "target_sheet_no": self.target_sheet_no,
            "target_bbox": self.target_bbox.to_dict() if self.target_bbox else None,
            "confidence": self.confidence,
        }


@dataclass
class Project:
    """Top-level IR container — PLANNING.md §3: Project -> Sheet."""

    source_path: str
    sheets: list[Sheet] = field(default_factory=list)
    references: list[Reference] = field(default_factory=list)

    def sheet_by_no(self, sheet_no: str) -> Optional[Sheet]:
        """Look up a sheet by its printed Sheet No. — the identifier that
        matters for cross-sheet references and revision diffing, not
        page_index/upload order."""
        for s in self.sheets:
            if s.sheet_no == sheet_no:
                return s
        return None

    def to_dict(self) -> dict:
        return {
            "source_path": self.source_path,
            "sheets": [s.to_dict() for s in self.sheets],
            "references": [r.to_dict() for r in self.references],
        }
