"""Revision cloud/triangle detection — PLANNING.md §4 "Revision consistency
— mechanics", the third piece of the revision cross-check (cloud/triangle
-> schedule row). Paused since 2026-08-09 pending a sample that actually
contains revision clouds to calibrate against (see checks/revisions.py's
module docstring); unblocked now that samples/T2DPAA-T2D-C3S-BR-DRG-
101000_1.pdf's amended sheets (101032, 101033, 101051, 101056 — AMEND No.
1) do.

A per-sheet pass, unlike extraction/references.py's whole-project pass —
a cloud's tag only needs matching against its own sheet's revision
schedule, no cross-sheet index required.

*What the real sample's clouds/triangles actually look like* (inspected
via PyMuPDF's `page.get_drawings()` on page 13's three visible clouds,
same "check real geometry before writing detection code" approach
extraction/references.py used, and PLANNING.md §4 flagged as the right
call rather than assuming this would "mirror" that module's text-adjacency
approach):

1. **A cloud is not one closed path — it's dozens of separate small ones.**
   Each scallop/bump of the cloud outline is its own drawing object (2-3
   bezier-curve items, a few points wide), not one continuous polyline
   with many curve segments. A real cloud on the sample is 28-96 of these
   stroked in solid red (`(1.0, 0.0, 0.0)`), chained edge-to-edge into a
   rough closed loop around the flagged content. This is *why* `PathEntity`
   needed `color`/`curved` added (see ir.py/pdf_source.py) — bbox alone
   can't tell a scallop arc from any other small stroked curve.
2. **Object count, not item count, is what separates a real cloud from a
   false positive.** The sample has exactly one other place with a
   red curved stroke: a single drawing object (one `get_drawings()` dict)
   with dozens of *internal* curve items, spanning almost the full page
   width — turned out to be vector-text glyph outlines in a small red-
   highlighted key map, not a cloud. A minimum count of *separate* arc
   objects (`_MIN_ARC_COUNT`) filters this out; a per-item count would not
   have, since that one object has plenty of curve items too.
3. **The revision-tag triangle is a distinct 3-line shape, same color.**
   Two diagonal sides plus a horizontal baseline, each its own straight-
   line drawing object, same red as the cloud, sitting right against the
   cloud's edge, with the revision number/letter as ordinary text inside
   its bounding box. The sample sheets also carry unrelated 4-line red
   rectangle highlight boxes elsewhere (around a callout sheet-number in
   the title block) — the line *count* (exactly 3) is what tells a tag
   triangle apart from those, not color or size alone.
4. **Every cloud on the inspected sheet paired cleanly with a triangle
   reading the sheet's own AMEND No.** — this sample's drafting is
   correct, so it doesn't exercise the "cloud tag doesn't match the
   schedule" failure path; that's covered by synthetic tests instead
   (same split test_references.py uses for its resolution algorithm).

*Not yet handled, documented rather than silently assumed*: only a single
digit/letter tag is read from a triangle (mirrors references.py's own
`_TAG_RE` limitation); the cloud/triangle color is a hardcoded constant
calibrated against this sample, not yet project-configurable — a firm
using a different marker color needs a code change until this is folded
into the project rule config schema (PLANNING.md §4's consolidated
schema, not built yet).
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Optional

from pdfchecker.ir import BBox, PathEntity, RevisionCloud, TextWord

# Calibrated against the real sample's clouds/triangles — both drawn in
# solid red. Compared with a small tolerance rather than exact tuple
# equality since PDF color values aren't guaranteed to round-trip exactly.
_CLOUD_COLOR = (1.0, 0.0, 0.0)
_COLOR_TOLERANCE = 0.05

# A real cloud is built from many separate scallop-arc path objects (28-96
# on the sample); the one confirmed false positive (a red key-map text
# glyph) is a single object, so this threshold sits comfortably between
# the two rather than needing to be precise.
_MIN_ARC_COUNT = 6

# Points of slack when unioning touching/adjacent path bboxes into one
# cluster — calibrated against the ~0-3pt real gaps between consecutive
# scallop arcs (and triangle-edge endpoints) on the sample.
_CLUSTER_PAD = 3.0

# A tag triangle is exactly 3 straight-line objects (two sides + a
# baseline) — this is what distinguishes it from the 4-line rectangle
# highlight boxes seen elsewhere on the same sheets, confirmed against
# the sample rather than assumed.
_TRIANGLE_LINE_COUNT = 3
_TRIANGLE_MAX_SIZE = 60.0  # sample's triangles are ~20x20pt; generous upper bound

# How far a triangle cluster can sit from a cloud cluster and still count
# as its paired tag — calibrated against the ~0-15pt real gap on the
# sample (the triangle sits right against the cloud's edge).
_MAX_TRIANGLE_DIST = 80.0

# Same single-digit/single-letter limitation as references.py's own
# marker-tag regex — real revision IDs on the sample are "0", "1", etc.
_TAG_RE = re.compile(r"^[0-9A-Za-z]{1,2}$")


@dataclass
class _Cluster:
    bbox: BBox
    items: list[PathEntity]


def _union(a: BBox, b: BBox) -> BBox:
    return BBox(min(a.x0, b.x0), min(a.y0, b.y0), max(a.x1, b.x1), max(a.y1, b.y1))


def _bbox_distance(a: BBox, b: BBox) -> float:
    """0 if the boxes overlap or touch; otherwise the gap between them."""

    dx = max(0.0, max(a.x0, b.x0) - min(a.x1, b.x1))
    dy = max(0.0, max(a.y0, b.y0) - min(a.y1, b.y1))
    return (dx**2 + dy**2) ** 0.5


def _color_matches(color) -> bool:
    if color is None or len(color) != 3:
        return False
    return all(abs(c - t) <= _COLOR_TOLERANCE for c, t in zip(color, _CLOUD_COLOR))


def _cluster(paths: list[PathEntity], pad: float) -> list[_Cluster]:
    """Groups paths into connected components by bbox proximity (within
    `pad`). Simple greedy merge + repeated pass to catch chained growth
    (two initially-separate clusters that a later-processed path bridges
    together) — the same approach prototyped by hand against the real
    sample's cloud/triangle geometry before writing this."""

    clusters: list[Optional[_Cluster]] = []
    for p in paths:
        merged = None
        for c in clusters:
            if c is not None and _bbox_distance(c.bbox, p.bbox) <= pad:
                merged = c
                break
        if merged is None:
            clusters.append(_Cluster(bbox=p.bbox, items=[p]))
        else:
            merged.items.append(p)
            merged.bbox = _union(merged.bbox, p.bbox)

    changed = True
    while changed:
        changed = False
        for i, ci in enumerate(clusters):
            if ci is None:
                continue
            for j in range(i + 1, len(clusters)):
                cj = clusters[j]
                if cj is None:
                    continue
                if _bbox_distance(ci.bbox, cj.bbox) <= pad:
                    ci.items += cj.items
                    ci.bbox = _union(ci.bbox, cj.bbox)
                    clusters[j] = None
                    changed = True
    return [c for c in clusters if c is not None]


def _find_tag(triangle_bbox: BBox, words: list[TextWord]) -> str | None:
    candidates = [
        w
        for w in words
        if _bbox_distance(triangle_bbox, w.bbox) <= 0 and _TAG_RE.match(w.text.strip())
    ]
    if not candidates:
        return None

    def overlap_area(w: TextWord) -> float:
        ox = max(0.0, min(w.bbox.x1, triangle_bbox.x1) - max(w.bbox.x0, triangle_bbox.x0))
        oy = max(0.0, min(w.bbox.y1, triangle_bbox.y1) - max(w.bbox.y0, triangle_bbox.y0))
        return ox * oy

    candidates.sort(key=overlap_area, reverse=True)
    return candidates[0].text.strip()


def detect_revision_clouds(words: list[TextWord], paths: list[PathEntity]) -> list[RevisionCloud]:
    """Detects revision clouds on a single sheet from its already-
    extracted words/paths. See module docstring for the real convention
    this was calibrated against."""

    same_color = [p for p in paths if _color_matches(p.color)]
    curved = [p for p in same_color if p.curved]
    straight = [p for p in same_color if not p.curved]

    cloud_clusters = [c for c in _cluster(curved, _CLUSTER_PAD) if len(c.items) >= _MIN_ARC_COUNT]
    triangle_clusters = [
        c
        for c in _cluster(straight, _CLUSTER_PAD)
        if len(c.items) == _TRIANGLE_LINE_COUNT
        and c.bbox.width <= _TRIANGLE_MAX_SIZE
        and c.bbox.height <= _TRIANGLE_MAX_SIZE
    ]

    clouds = []
    available_triangles = list(triangle_clusters)
    for cluster in cloud_clusters:
        best, best_dist = None, None
        for tri in available_triangles:
            dist = _bbox_distance(cluster.bbox, tri.bbox)
            if dist > _MAX_TRIANGLE_DIST:
                continue
            if best is None or dist < best_dist:
                best, best_dist = tri, dist

        triangle_bbox = None
        tag = None
        if best is not None:
            triangle_bbox = best.bbox
            tag = _find_tag(best.bbox, words)
            available_triangles.remove(best)  # don't pair the same triangle to two clouds

        clouds.append(
            RevisionCloud(
                bbox=cluster.bbox,
                tag=tag,
                triangle_bbox=triangle_bbox,
                arc_count=len(cluster.items),
            )
        )
    return clouds
