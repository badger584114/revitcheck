"""Geometry check engine (checks/geometry.py) — PLANNING.md §5a's
drawn-vs-stated dimensional consistency, the first Stage 3 check rule.
Same split as every other check module here: synthetic `DimensionEntity`/
`DxfSheet` objects for the tolerance-logic branches, plus a real-sample
assertion (`samples/dxf/101051`, attached via the real `attach_dxf_sheets`
join) for the "correctly finds nothing wrong on a clean sheet" path —
that sheet's own real override (150mm stated vs. 149.333mm drawn) is
well inside tolerance, so this doubles as a sanity check on the whole
pipeline end to end.

Also covers `geometry.ifc_setout_consistency` (§5's proposed third
geometry source, added 2026-08-12) — `_is_slender_vertical`'s shape
heuristic against synthetic bboxes, and the check itself against real
sheet 101051 reconstruction + a synthetic `IfcModel` (not BR06's real
`.ifc` file — that geometry-kernel ingestion is genuinely slow, see
tests/test_ifc_source.py's docstring, and isn't needed to exercise this
rule's own matching/tolerance logic once `reconstruct_sheet`'s real
output is available).
"""

import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

import fitz  # noqa: E402
import pdfplumber  # noqa: E402
import pytest  # noqa: E402

from pdfchecker.checks.catalog import RuleConfig  # noqa: E402
from pdfchecker.checks.geometry import (  # noqa: E402
    _cached_reconstruct_sheet,
    _is_slender_vertical,
    _parse_stated_mm,
    _reconstruction_cache,
    _resolve_geometry_sheet,
    check_dimension_consistency,
    check_ifc_setout_consistency,
    check_setout_reconstruction,
)
from pdfchecker.extraction.dxf_source import attach_dxf_sheets, ingest_dxf  # noqa: E402
from pdfchecker.extraction.tables import extract_tables  # noqa: E402
from pdfchecker.ir import (  # noqa: E402
    BBox,
    DimensionEntity,
    DxfInsert,
    DxfSheet,
    DxfText,
    IfcElement,
    IfcModel,
    Point3D,
    Project,
    Sheet,
    Table,
    TitleBlock,
    ViewportEntity,
)

SAMPLE_PDF = str(
    Path(__file__).resolve().parent.parent / "samples" / "BR06" / "T2DPAA-T2D-C3S-BR-DRG-101000.pdf"
)

SAMPLE_DXF = str(
    Path(__file__).resolve().parent.parent
    / "samples"
    / "BR06"
    / "dxf"
    / "T2DPAA-T2D-C3S-BR-DRG-101051_0.dxf"
)

# BR08 — the real multi-sheet cross-sheet case (checks/geometry.py's
# module docstring, extraction/setout_reconstruction.py's docstring's
# 2026-08-14 section): schedule sheet 2873042 has no DXF geometry of its
# own and instead carries a printed note pointing at sheet 2873041.
BR08_DIR = Path(__file__).resolve().parent.parent / "samples" / "BR08"
BR08_SAMPLE_PDF = str(BR08_DIR / "T2DPAA-T2D-C3S-BR-DRG-103000.pdf")
BR08_GEOMETRY_DXF = str(BR08_DIR / "dxf" / "T2DPAA-T2D-C3S-BR-DRG-103041_0.dxf")

# The real setout-precedence note's calibrated phrase (module docstring's
# `_SETOUT_PRECEDENCE_NOTE_RE`), parameterized by sheet number for
# synthetic tests.
_PRECEDENCE_NOTE = (
    "THE SETOUT DIMENSIONS/DETAILS GIVEN ON THE FOUNDATION LAYOUT SHEET NO. "
    "{sheet_no} TAKE PRECEDENCE OVER THE SET-OUT COORDINATES IN THE TABLES."
)


def _sheet(sheet_no: str, dxf_sheet: DxfSheet) -> Sheet:
    s = Sheet(
        page_index=0,
        page_width=100.0,
        page_height=100.0,
        title_block=TitleBlock(fields={"sheet_no": sheet_no}),
        revision_schedule=[],
        tables=[],
        words=[],
        paths=[],
        raw_text="",
    )
    s.dxf_sheet = dxf_sheet
    return s


def _synthetic_sheet(sheet_no: str, *, raw_text: str = "", tables=None, dxf_sheet=None) -> Sheet:
    return Sheet(
        page_index=0,
        page_width=100.0,
        page_height=100.0,
        title_block=TitleBlock(fields={"sheet_no": sheet_no}),
        revision_schedule=[],
        tables=tables or [],
        words=[],
        paths=[],
        raw_text=raw_text,
        dxf_sheet=dxf_sheet,
    )


def _real_sheet_101051() -> Sheet:
    # Fresh tables + DXF each call — callers may mutate the schedule rows
    # (see test_mismatch_beyond_survey_tolerance_flagged_high above), so
    # sharing one parsed copy across tests would leak state between them.
    with pdfplumber.open(SAMPLE_PDF) as pdf:
        tables = extract_tables(pdf.pages[14])
    return Sheet(
        page_index=14,
        page_width=100.0,
        page_height=100.0,
        title_block=TitleBlock(fields={"sheet_no": "2871051"}),
        revision_schedule=[],
        tables=tables,
        words=[],
        paths=[],
        raw_text="",
        dxf_sheet=ingest_dxf(SAMPLE_DXF),
    )


def _ifc_pile(global_id: str, easting: float, northing: float, footprint: float = 0.75, height: float = 10.55) -> IfcElement:
    # A real BR06 pile's actual footprint/height (extraction/ifc_source.py's
    # docstring) — world-space bbox centered horizontally on (easting,
    # northing), matching how checks/geometry.py reads element position
    # (the midpoint of bbox_min/bbox_max, not a separate centroid field).
    half = footprint / 2
    return IfcElement(
        global_id=global_id,
        ifc_class="IfcSlab",
        predefined_type="BASESLAB",
        display_name="01_SFO_FRP_Pile_CastInPlace_CS:CAST-IN-PLACE PILE - 0750:synthetic",
        bbox_min=Point3D(x=easting - half, y=northing - half, z=0.0),
        bbox_max=Point3D(x=easting + half, y=northing + half, z=height),
    )


def _dim(measurement: float, stated_text, layer: str = "D-ENHA-TEXT-DIMS", dim_type: int = 0) -> DimensionEntity:
    p = Point3D(0.0, 0.0, 0.0)
    return DimensionEntity(
        measurement=measurement,
        stated_text=stated_text,
        dim_line_point=p,
        ext_line1_origin=p,
        ext_line2_origin=p,
        dimstyle="Dimension_Standard_O__mm_",
        layer=layer,
        dim_type=dim_type,
    )


# --- _parse_stated_mm -----------------------------------------------------


def test_parse_stated_mm_strips_left_to_right_mark():
    # The real gotcha found on the sample — a trailing U+200E that's
    # invisible in a terminal/editor.
    assert _parse_stated_mm("150‎") == 150.0


def test_parse_stated_mm_rejects_letter_tag():
    # The real bar-mark/schedule-key convention found on sheet 101151.
    assert _parse_stated_mm("A") is None


# --- synthetic: tolerance logic -------------------------------------------


def test_mismatch_beyond_tolerance_flagged():
    dxf_sheet = DxfSheet(
        source_path="x.dxf",
        dimensions=[_dim(measurement=3.000, stated_text="3010")],  # 3000mm drawn vs 3010mm stated
        viewports=[],
        units="m",
    )
    project = Project(source_path="synthetic", sheets=[_sheet("2871099", dxf_sheet)])
    issues = check_dimension_consistency(project, RuleConfig())
    assert len(issues) == 1
    assert issues[0].category == "geometry"
    assert issues[0].severity == "high"
    assert issues[0].suggested_fix["drawn_mm"] == 3000.0
    assert issues[0].suggested_fix["stated_mm"] == 3010.0
    # This synthetic DxfSheet has no viewports, so there's nothing for
    # extraction/dxf_pdf_transform.py's model_to_pdf_point to map this
    # dimension's points through — correctly None, not a sign the
    # transform itself is missing (see test_bbox_populated_when_dimension_
    # falls_within_a_viewport below for the transform actually running).
    assert issues[0].bbox is None


def test_bbox_populated_when_dimension_falls_within_a_viewport():
    # Same mismatch as the test above, but this DxfSheet has a real
    # viewport covering the dimension's (model-space) location — proves
    # check_dimension_consistency's Issue.bbox is actually wired up to
    # extraction/dxf_pdf_transform.py, not just that the transform module
    # works in isolation. Unit-scale viewport (ps == view, no zoom) keeps
    # the expected page-space numbers simple to reason about.
    p = Point3D(5.0, 5.0, 0.0)
    dim = DimensionEntity(
        measurement=3.000,
        stated_text="3010",
        dim_line_point=p,
        ext_line1_origin=p,
        ext_line2_origin=p,
        dimstyle="Dimension_Standard_O__mm_",
        layer="D-ENHA-TEXT-DIMS",
        dim_type=0,
    )
    viewport = ViewportEntity(
        id=1,
        ps_center=Point3D(0.005, 0.005, 0.0),  # 5mm, 5mm — same as the dim's model point
        ps_width=0.02,
        ps_height=0.02,
        view_center_point=p,
        view_height=0.02,  # scale = ps_height / view_height = 1:1
    )
    dxf_sheet = DxfSheet(
        source_path="x.dxf", dimensions=[dim], viewports=[viewport], units="m"
    )
    project = Project(source_path="synthetic", sheets=[_sheet("2871099", dxf_sheet)])
    issues = check_dimension_consistency(project, RuleConfig())
    assert len(issues) == 1
    bbox = issues[0].bbox
    assert bbox is not None
    # paper (0.005, 0.005)m -> pdf (0.005*2834.6457, page_height(100) - 0.005*2834.6457)
    expected_x = 0.005 * (72 / 25.4) * 1000
    assert bbox.x0 == pytest.approx(expected_x, abs=0.01)
    assert bbox.y0 == pytest.approx(100.0 - expected_x, abs=0.01)


def test_mismatch_within_default_tolerance_no_issue():
    # Default tolerance is rounding_grid_default_mm/2 + measurement_epsilon_mm
    # = 5/2 + 0.5 = 3.0mm by default — 2mm off should pass.
    dxf_sheet = DxfSheet(
        source_path="x.dxf",
        dimensions=[_dim(measurement=3.000, stated_text="3002")],
        viewports=[],
        units="m",
    )
    project = Project(source_path="synthetic", sheets=[_sheet("2871099", dxf_sheet)])
    assert check_dimension_consistency(project, RuleConfig()) == []


def test_setout_critical_layer_uses_tighter_tolerance():
    # Same 2mm-off dimension as the passing default-tier test above, but
    # on a layer configured as setout_critical (tolerance 1/2 + 0.5 = 1.0mm)
    # — should now flag.
    dxf_sheet = DxfSheet(
        source_path="x.dxf",
        dimensions=[_dim(measurement=3.000, stated_text="3002", layer="C-BEARING")],
        viewports=[],
        units="m",
    )
    project = Project(source_path="synthetic", sheets=[_sheet("2871099", dxf_sheet)])
    config = RuleConfig(setout_critical_layers=["C-BEARING"])
    issues = check_dimension_consistency(project, config)
    assert len(issues) == 1
    assert "setout_critical" in issues[0].description


def test_no_override_no_comparison():
    dxf_sheet = DxfSheet(
        source_path="x.dxf",
        dimensions=[_dim(measurement=3.000, stated_text=None)],
        viewports=[],
        units="m",
    )
    project = Project(source_path="synthetic", sheets=[_sheet("2871099", dxf_sheet)])
    assert check_dimension_consistency(project, RuleConfig()) == []


def test_non_linear_dim_type_skipped_even_with_mismatch():
    # dim_type=4 (radius, on the real sample's own dimtype enumeration) —
    # a huge apparent "mismatch" here must NOT be flagged, since measurement
    # isn't a comparable length for this dimension kind.
    dxf_sheet = DxfSheet(
        source_path="x.dxf",
        dimensions=[_dim(measurement=3.000, stated_text="9999", dim_type=4)],
        viewports=[],
        units="m",
    )
    project = Project(source_path="synthetic", sheets=[_sheet("2871099", dxf_sheet)])
    assert check_dimension_consistency(project, RuleConfig()) == []


def test_letter_tag_override_skipped_even_with_mismatch():
    dxf_sheet = DxfSheet(
        source_path="x.dxf",
        dimensions=[_dim(measurement=3.000, stated_text="A")],
        viewports=[],
        units="m",
    )
    project = Project(source_path="synthetic", sheets=[_sheet("2871099", dxf_sheet)])
    assert check_dimension_consistency(project, RuleConfig()) == []


def test_sheet_without_dxf_data_skipped():
    sheet = Sheet(
        page_index=0,
        page_width=100.0,
        page_height=100.0,
        title_block=TitleBlock(fields={"sheet_no": "2871099"}),
        revision_schedule=[],
        tables=[],
        words=[],
        paths=[],
        raw_text="",
    )  # dxf_sheet left as its default None — a drafting-only run
    project = Project(source_path="synthetic", sheets=[sheet])
    assert check_dimension_consistency(project, RuleConfig()) == []


# --- against the real sample -----------------------------------------------


def test_no_issues_on_real_clean_sheet():
    # samples/dxf/101051's one real override (150mm stated vs 149.333mm
    # drawn) is well inside tolerance — exercises the full real pipeline
    # (ingest_dxf -> attach_dxf_sheets -> check_dimension_consistency),
    # not just the tolerance math in isolation.
    sheet = Sheet(
        page_index=0,
        page_width=100.0,
        page_height=100.0,
        title_block=TitleBlock(fields={"sheet_no": "2871051"}),
        revision_schedule=[],
        tables=[],
        words=[],
        paths=[],
        raw_text="",
    )
    project = Project(source_path="synthetic", sheets=[sheet])
    dxf_sheet = ingest_dxf(SAMPLE_DXF)

    matched = attach_dxf_sheets(project, [dxf_sheet])

    assert matched == 1
    assert sheet.dxf_sheet is dxf_sheet
    assert check_dimension_consistency(project, RuleConfig()) == []


# --- geometry.setout_reconstruction (§5b) -----------------------------------
# extraction/setout_reconstruction.py's mechanics are covered in depth by
# tests/test_setout_reconstruction.py; these just check the thin rule
# wrapper turns a real reconstruction result into the right Issues.


def test_no_issues_on_real_sample_within_tolerance():
    # samples/dxf/101051's 24 real piles all reconstruct within ~7mm of
    # their schedule row (test_setout_reconstruction.py has the per-pile
    # figures) — well inside the 10mm default survey_tolerance_mm, so the
    # only Issues should be the four "OFF STRUCTURE BARRIER" piles that
    # have no dimension chain of their own on this sheet (low severity,
    # informational — not a geometric mismatch).
    with pdfplumber.open(SAMPLE_PDF) as pdf:
        tables = extract_tables(pdf.pages[14])
    sheet = Sheet(
        page_index=14,
        page_width=100.0,
        page_height=100.0,
        title_block=TitleBlock(fields={"sheet_no": "2871051"}),
        revision_schedule=[],
        tables=tables,
        words=[],
        paths=[],
        raw_text="",
        dxf_sheet=ingest_dxf(SAMPLE_DXF),
    )
    project = Project(source_path="synthetic", sheets=[sheet])

    issues = check_setout_reconstruction(project, RuleConfig())
    assert len(issues) == 4
    assert all(i.severity == "low" for i in issues)
    assert all(i.category == "geometry" for i in issues)


def test_mismatch_beyond_survey_tolerance_flagged_high():
    # Same shape as test_mismatch_beyond_tolerance_flagged above, one
    # level up: a schedule row whose stated coordinate disagrees with the
    # sheet's own bearing + dimension chain reconstruction.
    with pdfplumber.open(SAMPLE_PDF) as pdf:
        tables = extract_tables(pdf.pages[14])
    for table in tables:
        for row in table.rows:
            if row and row[0] == "PIL234301":
                # Real stated Easting is 278435.835 — push it 50mm off.
                row[7] = "278435.885"
    sheet = Sheet(
        page_index=14,
        page_width=100.0,
        page_height=100.0,
        title_block=TitleBlock(fields={"sheet_no": "2871051"}),
        revision_schedule=[],
        tables=tables,
        words=[],
        paths=[],
        raw_text="",
        dxf_sheet=ingest_dxf(SAMPLE_DXF),
    )
    project = Project(source_path="synthetic", sheets=[sheet])

    issues = check_setout_reconstruction(project, RuleConfig())
    mismatch = [i for i in issues if "PIL234301" in i.description and "schedule states" in i.description]
    assert len(mismatch) == 1
    assert mismatch[0].severity == "high"
    assert mismatch[0].suggested_fix["delta_mm"] > 40


# --- _is_slender_vertical (schema-general pile-shape heuristic) ------------
# extraction/ifc_source.py's docstring has the real BR06 geometry this is
# calibrated against — piles ~0.75m x 0.75m x 10.55m, pile caps ~1.1m x
# 1.1m x 1.15m, ordinary slabs/floors large-footprint and thin.


def test_is_slender_vertical_identifies_pile_shape():
    pile = _ifc_pile("g1", 0.0, 0.0)  # 0.75m footprint, 10.55m tall
    assert _is_slender_vertical(pile, RuleConfig()) is True


def test_is_slender_vertical_rejects_flat_slab():
    slab = IfcElement(
        global_id="g2",
        ifc_class="IfcSlab",
        predefined_type="FLOOR",
        display_name="Floor:375 Deck Slab",
        bbox_min=Point3D(x=0.0, y=0.0, z=0.0),
        bbox_max=Point3D(x=9.0, y=3.0, z=0.25),  # large footprint, thin
    )
    assert _is_slender_vertical(slab, RuleConfig()) is False


def test_is_slender_vertical_rejects_roughly_cubic_pile_cap():
    pile_cap = IfcElement(
        global_id="g3",
        ifc_class="IfcSlab",
        predefined_type="BASESLAB",
        display_name="01_SFO_FRC_PileCap_CastInPlace_CS",
        bbox_min=Point3D(x=0.0, y=0.0, z=0.0),
        bbox_max=Point3D(x=1.1, y=1.1, z=1.15),  # roughly cubic, footprint under 2m but low aspect ratio
    )
    assert _is_slender_vertical(pile_cap, RuleConfig()) is False


def test_is_slender_vertical_rejects_wide_slender_shape():
    # Tall but wide (a lift shaft wall, say) — the footprint cap matters
    # independently of the aspect-ratio check, not just ratio alone.
    wide_tall = IfcElement(
        global_id="g4",
        ifc_class="IfcWall",
        predefined_type=None,
        display_name=None,
        bbox_min=Point3D(x=0.0, y=0.0, z=0.0),
        bbox_max=Point3D(x=5.0, y=0.3, z=20.0),
    )
    assert _is_slender_vertical(wide_tall, RuleConfig()) is False


# --- geometry.ifc_setout_consistency ----------------------------------------
# Reuses the real sheet 101051 reconstruction (same as
# geometry.setout_reconstruction's real-sample tests above) but injects a
# synthetic IfcModel rather than loading BR06's real .ifc file — that
# geometry-kernel pass is genuinely slow (tests/test_ifc_source.py's
# docstring), and isn't needed to test this rule's own matching/tolerance
# logic once reconstruct_sheet's real output is available. Real Easting/
# Northing values below (PIL234301, PIL234321) are the actual schedule
# values on this sheet, confirmed 2026-08-12.


def test_no_ifc_model_no_issues():
    sheet = _real_sheet_101051()
    project = Project(source_path="synthetic", sheets=[sheet])  # ifc_model left at its default None
    assert check_ifc_setout_consistency(project, RuleConfig()) == []


def test_no_slender_candidates_in_model_reports_coverage_gap():
    # A model with elements but none pile-shaped (e.g. piles genuinely
    # weren't modeled) — this must NOT read as "checked, all clean" in the
    # output (CLAUDE.md: "partial reconstruction should report a
    # confidence/coverage indicator rather than failing silently"), so
    # it's one low-severity Issue per sheet that had points to check, not
    # a silent [] the same as "no IFC model attached at all".
    sheet = _real_sheet_101051()
    slab = IfcElement(
        global_id="g1",
        ifc_class="IfcSlab",
        predefined_type="FLOOR",
        display_name="Floor",
        bbox_min=Point3D(x=278435.0, y=6130732.0, z=0.0),
        bbox_max=Point3D(x=278444.0, y=6130735.0, z=0.25),
    )
    project = Project(
        source_path="synthetic",
        sheets=[sheet],
        ifc_model=IfcModel(source_path="x.ifc", schema="IFC4", length_unit="METRE", has_map_conversion=False, site_ref_lat=None, site_ref_long=None, elements=[slab]),
    )
    issues = check_ifc_setout_consistency(project, RuleConfig())
    assert len(issues) == 1
    assert issues[0].severity == "low"
    assert "none match the pile-shape heuristic" in issues[0].description
    assert issues[0].suggested_fix["candidate_count"] == 0


def test_ifc_pile_within_tolerance_no_issue():
    # PIL234301's real stated Easting/Northing — placing a synthetic IFC
    # pile exactly there should match well inside the default 10mm
    # tolerance (real reconstruction accuracy is ~7mm, confirmed in
    # test_setout_reconstruction.py).
    sheet = _real_sheet_101051()
    ifc_model = IfcModel(
        source_path="x.ifc", schema="IFC4", length_unit="METRE", has_map_conversion=False,
        site_ref_lat=None, site_ref_long=None,
        elements=[_ifc_pile("pile-301", 278435.835, 6130732.397)],
    )
    project = Project(source_path="synthetic", sheets=[sheet], ifc_model=ifc_model)

    issues = check_ifc_setout_consistency(project, RuleConfig())
    own_issues = [i for i in issues if "PIL234301" in i.description]
    assert own_issues == []


def test_ifc_pile_beyond_tolerance_flagged_high():
    # Same real PIL234301 position, offset 50mm in Easting.
    sheet = _real_sheet_101051()
    ifc_model = IfcModel(
        source_path="x.ifc", schema="IFC4", length_unit="METRE", has_map_conversion=False,
        site_ref_lat=None, site_ref_long=None,
        elements=[_ifc_pile("pile-301", 278435.885, 6130732.397)],
    )
    project = Project(source_path="synthetic", sheets=[sheet], ifc_model=ifc_model)

    issues = check_ifc_setout_consistency(project, RuleConfig())
    mismatch = [i for i in issues if "PIL234301" in i.description]
    assert len(mismatch) == 1
    assert mismatch[0].severity == "high"
    assert mismatch[0].suggested_fix["delta_mm"] > 40
    assert mismatch[0].suggested_fix["ifc_global_id"] == "pile-301"


def test_ifc_no_candidate_within_search_radius_flagged_low():
    # Only one real IFC pile placed, at PIL234301's position (Abutment A).
    # PIL234321 is a real reconstructable pile on the *other* abutment,
    # ~8m away — well beyond the default 2m ifc_match_max_distance_m, so
    # it should be reported as "no match found", not compared against the
    # wrong pile.
    sheet = _real_sheet_101051()
    ifc_model = IfcModel(
        source_path="x.ifc", schema="IFC4", length_unit="METRE", has_map_conversion=False,
        site_ref_lat=None, site_ref_long=None,
        elements=[_ifc_pile("pile-301", 278435.835, 6130732.397)],
    )
    project = Project(source_path="synthetic", sheets=[sheet], ifc_model=ifc_model)

    issues = check_ifc_setout_consistency(project, RuleConfig())
    no_match = [i for i in issues if "PIL234321" in i.description]
    assert len(no_match) == 1
    assert no_match[0].severity == "low"
    assert no_match[0].suggested_fix["nearest_distance_m"] > 2.0


def test_unreconstructed_points_never_checked_against_ifc():
    # PIL234341 is one of the real "OFF STRUCTURE BARRIER" piles
    # geometry.setout_reconstruction already can't reconstruct
    # (status="unmatched_pile") — it must never appear here either, even
    # with zero IFC candidates anywhere near it (nothing to cross-check).
    sheet = _real_sheet_101051()
    ifc_model = IfcModel(
        source_path="x.ifc", schema="IFC4", length_unit="METRE", has_map_conversion=False,
        site_ref_lat=None, site_ref_long=None,
        elements=[_ifc_pile("pile-301", 278435.835, 6130732.397)],
    )
    project = Project(source_path="synthetic", sheets=[sheet], ifc_model=ifc_model)

    issues = check_ifc_setout_consistency(project, RuleConfig())
    assert all("PIL234341" not in i.description for i in issues)


def test_one_to_one_assignment_no_double_claiming():
    # Real fix, 2026-08-12: the original version matched every point
    # independently against the same global "nearest" element, so two
    # points near each other could both claim it. PIL234301 and PIL234302
    # are real, consecutive piles on this sheet (~3.02m apart) — a single
    # synthetic candidate placed at their midpoint sits ~1.51m from each,
    # within the default 2m ifc_match_max_distance_m for BOTH. Before the
    # fix, both would independently report a ~1512mm "mismatch" against
    # this one element. After the fix, exactly one of them claims it
    # (mismatch, since 1512mm is well beyond the 10mm tolerance) and the
    # other reports "already matched to a different, closer point" instead
    # of a second, misleading mismatch against the same global_id.
    sheet = _real_sheet_101051()
    midpoint_easting = (278435.835 + 278436.09) / 2
    midpoint_northing = (6130732.397 + 6130729.383) / 2
    ifc_model = IfcModel(
        source_path="x.ifc", schema="IFC4", length_unit="METRE", has_map_conversion=False,
        site_ref_lat=None, site_ref_long=None,
        elements=[_ifc_pile("mid-candidate", midpoint_easting, midpoint_northing)],
    )
    project = Project(source_path="synthetic", sheets=[sheet], ifc_model=ifc_model)

    issues = check_ifc_setout_consistency(project, RuleConfig())
    own_issues = [i for i in issues if "PIL234301" in i.description or "PIL234302" in i.description]

    mismatches = [i for i in own_issues if i.suggested_fix.get("ifc_global_id") == "mid-candidate"]
    already_claimed = [i for i in own_issues if "already matched to a different" in i.description]
    assert len(mismatches) == 1  # never both, unlike before the fix
    assert mismatches[0].severity == "high"
    assert len(already_claimed) == 1
    assert already_claimed[0].severity == "low"
    assert already_claimed[0].suggested_fix["derived"] is not None


def test_reconstruction_cache_shared_across_both_rules():
    # Real fix, 2026-08-12: geometry.setout_reconstruction and
    # geometry.ifc_setout_consistency each independently called
    # extraction/setout_reconstruction.py's reconstruct_sheet per sheet —
    # a normal run has both enabled together (RuleConfig.enabled_rule_ids
    # defaults to every registered rule), so the whole schedule-parse +
    # dimension-chain-walk pipeline ran twice per sheet with the second
    # pass's result discarded. Whitebox check that the cache is actually
    # populated and reused, not just that both checks still produce
    # correct results (covered by the other real-sample tests here).
    sheet = _real_sheet_101051()
    config = RuleConfig()

    _reconstruction_cache.clear()
    result_a = _cached_reconstruct_sheet(sheet, config)
    result_b = _cached_reconstruct_sheet(sheet, config)
    assert result_a is result_b  # same cached list object, not recomputed
    assert len(_reconstruction_cache) == 1


def test_reconstruction_cache_entry_freed_with_its_sheet():
    # The cache is keyed by id(sheet), not the (unhashable) Sheet object
    # itself — safe only because a weakref finalizer removes the entry
    # the moment that Sheet is garbage-collected, so a later object
    # reusing the same freed id can never see a stale cached result.
    import gc

    _reconstruction_cache.clear()
    sheet = _real_sheet_101051()
    _cached_reconstruct_sheet(sheet, RuleConfig())
    assert len(_reconstruction_cache) == 1

    del sheet
    gc.collect()
    assert len(_reconstruction_cache) == 0


# --- _resolve_geometry_sheet -----------------------------------------------


def test_resolve_geometry_sheet_returns_self_when_no_note():
    schedule_sheet = _synthetic_sheet("1000001", raw_text="No special note on this sheet.")
    project = Project(source_path="synthetic", sheets=[schedule_sheet])
    assert _resolve_geometry_sheet(schedule_sheet, project) is schedule_sheet


def test_resolve_geometry_sheet_follows_precedence_note():
    geometry_dxf = DxfSheet(source_path="geom.dxf", dimensions=[], viewports=[], units="m")
    geometry_sheet = _synthetic_sheet("1000002", dxf_sheet=geometry_dxf)
    schedule_sheet = _synthetic_sheet(
        "1000001", raw_text=_PRECEDENCE_NOTE.format(sheet_no="1000002")
    )
    project = Project(source_path="synthetic", sheets=[schedule_sheet, geometry_sheet])
    assert _resolve_geometry_sheet(schedule_sheet, project) is geometry_sheet


def test_resolve_geometry_sheet_falls_back_when_referenced_sheet_missing():
    schedule_sheet = _synthetic_sheet(
        "1000001", raw_text=_PRECEDENCE_NOTE.format(sheet_no="9999999")
    )
    project = Project(source_path="synthetic", sheets=[schedule_sheet])
    assert _resolve_geometry_sheet(schedule_sheet, project) is schedule_sheet


def test_resolve_geometry_sheet_falls_back_when_referenced_sheet_has_no_dxf():
    geometry_sheet = _synthetic_sheet("1000002", dxf_sheet=None)
    schedule_sheet = _synthetic_sheet(
        "1000001", raw_text=_PRECEDENCE_NOTE.format(sheet_no="1000002")
    )
    project = Project(source_path="synthetic", sheets=[schedule_sheet, geometry_sheet])
    assert _resolve_geometry_sheet(schedule_sheet, project) is schedule_sheet


# --- check_setout_reconstruction: cross-sheet ------------------------------


def test_check_setout_reconstruction_skips_schedule_sheet_with_no_dxf_and_no_note():
    # Regression for the loop-guard fix: a schedule sheet with no DXF of
    # its own and no cross-reference note must not crash, and produces no
    # Issues (nothing to check against) — same as before this feature.
    schedule_sheet = _synthetic_sheet("1000001", raw_text="", dxf_sheet=None)
    project = Project(source_path="synthetic", sheets=[schedule_sheet])
    assert check_setout_reconstruction(project, RuleConfig()) == []


def _chain_dim(p1: Point3D, p2: Point3D, measurement: float) -> DimensionEntity:
    return DimensionEntity(
        measurement=measurement,
        stated_text=None,
        dim_line_point=p1,
        ext_line1_origin=p1,
        ext_line2_origin=p2,
        dimstyle="Dimension_Standard_O__mm_",
        layer="D-ENHA-TEXT-DIMS",
        dim_type=0,
    )


def _cross_sheet_synthetic_project(stated_easting: float, stated_northing: float) -> Project:
    # A schedule sheet with no DXF of its own, a precedence note pointing
    # at a second sheet, and that second sheet carrying a real
    # reconstructable chain (bearing due east, origin at E1000/N2000,
    # 20m to the pile) — mirrors
    # test_setout_reconstruction.py's synthetic cross-sheet fixture.
    schedule_table = Table(
        kind="unknown",
        rows=[
            ["SITE ID", "LOCATION", "EASTING (m)", "NORTHING (m)"],
            ["PIL001", "ABUTMENT A", f"{stated_easting:.3f}", f"{stated_northing:.3f}"],
        ],
        bbox=BBox(x0=0, y0=0, x1=10, y1=10),
    )
    schedule_sheet = _synthetic_sheet(
        "1000001",
        raw_text=_PRECEDENCE_NOTE.format(sheet_no="1000002"),
        tables=[schedule_table],
        dxf_sheet=None,
    )

    origin_insert = DxfInsert(name="SETOUT POINT MARKER", insert=Point3D(0, 0, 0), layer="D-SETOUT")
    origin_text = DxfText(text="E 1000.000\nN 2000.000", insert=Point3D(0.2, 0.2, 0), layer="D-TEXT")
    bearing_text = DxfText(text='90° 00\' 00"', insert=Point3D(10.0, 0, 0), layer="D-TEXT")
    location_text = DxfText(text="ABUTMENT A", insert=Point3D(10.0, 0, 0), layer="D-TEXT")
    pile_text = DxfText(text="PIL001", insert=Point3D(20.0, 0, 0), layer="D-TEXT")
    dim1 = _chain_dim(Point3D(0, 0, 0), Point3D(10, 0, 0), 10.0)
    dim2 = _chain_dim(Point3D(10, 0, 0), Point3D(20, 0, 0), 10.0)
    geometry_dxf = DxfSheet(
        source_path="geom.dxf",
        units="m",
        dimensions=[dim1, dim2],
        viewports=[],
        inserts=[origin_insert],
        texts=[origin_text, bearing_text, location_text, pile_text],
    )
    geometry_sheet = _synthetic_sheet("1000002", dxf_sheet=geometry_dxf)

    return Project(source_path="synthetic", sheets=[schedule_sheet, geometry_sheet])


def test_check_setout_reconstruction_uses_cross_sheet_dxf_and_reports_geometry_sheet_no():
    # The schedule states a stated E/N far from what the geometry sheet's
    # chain derives (real E ~1010, N ~2000) — forces a "high" mismatch
    # Issue, so suggested_fix["geometry_sheet_no"] can be asserted
    # directly instead of relying on a clean-pass producing nothing.
    project = _cross_sheet_synthetic_project(stated_easting=1500.0, stated_northing=2000.0)

    issues = check_setout_reconstruction(project, RuleConfig())
    assert len(issues) == 1
    issue = issues[0]
    assert issue.severity == "high"
    assert issue.sheet_no == "1000001"  # reported against the schedule sheet
    assert issue.suggested_fix["geometry_sheet_no"] == "1000002"
    assert "sheet 1000002" in issue.description


def test_check_setout_reconstruction_cross_sheet_clean_pass_no_issue():
    # Stated value matches the geometry-derived value closely (origin
    # E1000/N2000, due-east bearing, 20m anchor-to-pile chain -> derived
    # E1020/N2000) — proves the cross-sheet path can also produce a clean
    # pass, not just mismatches.
    project = _cross_sheet_synthetic_project(stated_easting=1020.0, stated_northing=2000.0)
    assert check_setout_reconstruction(project, RuleConfig()) == []


# --- _cached_reconstruct_sheet: geometry_sheet-aware cache -----------------


def test_reconstruction_cache_distinguishes_different_geometry_sheets():
    schedule_sheet = _real_sheet_101051()
    schedule_sheet.dxf_sheet = None  # force reliance on an explicit geometry_sheet
    geometry_sheet_a = _real_sheet_101051()
    geometry_sheet_b = _real_sheet_101051()
    config = RuleConfig()

    _reconstruction_cache.clear()
    result_a1 = _cached_reconstruct_sheet(schedule_sheet, config, geometry_sheet_a)
    result_a2 = _cached_reconstruct_sheet(schedule_sheet, config, geometry_sheet_a)
    result_b = _cached_reconstruct_sheet(schedule_sheet, config, geometry_sheet_b)

    assert result_a1 is result_a2  # same geometry_sheet -> cached, not recomputed
    assert result_a1 is not result_b  # different geometry_sheet -> distinct cache entry
    assert len(_reconstruction_cache) == 2


def test_reconstruction_cache_geometry_sheet_freed_independently():
    import gc

    schedule_sheet = _real_sheet_101051()
    schedule_sheet.dxf_sheet = None
    geometry_sheet = _real_sheet_101051()

    _reconstruction_cache.clear()
    _cached_reconstruct_sheet(schedule_sheet, RuleConfig(), geometry_sheet)
    assert len(_reconstruction_cache) == 1

    del geometry_sheet
    gc.collect()
    assert len(_reconstruction_cache) == 0


# --- real BR08 cross-sheet, at the check-function level --------------------


def test_real_br08_check_setout_reconstruction_cross_sheet():
    # Full real BR08 Project: schedule sheet 2873042 (page_index 20, no
    # DXF of its own) + geometry sheet 2873041 (page_index 19). See
    # extraction/setout_reconstruction.py's docstring's 2026-08-14
    # section for the real result this mirrors at the reconstruct_sheet
    # level: ABUTMENT A/B's 28 piles reconstruct cleanly (no Issues);
    # ABUTMENT A1/B1/B2's 15 piles have large, real, honestly-reported
    # mismatches (high-severity Issues), a known limitation, not a bug in
    # this cross-sheet wiring — both carry suggested_fix["geometry_sheet_
    # no"] == "2873041" either way (the coverage-gap "unmatched"/"no_*"
    # statuses do too), proving the resolution is real, not incidental.
    with pdfplumber.open(BR08_SAMPLE_PDF) as pdf:
        schedule_tables = extract_tables(pdf.pages[20])
    with fitz.open(BR08_SAMPLE_PDF) as doc:
        schedule_raw_text = doc[20].get_text("text")

    schedule_sheet = _synthetic_sheet(
        "2873042", raw_text=schedule_raw_text, tables=schedule_tables, dxf_sheet=None
    )
    schedule_sheet.page_index = 20
    geometry_sheet = _synthetic_sheet("2873041", dxf_sheet=ingest_dxf(BR08_GEOMETRY_DXF))
    geometry_sheet.page_index = 19

    project = Project(source_path="synthetic", sheets=[schedule_sheet, geometry_sheet])
    issues = check_setout_reconstruction(project, RuleConfig())

    assert issues  # ABUTMENT A1/B1/B2's real mismatches must surface
    for issue in issues:
        assert issue.suggested_fix["geometry_sheet_no"] == "2873041"
        assert issue.sheet_no == "2873042"
    assert all(issue.severity == "high" for issue in issues)
    # 15 real mismatched piles (ABUTMENT A1/B1/B2), none from the clean
    # 28-pile A/B groups.
    assert len(issues) == 15
