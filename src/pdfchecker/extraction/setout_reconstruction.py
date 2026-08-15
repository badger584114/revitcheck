"""Setout reconstruction — PLANNING.md §5b, Stage 3. Independently derives
each setout point's real-world position from the sheet's own printed
bearing + chained dimension spacing, anchored to a known-coordinate setout
point, and hands the result to `checks/geometry.py`'s
`geometry.setout_reconstruction` rule to compare against the schedule.

**Why this doesn't just compare DXF model geometry to the schedule** (a
simpler design considered and rejected before any code was written — see
CLAUDE.md's Stage 3 status paragraph for the short version): the
schedule's Easting/Northing columns are NOT live/parametric — they're
produced by a separate script/manual process from the same bearing +
dimension inputs a drafter types onto the sheet. The two real failure
modes this check exists to catch are (1) a manual entry mistake in the
bearing or a dimension override, and (2) the schedule going stale because
a dimension changed but the E/N-generating script wasn't rerun. Comparing
DXF model geometry to the schedule wouldn't reliably catch either — the
model can be internally correct while the *printed, manually-entered*
numbers and the *schedule* have drifted apart. So reconstruction has to
walk the same inputs a human/script would: origin (setout point, known
real E/N) -> bearing (printed, manually-entered direction) -> chained
dimension spacing (printed/stated values, same "what a human reading the
sheet would compute" principle as §5a's drawn-vs-stated check) -> an
independently-derived E/N per point.

**Confirmed 2026-08-11 against the real sample** (sheet `2871051` / DXF
`T2DPAA-T2D-C3S-BR-DRG-101051_0.dxf`, the "FOUNDATION LAYOUT" sheet
already calibrated for §5a):

1. **A real setout-point origin with known real coordinates.** An
   `INSERT` named `CS_SYMB_SETOUT POINT...` sits at a local model-space
   position; a nearby `MTEXT` (read via `.plain_text()`, which cleanly
   resolves the `\\P` paragraph-break code the raw `.text` leaves broken)
   reads e.g. `"E 278437.803\\nN 6130709.230"`. One such pair per
   abutment (two found on this sheet).
2. **A real printed bearing per row** — plain `MTEXT`, not an angular
   `DIMENSION` entity: `"BEARING"` labels sit beside DMS-format value
   text (confirmed real values `"175° 08' 40\""`, `"90° 35' 22\""`,
   `"85° 08' 40\""`). One placeholder `"BEARING x° xx' xx\""` also exists
   elsewhere on the sheet — `_parse_bearing_dms` must return `None` for
   this rather than raise, same "skip rather than guess" convention as
   everywhere else in this codebase. Matching a bearing to a chain is by
   nearest DMS-text to the chain's nodes (not by finding the "BEARING"
   label first and reading rightward) — the wrong-row DMS texts are 7-37m
   away on the real sample, comfortably farther than the correct one
   (~1.5m), so nearest-match alone is unambiguous here.
3. **A real, literal dimension chain.** Dumping every `DIMENSION` in one
   abutment's column and sorting by witness-point position shows each
   dimension's `ext_line2_origin` (`defpoint3`) equals the next
   dimension's `ext_line1_origin` (`defpoint2`), to the mm — a genuine
   chained-dimension convention, not something to infer from proximity.
   One extra link branches off partway along the chain to a point very
   near the setout-point `INSERT` (0.19m away — an annotation/leader
   offset, not the same point exactly) — this is how the chain is
   anchored to the known-coordinate origin: `walk_chain` below finds the
   graph node nearest the origin `INSERT` (within
   `origin_pair_max_distance_m`) and treats *that* node, not the origin's
   own exact position, as the zero-point of the walk.
4. **Sign/direction handling.** The chain graph is a simple path with one
   short side-branch (the anchor leaf described above) — never a real
   multi-way branching graph on this sample — so cumulative distance from
   the anchor is unambiguous per node. Which side of the anchor is
   "positive" (matches the bearing's forward direction) is resolved
   without knowing the local-to-real rotation at all: every edge's local
   displacement vector is dot-producted against the chain's own overall
   span vector (its two most-distant nodes) to get a consistent +/- sign,
   then that sign is applied to real-world distance walked along the
   bearing unit vector. This relies on the chain being genuinely
   collinear in local space, which the real sample is (a few cm of
   wiggle over a 34m run) — worth re-confirming if a future sample shows
   a curved/kinked setout line.

**Explicitly not built here:** structures without a printed bearing/
chained-dimension convention at all, and multi-hop survey-tolerance
scaling (`checks/geometry.py`'s `survey_tolerance_mm` is a flat value for
now — every point here is one hop from its anchor in spirit even though
several dimension links are walked to reach it, so PLANNING.md §5's
`base + per_hop×√hops` formula doesn't have a real multi-hop case to
calibrate against yet). Both left for when a sample demands them.

A real multi-branch dimension graph (PLANNING.md §5b's fuller traversal,
originally sketched for structures without a single straight chained
row) is a different case, **confirmed 2026-08-15 directly by the user to
never occur in this domain, not merely unbuilt for lack of a sample**:
bridges are always set out in a linear fashion, specifically so the
construction team gets simple, unambiguous instructions — a real
constraint on how setout is drafted, not just a pattern this codebase
happened to see twice. A genuinely forking chain graph, and the
"redundant paths are a QC signal, reconciled by flagging the
disagreement" design PLANNING.md §5b sketches for it, was a hedge
against a case the drafting convention itself rules out, not a deferred
feature. Point 4 above's "never a real multi-way branching graph on this
sample" turns out to be the general case, not a one-sample observation
waiting on more data. (BR08's A1/B1/B2 sub-chain offset, described
below, is a separate, already-resolved anomaly — a staged/split-design-
ownership package boundary, not a forking chain — and the user
confirmed it doesn't weaken this conclusion.)

**Confirmed 2026-08-11: at the time, there was no more real data in the
BR06 sample to push on the above with.** Ran `reconstruct_sheet` against
every sheet in the full 37-page PDF, attached to all 31 real DWGs
(converted via ODA, not just the two committed to `samples/BR06/dxf/`) —
sheet 2871051 was the *only* sheet in that sample carrying a
setout/coordinate schedule at all; every other sheet had no
`SITE ID`/`EASTING`/`NORTHING`-style table for `parse_pile_schedule` to
find.

**Confirmed 2026-08-14: `samples/BR08/` (a second, real, complete bridge
drawing set added after the above) has more — including a genuine
multi-sheet case**, the first real slice of §5b's fuller "not one sheet"
form:

- Sheet 2873042 carries a pile schedule in the same `SITE ID`/`LOCATION`/
  `EASTING`/`NORTHING` format as BR06 (two tables — "ABUTMENT A AND A1"
  and "ABUTMENT B, B1 AND B2") — but its own DXF has zero `DIMENSION`
  entities and zero pile-label text. It's a pure table sheet.
- Sheet 2873042's raw page text carries one real, unique sentence: "...
  THE SETOUT DIMENSIONS/DETAILS GIVEN ON THE FOUNDATION LAYOUT SHEET NO.
  2873041 TAKE PRECEDENCE OVER THE SET-OUT COORDINATES IN THE TABLES." —
  `find_geometry_sheet_no` reads this off `Sheet.raw_text` (already
  populated by `pipeline.py`, no new extraction needed). A real,
  unrelated "...CONTRACT DOCUMENTS SHALL TAKE PRECEDENCE." boilerplate
  sentence sits on the very same page, confirming the regex has to match
  the whole calibrated phrase, not just "TAKE PRECEDENCE".
- Sheet 2873041 (the referenced "foundation layout" sheet) is a
  self-contained geometry sheet structurally identical to BR06's single-
  sheet case — 46 `DIMENSION` entities, DMS bearing text, 43
  `PIL...`-prefixed pile labels, `ABUTMENT A/A1/B/B1/B2` location labels,
  and 5 `SETOUT POINT` inserts each paired with a real `E .../N ...`
  control-point text (one origin per abutment sub-group, not two).

**Real result, and a real limitation this surfaced (not introduced by
the cross-sheet work above — confirmed present either way):** the two
main groups, `ABUTMENT A` and `ABUTMENT B` (14 piles each, 28 total),
reconstruct to well under 2mm — the cross-sheet mechanism itself is
proven correct by this. The three smaller sub-groups, `ABUTMENT A1`/
`B1`/`B2` (5 piles each), reconstruct with large (1.4m-16m) errors.
`find_location_for_chain`'s `known_locations` scoping *does* generalize
correctly to all five groups for pile-ID matching (`candidate_ids`) —
every pile lands in the right group. What doesn't generalize is origin
and bearing *selection*, which was never location-scoped at all: both
just pick whichever origin/bearing text is nearest to a chain's own
nodes (`min(origins, key=...)`, `find_bearing_for_chain`). With BR06's 2
well-separated abutments this was unambiguous (module text above: wrong
bearings 7-37m away vs. the correct one at ~1.5m). BR08's 5 origins/5
bearings sit in the same rough area as each other, a few meters apart,
so nearest-distance-to-chain-nodes can and does grab the wrong one for
the smaller sub-groups.

A location-scoped fix was attempted and reverted the same session, not
shipped half-verified: greedy one-to-one nearest assignment between
origins/bearings and `ABUTMENT` location labels (mirroring
`match_chain_points_to_piles`'s existing one-to-one pattern) produced a
plausible-looking assignment, but applying it made things *worse*, not
better — `ABUTMENT B` (previously accurate to <2mm) broke to ~2.5m off,
while `A1`/`B2` stayed equally wrong and `B1` started failing
`walk_chain`'s anchor-distance check entirely. That disproved "nearest
label" as the right disambiguating signal, for reasons this session
didn't understand yet — reverted rather than kept, since a fix that
measurably breaks a previously-correct case is worse than an honestly-
reported gap.

So `reconstruct_sheet` grew an optional `geometry_sheet` parameter
(`checks/geometry.py`'s `_resolve_geometry_sheet` resolves it from
`find_geometry_sheet_no` + `Project.sheet_by_no`) — the schedule table
always comes from the `sheet` argument, but the DXF geometry can come
from a *different*, cross-referenced sheet. Defaults to `None` (same
sheet), so BR06's original case is unaffected.

**Re-investigated and resolved 2026-08-14, later session — the real root
cause was not origin/bearing selection at all.** Brute-forcing every
origin×bearing×sign combination for the three broken sub-chains against
the schedule (not shipped — a one-off diagnostic, never how the real
check picks a value) showed the "nearest text" origin and bearing picked
for `A1`/`B1`/`B2` were *already correct* — the wrong-looking result came
from `walk_chain`'s sign/direction step instead. Its "positive walking
direction" is derived from the chain's own two farthest local nodes
(`far_a`/`far_b`) — a real-world-arbitrary choice: nothing ties which of
the two farthest nodes is "positive" to which way the printed bearing
actually faces, it's decided purely by witness-point/entity insertion
order. On BR06's abutments and BR08's two 14-pile main chains, that
arbitrary pick happened to land on the correct real-world direction; on
BR08's three 4-dimension sub-chains it landed backwards on all 3/3 —
confirmed directly: flipping the bearing 180° for each took their error
from a *growing* 1.4m-16m (the signature of a direction flip — the error
compounds with distance from the anchor) down to a *constant* ~1.40m
for every pile in the group.

The one real, checkable-without-the-schedule structural fact that told
the reliably-correct chains apart from the reliably-wrong ones on every
sample seen so far: the main chains are anchored *via a branch* (module
docstring point 3 — a short leader link off a node partway along the
chain, where the setout point attaches), while the broken sub-chains are
bare straight paths with no such fork. `_chain_has_branch` makes that
check concrete (any node with 3+ dimension edges touching it). Fixed via
sign inheritance, not a fresh heuristic on the branch-less chain's own
(unreliably small) node set: `reconstruct_sheet` walks branch-having
chains first, and for each records an *oriented* span vector
(`_oriented_span_from_walk` — real-world-facing, since it's derived from
that chain's own already-walked signed distances, not local order).
`_find_reference_span` then looks for a branch-less chain's nearest
same-line continuation among those already-walked chains — physically
close (BR08's real gaps are ~2m) and running along the same local line
(colinear, not perpendicular) — and `walk_chain`'s new `reference_span`
parameter uses that neighbor's oriented span in place of the branch-less
chain's own guess. No schedule value is read anywhere in this — the
neighbor's direction is established independently, from its own
already-correct anchor+bearing walk, the same way a human re-deriving
the sub-chain by hand would notice "this row keeps going the same way
the main row was going" rather than treat it as a fresh, unrelated
measurement. Real result: `ABUTMENT A1`/`B1`/`B2` now reconstruct to a
*constant* ~1.40m off their schedule row (down from the old 1.4m-16m
growing spread), for all three sub-groups, with the two main groups'
own <2mm accuracy unaffected (they don't need borrowing — they still
walk with their own self-derived span, unchanged).

That remaining constant ~1.40m is real, not a further bug to chase —
confirmed directly by the user, who has access to the actual drawings:
`A1`/`B1`/`B2` were added to this bridge later than the main structure,
as part of a separate retaining-wall design package for the same large
infrastructure project, and use that package's own setout point rather
than the bridge sheet's — a staged/split-design-ownership interface
condition specific to this project, not a reconstruction error to design
away. `checks/geometry.py`'s `geometry.setout_reconstruction` correctly
surfacing these as a high-severity delta Issue is the right behavior;
the fix above is only about making the *reported magnitude* honest (a
constant offset pointing at "systematic origin issue," matching what's
really going on) rather than a fictitious growing-with-distance number
that would send a reviewing engineer looking for a dimension-chain
mistake that was never there.

**Still not built** (unchanged from 2026-08-11, and still without real
data to calibrate against): structures without a printed bearing/
chained-dimension convention at all, and multi-hop survey-tolerance
scaling. The multi-*sheet* half of "the fuller form of §5b" is what BR08
unblocked; a genuinely branching (not single-path) dimension graph is a
separate concern — **confirmed 2026-08-15, directly by the user, to be
resolved rather than open: bridges are always set out in a linear
fashion so the construction team gets simple instructions, so this case
doesn't occur and isn't being waited on** — don't conflate the three.
Also still open: `_chain_has_branch` is a real,
calibrated signal on every sample seen so far (3/3 branch-having chains
correct, 3/3 branch-less chains wrong before this fix), but it isn't a
proof for chains with no colinear branch-having neighbor at all to
borrow from — those still fall back to the original self-derived (and,
in principle, still arbitrary) span, same as before this fix existed.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass

from pdfchecker.ir import DimensionEntity, DxfSheet, Point3D, Sheet, SetoutPoint, Table

# extraction/dxf_source.py's _INSUNITS-resolved unit strings, to meters —
# schedule Easting/Northing columns are always in meters on the real
# sample (column header literally says "(m)"), while a DimensionEntity's
# `measurement` is in the sheet's own real-world unit (DxfSheet.units).
_METERS_PER_UNIT = {
    "mm": 0.001,
    "cm": 0.01,
    "m": 1.0,
    "km": 1000.0,
    "in": 0.0254,
    "ft": 0.3048,
}

# AutoCAD/Revit trailing format characters — same set checks/geometry.py's
# `_parse_stated_mm` strips before parsing an override as a number.
_FORMAT_CHARS_RE = re.compile(r"[​-‏‪-‮]")

# A control-point label's plain text, confirmed on the real sample as
# "E <easting>\nN <northing>" (MTEXT.plain_text() resolves the \P
# paragraph break into a real newline — see this module's docstring).
_CONTROL_POINT_RE = re.compile(
    r"E\s*([\d.]+)\s*\n\s*N\s*([\d.]+)", re.IGNORECASE
)

# A bearing value, confirmed on the real sample as degrees-minutes-seconds
# text like "175° 08' 40\"" — deliberately requires all three numeric
# groups so the real placeholder text ("BEARING x° xx' xx\"") doesn't
# parse as zero degrees, which would be a silent wrong answer rather than
# the "skip, don't guess" this should be.
_BEARING_DMS_RE = re.compile(r"(\d+)\s*°\s*(\d+)\s*'\s*(\d+)\s*\"")

# A cross-sheet setout-precedence note, confirmed real and unique across
# BR08's full 133-page sample (`samples/BR08/`, sheet 2873042 pointing to
# sheet 2873041 — see this module's docstring's 2026-08-14 section).
# Deliberately matches the whole calibrated phrase, not just "TAKE
# PRECEDENCE" — the same sheet also carries an unrelated "...CONTRACT
# DOCUMENTS SHALL TAKE PRECEDENCE." boilerplate sentence a looser pattern
# would misfire on. Like `_BEARING_DMS_RE` above, this is a narrow match
# calibrated to one real, confirmed sentence, not a general "extract any
# sheet reference from any sentence" parser.
_SETOUT_PRECEDENCE_NOTE_RE = re.compile(
    r"SETOUT DIMENSIONS/DETAILS GIVEN ON THE FOUNDATION LAYOUT SHEET NO\.\s*"
    r"(\d{6,8})\s*TAKE PRECEDENCE OVER THE SET-OUT COORDINATES IN THE TABLES",
    re.IGNORECASE,
)


def find_geometry_sheet_no(raw_text: str) -> str | None:
    """Reads a printed cross-sheet setout-precedence note off a schedule
    sheet's raw page text (`Sheet.raw_text`) — the real BR08 convention
    where a pile schedule's own DXF carries no geometry at all, and a
    note instead says a *different* sheet's dimension chain is
    authoritative for setout (see this module's docstring). `None` is the
    normal case: most schedule sheets (e.g. BR06's 2871051) are
    self-contained and carry no such note."""

    m = _SETOUT_PRECEDENCE_NOTE_RE.search(raw_text)
    return m.group(1) if m else None


def _find_header_col(header_row: list[str | None], *substrings: str) -> int | None:
    header = [(c or "").strip().upper() for c in header_row]
    for i, cell in enumerate(header):
        if all(s in cell for s in substrings):
            return i
    return None


def parse_pile_locations(table: Table) -> dict[str, str]:
    """Reads the `LOCATION` column (e.g. `"ABUTMENT A"`, `"OFF STRUCTURE
    BARRIER"`) keyed by `SITE ID` — same header-scanning approach as
    `parse_pile_schedule`, kept separate since not every caller needs it.
    Used by `reconstruct_sheet` to scope which schedule rows a given
    dimension chain is even allowed to match against (see its docstring
    for why pure spatial nearest-match isn't reliable enough on its own)."""

    for row_idx, header_row in enumerate(table.rows):
        id_col = _find_header_col(header_row, "SITE ID")
        location_col = _find_header_col(header_row, "LOCATION")
        if id_col is None or location_col is None:
            continue
        locations = {}
        for row in table.rows[row_idx + 1 :]:
            if id_col >= len(row) or location_col >= len(row):
                continue
            point_id = (row[id_col] or "").strip()
            location = (row[location_col] or "").strip()
            if point_id and location:
                locations[point_id] = location
        return locations
    return {}


def parse_pile_schedule(table: Table) -> list[SetoutPoint]:
    """Reads `SITE ID`/`EASTING (m)`/`NORTHING (m)` columns from any
    `Table` that has them — pdfplumber's ruled-table rows are already
    clean per-column cells (unlike the revision schedule's word-clustered
    extraction), so this just locates the header row and its relevant
    cells by substring match and reads the corresponding cell per data
    row. Returns `[]`, not an error, for a table that isn't a pile
    schedule at all.

    Deliberately does NOT require `table.kind == "schedule"` — confirmed
    on the real sample that `extraction/tables.py`'s classifier only
    catches one of a page's several real pile schedules: a schedule's
    title (e.g. "ABUTMENT A PILE SCHEDULE") sometimes lands in the same
    pdfplumber table as its header row, sometimes in the same table as
    the *previous* schedule's last row — an artifact of how the page's
    ruling lines happen to group rows, not something `_classify`'s
    row[0]-only check can see past. Scanning every row of every table for
    the real header (`SITE ID`/`EASTING`/`NORTHING` cells, wherever they
    land) is what actually finds all of a page's schedules; the resulting
    matched points come out identical for the parts `_classify` does get
    right, and the required substrings are specific enough that a
    non-schedule table (revision schedule, title block fragments) simply
    never matches, so scanning broadly is safe, not just permissive."""

    points = []
    for row_idx, header_row in enumerate(table.rows):
        id_col = _find_header_col(header_row, "SITE ID")
        easting_col = _find_header_col(header_row, "EASTING")
        northing_col = _find_header_col(header_row, "NORTHING")
        if id_col is None or easting_col is None or northing_col is None:
            continue
        for row in table.rows[row_idx + 1 :]:
            if id_col >= len(row) or easting_col >= len(row) or northing_col >= len(row):
                continue
            point_id = (row[id_col] or "").strip()
            try:
                easting = float((row[easting_col] or "").strip())
                northing = float((row[northing_col] or "").strip())
            except ValueError:
                continue
            if not point_id:
                continue
            points.append(SetoutPoint(point_id=point_id, easting=easting, northing=northing))
        break  # only one header row expected per table
    return points


def _xy_dist(a: Point3D, b: Point3D) -> float:
    return math.hypot(a.x - b.x, a.y - b.y)


def _chain_nodes(chain: list[DimensionEntity], tolerance_m: float) -> list[Point3D]:
    """Deduped witness-point node list for a chain — shared by
    `find_bearing_for_chain`/`match_chain_points_to_piles` (which only
    need node *positions*) and `walk_chain` (which additionally needs the
    dimension values between them)."""

    nodes: list[Point3D] = []
    for d in chain:
        for p in (d.ext_line1_origin, d.ext_line2_origin):
            if not any(_xy_dist(p, q) <= tolerance_m for q in nodes):
                nodes.append(p)
    return nodes


def find_dimension_chains(
    dimensions: list[DimensionEntity], link_tolerance_m: float = 0.01
) -> list[list[DimensionEntity]]:
    """Groups dimensions into connected components by shared witness
    points (`ext_line1_origin`/`ext_line2_origin` within
    `link_tolerance_m` of each other) — the real chained-dimension
    convention confirmed on the sample (module docstring, point 3).
    General graph-connectivity logic, not pile-specific; O(n^2) pairwise
    comparison, which is fine at the real scale here (tens of dimensions
    per sheet, not thousands)."""

    n = len(dimensions)
    parent = list(range(n))

    def find(x: int) -> int:
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a: int, b: int) -> None:
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[ra] = rb

    endpoints = [(d.ext_line1_origin, d.ext_line2_origin) for d in dimensions]
    for i in range(n):
        for j in range(i + 1, n):
            if any(_xy_dist(a, b) <= link_tolerance_m for a in endpoints[i] for b in endpoints[j]):
                union(i, j)

    groups: dict[int, list[DimensionEntity]] = {}
    for i in range(n):
        groups.setdefault(find(i), []).append(dimensions[i])
    return list(groups.values())


def find_location_for_chain(
    chain_local_points: list[Point3D],
    texts: list,
    known_locations: set[str],
    max_pair_distance_m: float = 10.0,
) -> str | None:
    """Finds which schedule `LOCATION` value (e.g. `"ABUTMENT A"`) this
    chain belongs to, via the nearest `DxfText` whose content exactly
    matches one of `known_locations` (case/whitespace-insensitive).
    Confirmed necessary on the real sample, not a nice-to-have: a nearby
    but unrelated structure's pile-ID label (`"OFF STRUCTURE BARRIER"`
    piles, no chain of their own on this sheet) can sit geometrically
    *closer* to an abutment chain's end node than that abutment's own
    label does — plain nearest-node matching alone let a barrier pile
    silently steal an abutment pile's chain node (same derived position
    assigned to two different schedule rows). Real `"ABUTMENT A"`/
    `"ABUTMENT B"` DXF text labels sit ~4-5m from their own chain and
    are unambiguously nearest to it (the *other* abutment's label is
    always much farther, being in a different column entirely) — using
    them to scope `match_chain_points_to_piles`'s candidate schedule IDs
    per chain resolves the ambiguity that pure distance can't."""

    normalized = {loc.strip().upper(): loc for loc in known_locations}
    candidates = [
        (min(_xy_dist(t.insert, p) for p in chain_local_points), normalized[key])
        for t in texts
        if (key := t.text.strip().upper()) in normalized
    ]
    if not candidates:
        return None
    distance, location = min(candidates, key=lambda c: c[0])
    return location if distance <= max_pair_distance_m else None


def find_control_points(
    dxf_sheet: DxfSheet,
    insert_substring: str = "SETOUT POINT",
    max_pair_distance_m: float = 5.0,
) -> list[tuple[Point3D, SetoutPoint]]:
    """Pairs each `insert_substring`-named `DxfInsert` (a setout-point
    symbol) to the nearest control-point-coordinate `DxfText` within
    range — module docstring point 1. A `DxfText`/`DxfInsert` pair
    farther apart than `max_pair_distance_m` is not paired (skip, don't
    guess); an unmatched insert produces no entry rather than a wrong one."""

    origins = []
    control_texts = []
    for t in dxf_sheet.texts:
        m = _CONTROL_POINT_RE.search(t.text)
        if m:
            control_texts.append((t, float(m.group(1)), float(m.group(2))))

    for ins in dxf_sheet.inserts:
        if insert_substring.upper() not in ins.name.upper():
            continue
        if not control_texts:
            continue
        nearest = min(control_texts, key=lambda ct: _xy_dist(ins.insert, ct[0].insert))
        text_obj, easting, northing = nearest
        if _xy_dist(ins.insert, text_obj.insert) > max_pair_distance_m:
            continue
        origins.append((ins.insert, SetoutPoint(point_id="", easting=easting, northing=northing)))
    return origins


def _parse_bearing_dms(text: str) -> float | None:
    """Parses a `"175° 08' 40\""`-style bearing into decimal degrees,
    clockwise from north (standard survey bearing convention — confirmed
    consistent with the real sample's rotation, module docstring). `None`
    for anything that doesn't match all three DMS components, including
    the real placeholder text `"BEARING x° xx' xx\""`."""

    cleaned = _FORMAT_CHARS_RE.sub("", text)
    m = _BEARING_DMS_RE.search(cleaned)
    if not m:
        return None
    deg, minute, sec = (float(g) for g in m.groups())
    return deg + minute / 60 + sec / 3600


def find_bearing_for_chain(
    chain_local_points: list[Point3D],
    texts: list,
    max_pair_distance_m: float = 30.0,
) -> float | None:
    """Finds the bearing value text nearest this chain's nodes and parses
    it. Matching is purely nearest-DMS-text-to-chain — module docstring
    point 2 explains why that's unambiguous on the real sample without
    needing to first locate the "BEARING" label itself."""

    candidates = []
    for t in texts:
        deg = _parse_bearing_dms(t.text)
        if deg is None:
            continue
        nearest_dist = min(_xy_dist(t.insert, p) for p in chain_local_points)
        candidates.append((nearest_dist, deg))
    if not candidates:
        return None
    nearest_dist, deg = min(candidates, key=lambda c: c[0])
    if nearest_dist > max_pair_distance_m:
        return None
    return deg


def _effective_value_m(dim: DimensionEntity, meters_per_unit: float) -> float | None:
    """The value a human reading the sheet would use for this link: the
    manually-entered `stated_text` override if present and numeric
    (module docstring's "check the manual entry, not the model"
    principle — same rule §5a's `_parse_stated_mm` applies), else the raw
    measurement. `None` only for a non-numeric override (e.g. a bar-mark
    letter tag, per checks/geometry.py's docstring) — skip that link
    rather than guess."""

    if dim.stated_text is not None:
        cleaned = _FORMAT_CHARS_RE.sub("", dim.stated_text).strip()
        try:
            return float(cleaned)
        except ValueError:
            return None
    return dim.measurement * meters_per_unit


@dataclass
class ChainWalkResult:
    """One node's outcome from walking a chain — mirrors §5b step 6's
    "partial reconstruction is expected, report status per point" rule."""

    local_point: Point3D
    derived: SetoutPoint | None  # None if this node couldn't be reached from the anchor
    signed_distance_m: float = 0.0


def walk_chain(
    chain: list[DimensionEntity],
    origin_local: Point3D,
    origin_real: SetoutPoint,
    bearing_deg: float,
    units: str,
    origin_pair_max_distance_m: float = 5.0,
    snap_tolerance_m: float = 0.01,
    reference_span: tuple[float, float] | None = None,
) -> list[ChainWalkResult] | None:
    """Walks the chain's graph from whichever node is nearest
    `origin_local` (module docstring point 3 — that node is not
    necessarily the origin's own exact position, just the nearest
    dimension witness point to it), accumulating signed real-world
    distance along the bearing unit vector. Returns `None` if no node is
    within `origin_pair_max_distance_m` of the origin — this chain can't
    be anchored, so nothing in it can be reconstructed (report as
    `no_origin`, not a silent empty result, at the call site).

    `reference_span` (module docstring's 2026-08-14 "sign inheritance"
    section) overrides the chain's own two-farthest-nodes span vector —
    used when this chain's own node set is too small/branch-less to
    determine a real-world-anchored direction reliably on its own, in
    favor of a *known-good* neighboring chain's already-oriented span
    (`_oriented_span_from_walk`, real-direction-facing: dot >= 0 always
    means "the neighbor's own increasing signed distance"). `None` (the
    default) keeps the original self-derived behavior byte-identical."""

    meters_per_unit = _METERS_PER_UNIT.get(units)
    if meters_per_unit is None:
        return None

    nodes: list[Point3D] = []

    def node_index(p: Point3D) -> int:
        for idx, q in enumerate(nodes):
            if _xy_dist(p, q) <= snap_tolerance_m:
                return idx
        nodes.append(p)
        return len(nodes) - 1

    adjacency: dict[int, list[tuple[int, float]]] = {}
    for dim in chain:
        value_m = _effective_value_m(dim, meters_per_unit)
        if value_m is None:
            continue
        i = node_index(dim.ext_line1_origin)
        j = node_index(dim.ext_line2_origin)
        adjacency.setdefault(i, []).append((j, value_m))
        adjacency.setdefault(j, []).append((i, value_m))

    if not nodes:
        return None

    anchor = min(range(len(nodes)), key=lambda i: _xy_dist(nodes[i], origin_local))
    if _xy_dist(nodes[anchor], origin_local) > origin_pair_max_distance_m:
        return None

    # Overall span vector — the chain's two most-distant nodes — used to
    # give every edge a consistent +/- sign without knowing the local-to-
    # real rotation (module docstring point 4). This is only a *relative*
    # sign convention, not a real-world-anchored one — nothing here ties
    # "which of the two farthest nodes is which" to the printed bearing's
    # actual forward direction, so on a short/branch-less chain this is a
    # coin flip (module docstring's 2026-08-14 section: confirmed backwards
    # on 3/3 real BR08 sub-chains). `reference_span`, when given, replaces
    # this self-derived guess with a neighboring chain's already-oriented
    # one instead.
    if reference_span is not None:
        span = reference_span
    else:
        far_a, far_b = 0, 0
        best = 0.0
        for i in range(len(nodes)):
            for j in range(i + 1, len(nodes)):
                d = _xy_dist(nodes[i], nodes[j])
                if d > best:
                    best, far_a, far_b = d, i, j
        span = (nodes[far_b].x - nodes[far_a].x, nodes[far_b].y - nodes[far_a].y)

    bearing_rad = math.radians(bearing_deg)
    unit_vec = (math.sin(bearing_rad), math.cos(bearing_rad))  # (dE, dN) per survey convention

    results: dict[int, float] = {anchor: 0.0}
    frontier = [anchor]
    while frontier:
        node = frontier.pop()
        for neighbor, value_m in adjacency.get(node, []):
            if neighbor in results:
                continue
            edge_vec = (nodes[neighbor].x - nodes[node].x, nodes[neighbor].y - nodes[node].y)
            sign = 1.0 if (edge_vec[0] * span[0] + edge_vec[1] * span[1]) >= 0 else -1.0
            results[neighbor] = results[node] + sign * value_m
            frontier.append(neighbor)

    out = []
    for idx, p in enumerate(nodes):
        signed_distance = results.get(idx)
        if signed_distance is None:
            out.append(ChainWalkResult(local_point=p, derived=None))
            continue
        derived = SetoutPoint(
            point_id="",
            easting=origin_real.easting + signed_distance * unit_vec[0],
            northing=origin_real.northing + signed_distance * unit_vec[1],
        )
        out.append(ChainWalkResult(local_point=p, derived=derived, signed_distance_m=signed_distance))
    return out


def _chain_has_branch(chain: list[DimensionEntity], tolerance_m: float) -> bool:
    """True if some witness point in this chain has 3+ dimension edges
    touching it — a real fork/branch in the chain graph, not just a plain
    path. Module docstring's 2026-08-14 section: this is the one real,
    checkable structural fact (no schedule needed) that told BR08's
    reliably-correct chains (the two 14-pile main abutments, each anchored
    via a short branch off their setout-point leader — module docstring
    point 3) apart from the three 4-dimension sub-chains that got
    `walk_chain`'s sign backwards, on every real chain seen so far.
    Doesn't *prove* a branch-having chain's self-derived span is correct
    in general (that heuristic is still a coin flip in principle — see
    `walk_chain`'s docstring), just that it's the only case confirmed
    reliable on real data, so branch-less chains borrow a reference span
    from one instead of trusting their own (`reconstruct_sheet`)."""

    degree: dict[int, int] = {}
    nodes: list[Point3D] = []

    def node_index(p: Point3D) -> int:
        for idx, q in enumerate(nodes):
            if _xy_dist(p, q) <= tolerance_m:
                return idx
        nodes.append(p)
        return len(nodes) - 1

    for d in chain:
        i = node_index(d.ext_line1_origin)
        j = node_index(d.ext_line2_origin)
        degree[i] = degree.get(i, 0) + 1
        degree[j] = degree.get(j, 0) + 1
    return any(deg >= 3 for deg in degree.values())


def _oriented_span_from_walk(
    walk: list[ChainWalkResult],
) -> tuple[float, float] | None:
    """The two farthest-apart nodes in an already-walked chain, as a
    vector oriented so a positive dot product always means "the same
    direction this chain's own signed distance increases in" — i.e. a
    real-world-facing span vector, safe to hand to another chain as
    `walk_chain`'s `reference_span` (module docstring's 2026-08-14
    section). `None` if the walk has fewer than two reconstructed nodes
    to span."""

    reconstructed = [r for r in walk if r.derived is not None]
    if len(reconstructed) < 2:
        return None
    far_a, far_b = reconstructed[0], reconstructed[1]
    best = _xy_dist(far_a.local_point, far_b.local_point)
    for i in range(len(reconstructed)):
        for j in range(i + 1, len(reconstructed)):
            d = _xy_dist(reconstructed[i].local_point, reconstructed[j].local_point)
            if d > best:
                best, far_a, far_b = d, reconstructed[i], reconstructed[j]
    span = (
        far_b.local_point.x - far_a.local_point.x,
        far_b.local_point.y - far_a.local_point.y,
    )
    if far_b.signed_distance_m < far_a.signed_distance_m:
        span = (-span[0], -span[1])
    return span


def _find_reference_span(
    chain_nodes: list[Point3D],
    trustworthy: list[tuple[list[Point3D], tuple[float, float]]],
    max_gap_m: float,
    min_colinearity: float = 0.9,
) -> tuple[float, float] | None:
    """Finds the nearest already-walked, branch-having chain
    (`trustworthy` — a list of `(its nodes, its oriented span)` pairs,
    `reconstruct_sheet`'s pass 1) that this branch-less chain is a real
    local-space continuation of: physically close (within `max_gap_m`,
    the real BR08 gaps are ~2m — module docstring's 2026-08-14 section)
    *and* running along roughly the same local line (`min_colinearity`,
    the cosine of the angle between the two chains' own span directions —
    a perpendicular or unrelated chain shouldn't donate its direction).
    `None` if no such neighbor exists, so the caller falls back to
    `walk_chain`'s own (unreliable but better-than-nothing) self-derived
    span — same "skip rather than guess" spirit, but there's no schedule
    to fall back on here, just the pre-existing heuristic."""

    def own_span(nodes: list[Point3D]) -> tuple[float, float] | None:
        if len(nodes) < 2:
            return None
        a, b = nodes[0], nodes[1]
        best = _xy_dist(a, b)
        for i in range(len(nodes)):
            for j in range(i + 1, len(nodes)):
                d = _xy_dist(nodes[i], nodes[j])
                if d > best:
                    best, a, b = d, nodes[i], nodes[j]
        length = math.hypot(b.x - a.x, b.y - a.y)
        return None if length == 0 else ((b.x - a.x) / length, (b.y - a.y) / length)

    this_dir = own_span(chain_nodes)
    if this_dir is None:
        return None

    best_candidate = None
    best_gap = None
    for other_nodes, other_span in trustworthy:
        gap = min(_xy_dist(p, q) for p in chain_nodes for q in other_nodes)
        if gap > max_gap_m:
            continue
        other_length = math.hypot(*other_span)
        if other_length == 0:
            continue
        other_dir = (other_span[0] / other_length, other_span[1] / other_length)
        cosine = this_dir[0] * other_dir[0] + this_dir[1] * other_dir[1]
        if abs(cosine) < min_colinearity:
            continue
        if best_gap is None or gap < best_gap:
            best_gap, best_candidate = gap, other_span
    return best_candidate


def match_chain_points_to_piles(
    local_points: list[Point3D],
    texts: list,
    schedule_ids: set[str],
    max_pair_distance_m: float = 5.0,
) -> dict[str, Point3D]:
    """Pairs each chain node position to a pile-ID `DxfText` (restricted
    to text matching a real schedule row's `point_id`) — positively
    identifies which schedule row a point belongs to rather than trusting
    chain order alone, per this codebase's standing "skip rather than
    guess" convention. Works on plain local positions (not a `walk_chain`
    result) so it can be reused before a chain has an origin/bearing to
    walk with — see `reconstruct_sheet`, which needs pile identity even
    when reconstruction itself fails, to report *which* schedule points a
    failure affects rather than a bare count.

    Matching is one-to-one (greedy nearest-distance-first assignment, not
    each label independently grabbing its own nearest node) — confirmed
    necessary on the real sample: labels for the sheet's "OFF STRUCTURE
    BARRIER" piles (a separate, undimensioned structure on this same
    sheet, no chain of their own here) sit close enough to an abutment
    chain's end node (0.85-2.49m) to independently "win" a naive nearest-
    node match despite not actually being on that chain — and since two
    different pile IDs would then both resolve to the identical chain
    node/position, that's not just a wrong match, it's a silently
    duplicated one. Greedy exclusive assignment lets the genuinely close
    real matches (the abutment piles this chain is actually for, typically
    well under 2m) claim their node first, leaving a barrier-pile label
    with no remaining node in range — correctly unmatched, not guessed."""

    id_texts_by_id: dict[str, list] = {}
    for t in texts:
        point_id = t.text.strip()
        if point_id in schedule_ids:
            id_texts_by_id.setdefault(point_id, []).append(t)

    candidates = []  # (distance, point_id, node_index)
    for point_id, id_texts in id_texts_by_id.items():
        for node_idx, node in enumerate(local_points):
            distance = min(_xy_dist(node, t.insert) for t in id_texts)
            if distance <= max_pair_distance_m:
                candidates.append((distance, point_id, node_idx))
    candidates.sort(key=lambda c: c[0])

    matched: dict[str, Point3D] = {}
    used_nodes: set[int] = set()
    for distance, point_id, node_idx in candidates:
        if point_id in matched or node_idx in used_nodes:
            continue
        matched[point_id] = local_points[node_idx]
        used_nodes.add(node_idx)
    return matched


@dataclass
class SetoutReconstructionPoint:
    """One schedule point's reconstruction outcome — §5b step 6's
    "partial reconstruction is expected, report status per point, not
    pass/fail" rule, applied at the check-rule level (checks/geometry.py).

    `local_point` (added 2026-08-14, PLANNING.md §8) is this pile's DXF
    model-space position on `geometry_sheet` — the same point
    `extraction/dxf_pdf_transform.py`'s `model_to_pdf_point` needs to
    give `checks/geometry.py`'s Issues a real page-space `bbox`, the same
    way `geometry.dimension_consistency` already does. `None` for
    `"unmatched_pile"` (the pile was never found in the DXF at all, so
    there's no local position to report) — populated for every other
    status, including the two failure statuses, since `matched_local`
    already has the chain node position before origin/bearing/walk is
    even attempted.

    `location` (added 2026-08-15) is the schedule's own `LOCATION` column
    value for this point (`parse_pile_locations`, already computed inside
    `reconstruct_sheet` for its own pile-ID-matching scoping — this just
    exposes it) — e.g. `"ABUTMENT A"`. `checks/geometry.py`'s `geometry.
    ifc_setout_consistency` uses it to scope IFC candidate matching
    geographically (a reconstructed LOCATION group's mean real Easting/
    Northing vs. each IFC element's own — see that rule's docstring for
    the real BR06 figures this is calibrated against). `None` when the
    schedule table had no `LOCATION` column, or this point's row didn't
    carry one — scoping falls back to unscoped matching in that case,
    same "don't restrict where there's no signal to restrict by"
    principle as everywhere else in this module."""

    point_id: str
    stated: SetoutPoint
    derived: SetoutPoint | None
    status: str  # "reconstructed" | "unmatched_pile" | "no_bearing" | "no_origin"
    geometry_sheet_no: str | None = None
    local_point: Point3D | None = None
    location: str | None = None


def reconstruct_sheet(
    sheet: Sheet, config, geometry_sheet: Sheet | None = None
) -> list[SetoutReconstructionPoint]:
    """Ties the module together for one sheet: parse the schedule,
    find every dimension chain + origin, walk each chain that can be
    anchored and has a nearby bearing, and match walked points back to
    schedule rows by pile-ID label. A schedule point never silently
    disappears — every row ends up in the result with a status explaining
    why it couldn't be reconstructed, if it couldn't.

    `geometry_sheet` is the real BR08 cross-sheet case (this module's
    docstring's 2026-08-14 section): the schedule table (`sheet.tables`,
    always read from `sheet` regardless of this parameter) and the DXF
    dimension-chain geometry (`.dxf_sheet`) can live on two *different*
    sheets, linked by a printed setout-precedence note
    (`find_geometry_sheet_no`). Defaults to `None`, meaning "same sheet"
    — BR06's original single-sheet case — with byte-identical behavior to
    before this parameter existed."""

    geometry_sheet_no = (geometry_sheet or sheet).sheet_no
    dxf_sheet = (geometry_sheet or sheet).dxf_sheet
    if dxf_sheet is None:
        return []

    schedule_points: list[SetoutPoint] = []
    locations_by_id: dict[str, str] = {}
    for table in sheet.tables:
        schedule_points.extend(parse_pile_schedule(table))
        locations_by_id.update(parse_pile_locations(table))
    if not schedule_points:
        return []
    schedule_by_id = {p.point_id: p for p in schedule_points}
    schedule_ids = set(schedule_by_id)
    known_locations = set(locations_by_id.values())

    origins = find_control_points(
        dxf_sheet, config.setout_point_insert_substring, config.origin_pair_max_distance_m
    )

    results: dict[str, SetoutReconstructionPoint] = {}

    def record(
        point_id: str, derived: SetoutPoint | None, status: str, local_point: Point3D | None = None
    ) -> None:
        if point_id in results:
            return
        results[point_id] = SetoutReconstructionPoint(
            point_id=point_id,
            stated=schedule_by_id[point_id],
            derived=derived,
            status=status,
            geometry_sheet_no=geometry_sheet_no,
            local_point=local_point,
            location=locations_by_id.get(point_id),
        )

    chains = [c for c in find_dimension_chains(dxf_sheet.dimensions, config.chain_link_tolerance_m) if len(c) >= 2]
    # Branch-having chains first (module docstring's 2026-08-14 "sign
    # inheritance" section) — they're the only case confirmed to resolve
    # `walk_chain`'s own sign correctly on real data, so their oriented
    # span needs to already be in `trustworthy` before a branch-less
    # chain looks for a neighbor to borrow one from. Reordering only
    # changes which pass a chain is walked in, not what it does.
    chains.sort(key=lambda c: not _chain_has_branch(c, config.chain_link_tolerance_m))
    trustworthy: list[tuple[list[Point3D], tuple[float, float]]] = []

    for chain in chains:
        nodes = _chain_nodes(chain, config.chain_link_tolerance_m)

        # Scope candidate schedule IDs to this chain's own LOCATION where
        # one can be identified (module docstring / find_location_for_chain
        # — pure spatial nearest-match alone can't reliably tell this
        # chain's piles apart from an unrelated nearby structure's).
        # Falls back to the full schedule if no location label is found
        # nearby — best-effort, not a hard failure.
        location = find_location_for_chain(
            nodes, dxf_sheet.texts, known_locations, config.bearing_pair_max_distance_m
        )
        candidate_ids = (
            {pid for pid in schedule_ids if locations_by_id.get(pid) == location}
            if location is not None
            else schedule_ids
        )

        matched_local = match_chain_points_to_piles(
            nodes, dxf_sheet.texts, candidate_ids, config.pile_label_pair_max_distance_m
        )
        if not matched_local:
            continue  # this chain isn't a pile row on the schedule at all

        if not origins:
            for point_id, local_point in matched_local.items():
                record(point_id, None, "no_origin", local_point)
            continue
        origin_local, origin_real = min(
            origins, key=lambda o: min(_xy_dist(o[0], p) for p in nodes)
        )

        bearing_deg = find_bearing_for_chain(nodes, dxf_sheet.texts, config.bearing_pair_max_distance_m)
        if bearing_deg is None:
            for point_id, local_point in matched_local.items():
                record(point_id, None, "no_bearing", local_point)
            continue

        # A branch-less chain's own span vector is an unreliable, real-
        # world-arbitrary sign guess (walk_chain's docstring) — borrow an
        # already-walked branch-having neighbor's oriented span instead,
        # when this chain is a real local-space continuation of one
        # (module docstring's 2026-08-14 "sign inheritance" section).
        has_branch = _chain_has_branch(chain, config.chain_link_tolerance_m)
        reference_span = (
            None
            if has_branch
            else _find_reference_span(nodes, trustworthy, config.chain_continuation_max_gap_m)
        )

        walk = walk_chain(
            chain,
            origin_local,
            origin_real,
            bearing_deg,
            dxf_sheet.units,
            config.origin_pair_max_distance_m,
            config.chain_link_tolerance_m,
            reference_span=reference_span,
        )
        if walk is None:
            for point_id, local_point in matched_local.items():
                record(point_id, None, "no_origin", local_point)
            continue

        if has_branch:
            oriented = _oriented_span_from_walk(walk)
            if oriented is not None:
                trustworthy.append((nodes, oriented))

        for point_id, local_point in matched_local.items():
            walked = min(walk, key=lambda r: _xy_dist(r.local_point, local_point))
            if walked.derived is None:
                record(point_id, None, "no_origin", local_point)
            else:
                record(point_id, walked.derived, "reconstructed", local_point)

    for point_id in schedule_ids - set(results):
        results[point_id] = SetoutReconstructionPoint(
            point_id=point_id,
            stated=schedule_by_id[point_id],
            derived=None,
            status="unmatched_pile",
            geometry_sheet_no=geometry_sheet_no,
            location=locations_by_id.get(point_id),
        )

    return list(results.values())
