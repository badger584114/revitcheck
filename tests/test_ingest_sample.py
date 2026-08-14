"""Stage 1 ingestion tests, run against the real sample per CLAUDE.md's
rule: samples/ are the first real test fixtures, not synthetic ones.

The `project` fixture is defined in conftest.py (session-scoped) so this
file and test_checks.py share one ingestion run.
"""


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


def test_references_built(project):
    # PLANNING.md §3/§4's cross-sheet reference graph — a whole-project
    # pass, so this exercises it end-to-end against the real 37-page set
    # rather than a couple of synthetic sheets.
    assert len(project.references) > 50, "expected the real sample's many section/detail callouts to be found"


def test_known_section_reference_resolves(project):
    # Spot-checked visually against the rendered pages before writing
    # extraction code: sheet 2871023's plan view callout "3" points to
    # "HEADSTOCK SECTION 3", actually drawn on sheet 2871024.
    matches = [
        r for r in project.references
        if r.source_sheet_no == "2871023" and r.tag == "3" and r.resolved
    ]
    assert matches, "expected callout tag '3' on sheet 2871023 to resolve"
    assert matches[0].target_sheet_no == "2871024"
    assert matches[0].ref_type == "section"


def test_no_symbol_reference_targets_a_nonexistent_sheet(project):
    # Every symbol-based callout (section/detail marker) in this set
    # names a sheet number that's genuinely somewhere in the pack — a
    # reference to a sheet outside the set (confidence 0.0,
    # extraction/references.py) would mean a genuinely broken
    # cross-reference, which this clean set shouldn't have. Scoped to
    # non-"note" references — see the real general-note typo confirmed
    # in test_note_reference_catches_a_real_sheet_number_typo below,
    # which is a genuine finding, not something this test should paper
    # over by widening its own bound.
    dead = [
        r for r in project.references if not r.resolved and r.confidence == 0.0 and r.ref_type != "note"
    ]
    assert dead == []


def test_note_reference_catches_a_real_sheet_number_typo(project):
    # A real drafting typo, confirmed 2026-08-14: sheet 2871122's note
    # "REFER SHEET No. 287114 FOR HOLD DOWN JOINTS..." cites "287114" (6
    # digits) where this set's real sheet numbering is 7 digits —
    # almost certainly "2871114" (a real sheet in this set) with a digit
    # dropped. Exactly the kind of real error general-note-reference
    # resolution exists to catch, not an extraction artifact — every
    # other "REFER TO SHEET No." note on this sample (89 total) resolves
    # cleanly, so this one genuine gap stands out rather than being lost
    # in a sea of false positives.
    typo = [
        r for r in project.references if r.ref_type == "note" and r.target_sheet_hint == "287114"
    ]
    assert len(typo) == 1
    assert typo[0].source_sheet_no == "2871122"
    assert typo[0].resolved is False
    assert typo[0].confidence == 0.0


def test_pile_schedule_table_extracted(project):
    # Page 15's Abutment B pile schedule — a real setout/coordinate table
    # (PLANNING.md §3), confirms generic table extraction + the
    # bogus-full-page-table filter both work on a real sheet.
    sheet = project.sheet_by_no("2871051")
    schedules = [t for t in sheet.tables if t.kind == "schedule"]
    assert schedules, "expected at least one EASTING/NORTHING-bearing schedule table"
    all_text = " ".join(str(cell) for t in schedules for row in t.rows for cell in row if cell)
    assert "PIL234321" in all_text  # a real pile ID visible on this sheet
