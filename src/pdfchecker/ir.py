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
`DimensionEntity`, `ViewportEntity`, `DxfInsert`, `DxfText`, and
`DxfSheet` are DXF-only constructs, built by extraction/dxf_source.py
from real converted DXF (samples/dwg/ via ODA File Converter + `ezdxf`)
— deliberately a separate container from `Project`/`Sheet` rather than
merged into one PDF+DXF schema yet, since the check engine that needs
both together doesn't exist yet either. See `DxfSheet`'s own docstring
for why, and extraction/dxf_source.py's docstring for what real DXF
structure confirmed vs. corrected from PLANNING.md's original plan.
`DxfInsert`/`DxfText` were added 2026-08-11 for §5b
(extraction/setout_reconstruction.py). `SetoutPoint` (also added
2026-08-11) is format-agnostic, unlike the DXF-only constructs above —
it's the typed form of a setout-schedule row, used both for the
schedule's own stated points (parsed from a PDF `Table`) and a check's
independently DXF-derived points, so it lives near `Table` rather than
in the DXF-only group.

`IfcElement`/`IfcModel` were added 2026-08-12 for §5's proposed third
geometry-check source (extraction/ifc_source.py, IFC 3D-model export) —
project-level constructs, not per-sheet, attached to `Project.ifc_model`
(not `Sheet`/`DxfSheet` — one IFC export covers the whole model, no
per-sheet join the way `DxfSheet` needs). `checks/geometry.py`'s
`geometry.ifc_setout_consistency` is the first real consumer. See
extraction/ifc_source.py's docstring for the real findings this is
deliberately built around being schema-general rather than tied to this
sample's specific client/Revit-export conventions.
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

    @classmethod
    def from_dict(cls, d: dict) -> "BBox":
        """Inverse of `to_dict` — added 2026-08-17 so an `Issue` can
        round-trip through JSON (see `checks/issue.py`'s `from_dict`).
        PLANNING.md §8's engineer-selection step means a client sends a
        chosen subset back to a server that, per §2, kept nothing."""

        return cls(x0=d["x0"], y0=d["y0"], x1=d["x1"], y1=d["y1"])


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
    nothing to disagree with, not a missing-data case.

    `dim_type` was added once checks/geometry.py needed to tell a real
    length dimension apart from other DXF dimension kinds — confirmed
    against a real sheet (101151) that not every dimension measures a
    length or carries a numeric override: 46 of 49 dimensions there are
    `dim_type=0` (linear/rotated) with an override that's a bare letter
    ("A", "B", "C"...) keying into a separate bar-mark/schedule table
    elsewhere on the sheet, not a rounded buildable length — a real,
    legitimate drafting convention this check must skip rather than
    misread as a numeric mismatch. Kept as the raw DXF code (not
    interpreted at extraction time, matching `PathEntity.kind`'s
    precedent) — the check engine decides what's in scope."""

    measurement: float  # raw geometric distance between the witness-line origins, in the sheet's real-world units (DxfSheet.units)
    stated_text: Optional[str]  # manual text override, if any; None if none set
    dim_line_point: Point3D  # DXF `defpoint` — where the dimension line/text sits
    ext_line1_origin: Point3D  # DXF `defpoint2` — first witness line's origin on the dimensioned geometry
    ext_line2_origin: Point3D  # DXF `defpoint3` — second witness line's origin
    dimstyle: str
    layer: str
    dim_type: int = 0  # raw DXF dimtype code (0 = linear/rotated — the only kind this project's checks interpret so far)

    def to_dict(self) -> dict:
        return {
            "measurement": self.measurement,
            "stated_text": self.stated_text,
            "dim_line_point": self.dim_line_point.to_dict(),
            "ext_line1_origin": self.ext_line1_origin.to_dict(),
            "ext_line2_origin": self.ext_line2_origin.to_dict(),
            "dim_type": self.dim_type,
            "dimstyle": self.dimstyle,
            "layer": self.layer,
        }


@dataclass
class DxfInsert:
    """A DXF `INSERT` (block reference) — model-space block placement.
    Added for extraction/setout_reconstruction.py (PLANNING.md §5b): DXF
    block inserts on this firm's export carry zero `ATTRIB` attributes
    (extraction/dxf_source.py's docstring, point 3), so identifying what
    a block *is* means matching its `name` (Revit embeds the source
    family/type in it, e.g. '...CAST-IN-PLACE PILE...' or 'CS_SYMB_SETOUT
    POINT...') by substring, then reading nearby plain text (`DxfText`,
    below) for identity/values a real ATTRIB would otherwise carry — the
    same text-adjacency principle `titleblock.py`/`revision_clouds.py`
    already use for PDF, applied here to DXF's block/text split instead."""

    name: str
    insert: Point3D
    layer: str

    def to_dict(self) -> dict:
        return {"name": self.name, "insert": self.insert.to_dict(), "layer": self.layer}


@dataclass
class DxfText:
    """A DXF `TEXT`/`MTEXT` entity — model-space annotation text. `text`
    is the plain, formatting-code-stripped content (`MTEXT.plain_text()`,
    not the raw `.text` — confirmed real MTEXT carries `\\P` paragraph-
    break codes that `plain_text()` resolves into real newlines and raw
    `.text` does not, e.g. a real control-point label's raw text is
    `'E 278437.803\\PN 6130709.230'` vs. plain `'E 278437.803\\nN
    6130709.230'`). Added for extraction/setout_reconstruction.py: this is
    how pile IDs, bearing values, and setout-point coordinates are read
    off a sheet with no ATTRIB attributes to key off (see `DxfInsert`)."""

    text: str
    insert: Point3D
    layer: str

    def to_dict(self) -> dict:
        return {"text": self.text, "insert": self.insert.to_dict(), "layer": self.layer}


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

    Title-block extraction from DXF isn't attempted — confirmed feasible
    via fixed paper-space text position (no ATTRIB attributes exist to
    key off, unlike PLANNING.md §4 originally assumed, see extraction/
    dxf_source.py's docstring), but confirmed by the user 2026-08-15 not
    needed: title-block extraction stays PDF-only for good, not a gap
    waiting on a consumer.

    `inserts`/`texts` were added for §5b (extraction/setout_reconstruction.py)
    — modelspace `INSERT`/`TEXT`/`MTEXT` entities, the same "no ATTRIB,
    match by name + nearby text" approach `DxfInsert`/`DxfText` describe."""

    source_path: str
    dimensions: list[DimensionEntity]
    viewports: list[ViewportEntity]
    units: str  # e.g. "m" — resolved from the DXF header's $INSUNITS
    inserts: list[DxfInsert] = field(default_factory=list)
    texts: list[DxfText] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {
            "source_path": self.source_path,
            "dimensions": [d.to_dict() for d in self.dimensions],
            "viewports": [v.to_dict() for v in self.viewports],
            "units": self.units,
            "inserts": [i.to_dict() for i in self.inserts],
            "texts": [t.to_dict() for t in self.texts],
        }


@dataclass
class IfcElement:
    """One physical IFC element (any entity with both `ObjectPlacement`
    and `Representation` — a beam, slab, member, wall, whatever the
    project's Revit export happens to contain) — PLANNING.md §5's
    proposed third geometry-check source, extraction/ifc_source.py.

    Deliberately schema-general, not project-specific — see that
    module's docstring for the real findings this is built from (raised
    by the user 2026-08-12, calibrated against samples/BR06's and
    samples/BR08's real `.ifc` files, confirmed to be the same client
    project so their metadata conventions match each other but are NOT
    assumed to generalize to a different client). `ifc_class` (e.g.
    `"IfcBeam"`) and `predefined_type` (e.g. `"BEAM"`) are both IFC4
    schema-standard — portable to any IFC file regardless of authoring
    tool. `global_id` is the schema-standard GUID. `display_name` is the
    Revit family/type string (e.g. `"03_SFR_ACS_Abutment_CS: ABUTMENT
    EAST_BR06_3"`) — confirmed real but firm-specific naming, kept ONLY
    for human-readable labeling (Issue payloads, audit trail), never as
    a matching/lookup key. `bbox_min`/`bbox_max` are the element's
    world-space axis-aligned bounding box in real metres — confirmed
    `ifcopenshell.geom`'s default (`CONVERT_BACK_UNITS` left `False`)
    always normalizes to metres regardless of a file's declared length
    unit (both real files declare millimetres; geometry output still
    comes out already-in-metres), so this is a general property of the
    library, not a project-specific assumption."""

    global_id: str
    ifc_class: str
    predefined_type: Optional[str]
    display_name: Optional[str]
    bbox_min: Point3D
    bbox_max: Point3D

    def to_dict(self) -> dict:
        return {
            "global_id": self.global_id,
            "ifc_class": self.ifc_class,
            "predefined_type": self.predefined_type,
            "display_name": self.display_name,
            "bbox_min": self.bbox_min.to_dict(),
            "bbox_max": self.bbox_max.to_dict(),
        }


@dataclass
class IfcModel:
    """One project's IFC 3D-model export — extraction/ifc_source.py's
    `ingest_ifc(path) -> IfcModel`. Project-level, not per-sheet, unlike
    `DxfSheet` — an IFC export represents the whole model, not one
    sheet's view of it, so there's no per-sheet join at ingestion time
    (matching `DxfSheet`'s "no point building it ahead of a consumer"
    precedent: how an `IfcModel` corresponds to `Sheet`/`DxfSheet` data
    is check-engine work, not resolved yet — see this module's
    docstring for why that correspondence isn't even a coordinate-frame
    match for free, despite same-Revit-model provenance).

    `has_map_conversion` records whether the file carries IFC4's
    schema-standard `IfcMapConversion`/`IfcProjectedCRS` georeferencing
    entities — confirmed `False` on both real files inspected so far;
    `site_ref_lat`/`site_ref_long` fall back to `IfcSite.RefLatitude`/
    `RefLongitude` (also schema-standard, present on every `IfcSite`
    regardless of author, but coarse — confirmed ~1.7km off a real
    sheet's title-block lat/long, a project-wide site reference rather
    than per-element precision) as a portable, if rougher, real-world
    anchor. Neither is a substitute for a verified per-project
    coordinate transform — see extraction/ifc_source.py's docstring."""

    source_path: str
    schema: str  # e.g. "IFC4"
    length_unit: str  # the file's own declared unit, e.g. "MILLI.METRE" — informational only; geometry is always exposed in metres regardless (see IfcElement)
    has_map_conversion: bool
    site_ref_lat: Optional[float]  # decimal degrees, from IfcSite.RefLatitude
    site_ref_long: Optional[float]  # decimal degrees, from IfcSite.RefLongitude
    elements: list[IfcElement] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {
            "source_path": self.source_path,
            "schema": self.schema,
            "length_unit": self.length_unit,
            "has_map_conversion": self.has_map_conversion,
            "site_ref_lat": self.site_ref_lat,
            "site_ref_long": self.site_ref_long,
            "elements": [e.to_dict() for e in self.elements],
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


@dataclass(frozen=True)
class SetoutPoint:
    """A single named setout/coordinate point — PLANNING.md §5b's typed
    form of a setout-schedule row (e.g. a pile schedule's SITE ID/EASTING/
    NORTHING columns, `extraction/tables.py`'s `parse_pile_schedule`).
    Reused for both the schedule's *stated* points and a check's
    independently *derived* points (extraction/setout_reconstruction.py),
    so the two are directly comparable — the same
    "typed value, not raw table cells" step `RevisionEntry` already does
    for the revision schedule, applied here to setout data."""

    point_id: str
    easting: float
    northing: float

    def to_dict(self) -> dict:
        return {"point_id": self.point_id, "easting": self.easting, "northing": self.northing}


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
    the identifier cross-sheet references (§4) actually key off.

    `dxf_sheet` is the merge point PLANNING.md §5's geometry checks need
    between PDF-sourced drafting data and DXF-sourced geometry data —
    `None` for a drafting-only run, or when this sheet has no DXF
    counterpart. Populated by `extraction.dxf_source.attach_dxf_sheets`,
    which matches by the numeric-suffix join PLANNING.md §8 confirmed
    against the real sample (DWG filename ↔ this sheet's `sheet_no`, last
    4 digits) — not merged into one shared PDF+DXF schema at the field
    level, since a `DxfSheet`'s own fields (model-space coordinates, DXF
    units) aren't meaningful in PDF page-space terms."""

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
    dxf_sheet: Optional[DxfSheet] = None
    # False when ruled-table extraction was deliberately skipped for this
    # sheet — `extraction/tables.py`'s `page_may_hold_setout_table` found
    # no Easting/Northing text, so the (very expensive) table detector was
    # never run (see that function for the profiling and the trade-off).
    # `tables` is then `[]` because nobody *looked*, which is a different
    # claim from "this sheet has no tables", and consumers that care about
    # the difference need to be able to tell them apart — the same
    # "report a coverage indicator, don't fail silently" rule the check
    # rules follow, applied to ingestion.
    tables_scanned: bool = True

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
            "tables_scanned": self.tables_scanned,
            "word_count": len(self.words),
            "path_count": len(self.paths),
            "revision_clouds": [c.to_dict() for c in self.revision_clouds],
            "dxf_sheet": self.dxf_sheet.to_dict() if self.dxf_sheet else None,
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

    Symbol-based references (section markers, detail bubbles) were built
    first per PLANNING.md §4's scoping note; general free-text note
    references ("REFER TO SHEET No. X") were added 2026-08-14 as `"note"`
    — see that module's docstring for the resolution algorithm and real
    convention each type needed, and why a cross-drawing-package citation
    (a different discipline's own sheet set) is deliberately excluded
    rather than extracted as an ever-unresolved `Reference`. Match lines
    ("MATCH LINE — SEE SHEET S-103") are still §3's un-built fourth type."""

    ref_type: str  # "section" | "detail" | "note" | "unknown"
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
    # PLANNING.md §5's proposed third geometry source, added 2026-08-12 —
    # project-level (one IFC model covers every sheet), attached via
    # extraction.ifc_source.attach_ifc_model, same "no point building it
    # ahead of a consumer" precedent as DxfSheet. None on a drafting-only
    # run, or a geometry run with no IFC uploaded — checks must treat that
    # as "nothing to cross-check," not an error.
    ifc_model: Optional[IfcModel] = None

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
            "ifc_model": self.ifc_model.to_dict() if self.ifc_model else None,
        }
