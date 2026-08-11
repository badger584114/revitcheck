"""Geometry check engine (checks/geometry.py) — PLANNING.md §5a's
drawn-vs-stated dimensional consistency, the first Stage 3 check rule.
Same split as every other check module here: synthetic `DimensionEntity`/
`DxfSheet` objects for the tolerance-logic branches, plus a real-sample
assertion (`samples/dxf/101051`, attached via the real `attach_dxf_sheets`
join) for the "correctly finds nothing wrong on a clean sheet" path —
that sheet's own real override (150mm stated vs. 149.333mm drawn) is
well inside tolerance, so this doubles as a sanity check on the whole
pipeline end to end.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

import pdfplumber  # noqa: E402

from pdfchecker.checks.catalog import RuleConfig  # noqa: E402
from pdfchecker.checks.geometry import (  # noqa: E402
    _parse_stated_mm,
    check_dimension_consistency,
    check_setout_reconstruction,
)
from pdfchecker.extraction.dxf_source import attach_dxf_sheets, ingest_dxf  # noqa: E402
from pdfchecker.extraction.tables import extract_tables  # noqa: E402
from pdfchecker.ir import (  # noqa: E402
    DimensionEntity,
    DxfSheet,
    Point3D,
    Project,
    Sheet,
    TitleBlock,
)

SAMPLE_PDF = str(Path(__file__).resolve().parent.parent / "samples" / "T2DPAA-T2D-C3S-BR-DRG-101000.pdf")

SAMPLE_DXF = str(
    Path(__file__).resolve().parent.parent / "samples" / "dxf" / "T2DPAA-T2D-C3S-BR-DRG-101051_0.dxf"
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
    assert issues[0].bbox is None  # no DXF->PDF transform yet — documented, not an oversight


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
