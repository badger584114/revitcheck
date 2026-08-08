"""Stage 1 ingestion tests, run against the real sample per CLAUDE.md's
rule: samples/ are the first real test fixtures, not synthetic ones.

Ingestion of the full 37-page sample takes a couple of minutes (mostly
pdfplumber's find_tables() across pages with dozens of tables) — the
`project` fixture below is module-scoped specifically so the 7 tests here
share one ingestion run instead of re-parsing the PDF from scratch 7 times.
"""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.extraction.pipeline import ingest_pdf  # noqa: E402

SAMPLE = str(
    Path(__file__).resolve().parent.parent
    / "samples"
    / "T2DPAA-T2D-C3S-BR-DRG-101000.pdf"
)


@pytest.fixture(scope="module")
def project():
    return ingest_pdf(SAMPLE)


def test_ingests_all_sheets(project):
    assert len(project.sheets) == 37


def test_sheet_no_extracted_for_every_sheet(project):
    missing = [s.page_index for s in project.sheets if not s.sheet_no]
    assert not missing, f"pages missing sheet_no: {missing}"


def test_drawing_no_consistent_across_sheet_set(project):
    # All sheets in this set belong to the same drawing (No. 8011) — a
    # project-wide consistency fact worth asserting explicitly, since a
    # divergent value would mean title-block extraction picked up the
    # wrong text on some page.
    drawing_nos = {s.drawing_no for s in project.sheets}
    assert drawing_nos == {"8011"}


def test_lat_long_extracted_and_consistent(project):
    # Every sheet should carry the same site lat/long (PLANNING.md §4's
    # "sheet-to-sheet consistency" primitive, the lat/long example itself).
    lat_longs = {
        (s.title_block.get("sheet_latitude"), s.title_block.get("sheet_longitude"))
        for s in project.sheets
    }
    assert lat_longs == {("-34.94188", "138.57397")}


def test_foundation_layout_sheet_title_block(project):
    # Page 15 (Sheet No. 2871051, "FOUNDATION LAYOUT") — spot-checked
    # visually against the rendered page before writing extraction code.
    sheet = project.sheet_by_no("2871051")
    assert sheet is not None
    assert sheet.title_block.get("amend_no") == "0"
    assert sheet.title_block.get("drafted_by") == "T2DA"


def test_revision_schedule_parsed(project):
    sheet = project.sheet_by_no("2871051")
    assert sheet.revision_schedule, "expected at least the initial 'Issued for Construction' revision"
    rev = sheet.revision_schedule[-1]
    assert rev.rev_id == "0"
    assert "ISSUED FOR CONSTRUCTION" in rev.description.upper()
    assert rev.date == "28.03.26"


def test_pile_schedule_table_extracted(project):
    # Page 15's Abutment B pile schedule — a real setout/coordinate table
    # (PLANNING.md §3), confirms generic table extraction + the
    # bogus-full-page-table filter both work on a real sheet.
    sheet = project.sheet_by_no("2871051")
    schedules = [t for t in sheet.tables if t.kind == "schedule"]
    assert schedules, "expected at least one EASTING/NORTHING-bearing schedule table"
    all_text = " ".join(str(cell) for t in schedules for row in t.rows for cell in row if cell)
    assert "PIL234321" in all_text  # a real pile ID visible on this sheet
