"""DXF model-space -> PDF page-space coordinate transform — PLANNING.md
§8's "per-viewport DXF-space -> PDF-page-space transform", the other
real gap left alongside §5b's multi-branch-graph form (see
`extraction/setout_reconstruction.py`'s docstring). This is what lets a
geometry-check `Issue` carry a real page-space `bbox` (§8's markup
export needs one — CLAUDE.md's own rule that every rule's `Issue`
carries a precise location, not just a sheet number) instead of only a
`suggested_fix` note describing the DXF-space location, which is all
`checks/geometry.py`'s rules have done until now.

**Confirmed 2026-08-14 against sheet 2871051** (`samples/BR06/dxf/
T2DPAA-T2D-C3S-BR-DRG-101051_0.dxf` next to its real PDF page,
page_index 14) — the two things PLANNING.md §8 flagged as open questions
before this was built:

1. **Paper space (mm) <-> PDF page space (points) is a fixed, universal
   72/25.4 pt-per-mm scale with NO offset, not something derived from
   the DXF's own plot/margin settings.** The `LAYOUT` entity's own
   `paper_width`/`paper_height` (mm) converted at 72/25.4 predicts the
   real PDF page's `page.rect` size to within 0.06pt — real confirmed
   values: DXF paper 841.0mm x 594.0mm (a real A1 sheet) -> predicted
   2383.94 x 1683.78pt vs. the PDF's actual 2383.92 x 1683.72pt. So
   paper-space (0, 0) maps directly to a PDF page corner; no margin/
   plot-origin lookup is needed at all — this firm's Revit-authored
   "same sheet view exported to both PDF and DWG" workflow (confirmed by
   the user, PLANNING.md §8) really does mean the two paper spaces are
   identical, not just similar. (The `LAYOUT` entity's own
   `unit_factor`/`scale_denominator` fields looked promising at first
   but turned out to be the *viewport's own plot scale* metadata, e.g.
   "1:1000" — not a paper-space unit conversion; real paper-space
   coordinates (both `VIEWPORT.center` and title-block `MTEXT.insert`)
   are already in meters, matching the doc's own `$INSUNITS`, and
   confirmed via those title-block `MTEXT` positions sitting in the
   expected bottom-right corner of an ~0.841m x 0.594m sheet.)

2. **The per-viewport model-space -> paper-space transform (`ps_center`/
   `ps_width`/`ps_height`/`view_center_point`/`view_height`, `ViewportEntity`'s
   own docstring) is a plain scale + translate, no rotation on this
   sample** (every real `view_twist_angle` seen is `0.0`) **and needs no
   guessing at which viewport a given model-space point belongs to** —
   confirmed end-to-end, not just assumed: every real `PIL2343xx` pile
   label's DXF model-space `TEXT` position, run through this module's
   transform, lands within ~2.5pt of that *same* pile label's real PDF
   `TextWord` bbox on the real page (checked all 27 real pile labels on
   sheet 2871051 — the ~2.4pt constant Y offset is just `MTEXT.insert`
   vs. a PDF word bbox's top-left corner being different text anchors,
   not a transform error). This sheet's four real viewports split
   cleanly by *scale*, not just window position: two are real content
   viewports (`ps_height/view_height` = 0.01 and ~0.05, i.e. real 1:100
   and 1:20 drafting scales) and two are 1:1 "passthrough" viewports
   (`view_height == ps_height` exactly) whose model-space window sits at
   tiny paper-scale-sized coordinates (0 to ~1) — real content (piles,
   dimensions) lives at real survey-scale model coordinates (hundreds to
   thousands), so in practice the two never overlap and there's no real
   viewport-selection ambiguity on this sample. `find_viewport_for_point`
   still picks the smallest-window match on a tie, as a documented,
   untested-on-real-data fallback, in case a future sample's viewports
   really do overlap.

**Not confirmed / left for a sample that needs it:** a sheet whose real
content viewport has a nonzero `view_twist_angle` (this module still
implements the rotation math, just unverified against real rotated
data), and a sheet with more than one non-empty paper-space `LAYOUT` (the
real sample's sheets each have exactly one with real viewports, plus an
empty leftover `Layout2` that contributes nothing — `extraction/
dxf_source.py`'s `extract_viewports` already walks every paper-space
layout, so this only matters if a future sample's *second* layout is
also real).
"""

from __future__ import annotations

import math

from pdfchecker.ir import BBox, DimensionEntity, DxfSheet, Point3D, ViewportEntity

# Paper space is confirmed in meters (module docstring point 1) —
# PDF points are always 1/72 inch, so this is a fixed physical-unit
# conversion, not something read off the DXF at all.
_MM_PER_M = 1000.0
_PT_PER_MM = 72 / 25.4
_PT_PER_M = _MM_PER_M * _PT_PER_MM  # ~2834.6457 pt/m


def _rotate(dx: float, dy: float, twist_deg: float) -> tuple[float, float]:
    """Rotates a (dx, dy) offset by `-twist_deg` — undoes the viewport's
    own view-twist so the offset is expressed in the viewport's
    axis-aligned frame before scaling. A no-op at `twist_deg == 0.0`,
    the only value confirmed on real data (module docstring)."""

    if not twist_deg:
        return dx, dy
    rad = math.radians(-twist_deg)
    cos_t, sin_t = math.cos(rad), math.sin(rad)
    return dx * cos_t - dy * sin_t, dx * sin_t + dy * cos_t


def _half_extents(vp: ViewportEntity) -> tuple[float, float] | None:
    if vp.ps_height <= 0 or vp.view_height <= 0:
        return None  # a degenerate/zero-size viewport — nothing to window
    half_h = vp.view_height / 2
    half_w = half_h * (vp.ps_width / vp.ps_height)
    return half_w, half_h


def find_viewport_for_point(viewports: list[ViewportEntity], model_point: Point3D) -> ViewportEntity | None:
    """The viewport whose model-space view window contains `model_point`
    — module docstring point 2 for why this is unambiguous on the real
    sample (content and "passthrough" viewports occupy disjoint
    coordinate ranges) and why the tie-break below (smallest window
    wins) is a documented fallback, not something exercised by real
    data yet. `None` if no viewport's window contains the point — this
    model-space location isn't shown on this sheet at all, or belongs to
    a viewport this sheet doesn't have (skip rather than guess, same
    convention as the rest of this codebase)."""

    best: tuple[float, ViewportEntity] | None = None
    for vp in viewports:
        extents = _half_extents(vp)
        if extents is None:
            continue
        half_w, half_h = extents
        dx, dy = _rotate(
            model_point.x - vp.view_center_point.x, model_point.y - vp.view_center_point.y, vp.view_twist_angle
        )
        if abs(dx) > half_w or abs(dy) > half_h:
            continue
        area = half_w * half_h * 4
        if best is None or area < best[0]:
            best = (area, vp)
    return best[1] if best else None


def model_to_paper(vp: ViewportEntity, model_point: Point3D) -> tuple[float, float]:
    """`model_point` (this viewport's model space) -> paper-space
    (meters) via this viewport's own scale/center/twist. Doesn't check
    the point actually falls inside the viewport's window — call
    `find_viewport_for_point` first to pick the right `vp`; this is the
    per-viewport half of the transform, kept separate so a caller that
    already knows the viewport (e.g. testing) doesn't need to re-derive
    it."""

    scale = vp.ps_height / vp.view_height if vp.view_height else 0.0
    dx, dy = _rotate(model_point.x - vp.view_center_point.x, model_point.y - vp.view_center_point.y, vp.view_twist_angle)
    return vp.ps_center.x + dx * scale, vp.ps_center.y + dy * scale


def paper_to_pdf(paper_x_m: float, paper_y_m: float, page_height_pt: float) -> tuple[float, float]:
    """Paper-space (meters) -> PDF page-space (points, origin top-left)
    — module docstring point 1's fixed 72/25.4 scale, plus the Y-flip
    paper space's bottom-left/Y-up origin needs to land in PDF's
    top-left/Y-down convention (`ir.py`'s `BBox` docstring)."""

    return paper_x_m * _PT_PER_M, page_height_pt - paper_y_m * _PT_PER_M


def model_to_pdf_point(
    dxf_sheet: DxfSheet, model_point: Point3D, page_height_pt: float
) -> tuple[float, float] | None:
    """The full chain: model space -> paper space (via whichever
    viewport actually shows this point) -> PDF page space. `None` if no
    viewport contains the point — nothing to map it through."""

    vp = find_viewport_for_point(dxf_sheet.viewports, model_point)
    if vp is None:
        return None
    paper_x, paper_y = model_to_paper(vp, model_point)
    return paper_to_pdf(paper_x, paper_y, page_height_pt)


def dimension_bbox(dxf_sheet: DxfSheet, dim: DimensionEntity, page_height_pt: float) -> BBox | None:
    """A `DIMENSION`'s page-space bounding box — its two witness points
    plus its own dimension-line point, each independently transformed
    (module docstring: real dimensions confirmed to sit inside a single
    viewport, so all three normally resolve through the same one, but
    each is looked up on its own rather than assumed). `None` only if
    *none* of the three points falls inside any viewport — a partial
    result (1-2 of 3 resolve) still yields a box, since that's still a
    real, if slightly tighter, location rather than nothing."""

    points = [
        model_to_pdf_point(dxf_sheet, p, page_height_pt)
        for p in (dim.ext_line1_origin, dim.ext_line2_origin, dim.dim_line_point)
    ]
    resolved = [p for p in points if p is not None]
    if not resolved:
        return None
    xs = [p[0] for p in resolved]
    ys = [p[1] for p in resolved]
    return BBox(x0=min(xs), y0=min(ys), x1=max(xs), y1=max(ys))
