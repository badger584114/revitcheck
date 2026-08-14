"""Stage 2 drafting-check tests (PLANNING.md §9 step 2): title block
completeness, revision consistency, spelling. Two kinds of test here:

- Against the real sample (`project` fixture, conftest.py) — this is an
  "Issued For Construction" set with no actual title-block/revision
  defects, so it exercises the "correctly finds nothing wrong" path, not
  the "fires" path. Real defects would need a real bad sample.
- Synthetic minimal IR objects, built by hand, specifically to exercise
  the "fires when it should" path each rule needs covered — fast (no PDF
  parsing) and precise about what triggers each rule.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks import RuleConfig  # noqa: E402
from pdfchecker.checks.cross_sheet import check_reference_resolves  # noqa: E402
from pdfchecker.checks.en_gb_variants import AMERICAN_TO_BRITISH, BRITISH_TO_AMERICAN  # noqa: E402
from pdfchecker.checks.revisions import (  # noqa: E402
    check_cloud_matches_schedule,
    check_schedule_matches_title_block,
    check_sequential_numbering,
)
from pdfchecker.checks.spelling import check_spelling  # noqa: E402
from pdfchecker.checks.title_block import check_required_fields  # noqa: E402
from pdfchecker.ir import (  # noqa: E402
    BBox,
    Project,
    Reference,
    RevisionCloud,
    RevisionEntry,
    Sheet,
    TextWord,
    TitleBlock,
)

def _sheet(**overrides) -> Sheet:
    defaults = dict(
        page_index=0,
        page_width=2384.0,
        page_height=1684.0,
        title_block=TitleBlock(fields={"drawing_no": "8011", "sheet_no": "2871099", "amend_no": "0"}),
        revision_schedule=[],
        tables=[],
        words=[],
        paths=[],
        raw_text="",
    )
    defaults.update(overrides)
    return Sheet(**defaults)


# --- against the real sample -------------------------------------------


def test_no_title_block_issues_on_real_clean_set(real_issues):
    title_block_issues = [i for i in real_issues if i.category == "title_block"]
    assert title_block_issues == [], "this Issued-For-Construction set has complete title blocks on every sheet"


def test_no_revision_mismatches_on_real_clean_set(real_issues):
    revision_issues = [i for i in real_issues if i.category == "revision"]
    assert revision_issues == [], "every sheet's AMEND No. already matches its revision schedule in this set"


def test_cross_sheet_issues_on_real_set_are_lower_confidence_only(real_issues):
    # This set's unresolved symbol-based reference candidates all name a
    # sheet that genuinely exists in the pack (extraction/references.py's
    # confidence 0.3 case) — none reference a sheet missing from the set
    # entirely, so none should surface at "high" severity. One real "high"
    # exception, confirmed 2026-08-14 and asserted separately below: a
    # genuine drafting typo a general-note reference (not a symbol marker)
    # catches, which correctly *should* be "high" (confidence 0.0) — this
    # test's own bound is scoped past it rather than widened to hide it.
    cross_sheet_issues = [
        i
        for i in real_issues
        if i.category == "cross_sheet" and "287114" not in i.description
    ]
    assert all(i.severity == "medium" for i in cross_sheet_issues)


def test_cross_sheet_catches_the_real_sheet_number_typo(real_issues):
    # The real drafting typo confirmed in
    # test_note_reference_catches_a_real_sheet_number_typo
    # (tests/test_ingest_sample.py) — sheet 2871122's note cites "287114"
    # (6 digits) where this set's sheet numbers are all 7, almost
    # certainly "2871114" with a digit dropped. This is the check-engine
    # layer's own confirmation that the Issue actually surfaces, not just
    # the underlying Reference.
    matches = [
        i for i in real_issues if i.category == "cross_sheet" and "287114" in i.description
    ]
    assert len(matches) == 1
    assert matches[0].severity == "high"
    assert matches[0].sheet_no == "2871122"


def test_millimetres_not_flagged(real_issues):
    # The real bug this surfaced: every sheet's "ALL DIMENSIONS ARE IN
    # MILLIMETRES" note was flagged with "millimeters" suggested, because
    # the plural form wasn't in the British/American variant map.
    flagged_words = {i.description for i in real_issues if i.category == "spelling"}
    assert not any("MILLIMETRES" in d for d in flagged_words)


def test_glossary_terms_not_flagged(real_issues):
    flagged_words = {i.description.upper() for i in real_issues if i.category == "spelling"}
    for term in ["ABUTMENT", "PRECAMBER", "UPSTAND"]:
        assert not any(term in d for d in flagged_words), f"'{term}' should be suppressed by the firm glossary"


# --- synthetic: title block completeness --------------------------------


def test_missing_required_field_flagged():
    sheet = _sheet(title_block=TitleBlock(fields={"drawing_no": "8011", "sheet_no": "2871099"}))
    project = Project(source_path="synthetic", sheets=[sheet])
    config = RuleConfig(required_title_block_fields=["drawing_no", "sheet_no", "amend_no"])
    issues = check_required_fields(project, config)
    assert len(issues) == 1
    assert "amend_no" in issues[0].description
    assert issues[0].severity == "high"


def test_all_required_fields_present_no_issue():
    sheet = _sheet()
    project = Project(source_path="synthetic", sheets=[sheet])
    config = RuleConfig(required_title_block_fields=["drawing_no", "sheet_no", "amend_no"])
    assert check_required_fields(project, config) == []


# --- synthetic: revision consistency -------------------------------------


def test_schedule_title_block_mismatch_flagged():
    sheet = _sheet(
        title_block=TitleBlock(fields={"drawing_no": "8011", "sheet_no": "2871099", "amend_no": "2"}),
        revision_schedule=[RevisionEntry(rev_id="1", description="ISSUED FOR TENDER")],
    )
    project = Project(source_path="synthetic", sheets=[sheet])
    issues = check_schedule_matches_title_block(project, RuleConfig())
    assert len(issues) == 1
    assert "AMEND No. is '2'" in issues[0].description
    assert "'1'" in issues[0].description


def test_schedule_title_block_match_no_issue():
    sheet = _sheet(
        title_block=TitleBlock(fields={"drawing_no": "8011", "sheet_no": "2871099", "amend_no": "1"}),
        revision_schedule=[RevisionEntry(rev_id="1", description="ISSUED FOR TENDER")],
    )
    project = Project(source_path="synthetic", sheets=[sheet])
    assert check_schedule_matches_title_block(project, RuleConfig()) == []


def test_duplicate_revision_number_flagged():
    sheet = _sheet(
        revision_schedule=[
            RevisionEntry(rev_id="0", description="ISSUED FOR TENDER"),
            RevisionEntry(rev_id="0", description="RE-ISSUED FOR TENDER"),
        ]
    )
    project = Project(source_path="synthetic", sheets=[sheet])
    issues = check_sequential_numbering(project, RuleConfig())
    assert len(issues) == 1
    assert "duplicate" in issues[0].description.lower()


def test_missing_revision_number_flagged():
    sheet = _sheet(
        revision_schedule=[
            RevisionEntry(rev_id="0", description="ISSUED FOR TENDER"),
            RevisionEntry(rev_id="2", description="ISSUED FOR CONSTRUCTION"),
        ]
    )
    project = Project(source_path="synthetic", sheets=[sheet])
    issues = check_sequential_numbering(project, RuleConfig())
    assert len(issues) == 1
    assert "[1]" in issues[0].description


# --- synthetic: revision cloud -> schedule row ---------------------------
# (extraction/revision_clouds.py's clustering/tag-resolution algorithm
# itself is covered by tests/test_revision_clouds.py; these exercise only
# the rule that consumes an already-built RevisionCloud list.)


def test_cloud_tag_matches_schedule_no_issue():
    sheet = _sheet(
        revision_schedule=[RevisionEntry(rev_id="1", description="ISSUED FOR CONSTRUCTION")],
        revision_clouds=[RevisionCloud(bbox=BBox(0, 0, 10, 10), tag="1", arc_count=8)],
    )
    project = Project(source_path="synthetic", sheets=[sheet])
    assert check_cloud_matches_schedule(project, RuleConfig()) == []


def test_cloud_tag_with_no_schedule_row_flagged_high():
    sheet = _sheet(
        revision_schedule=[RevisionEntry(rev_id="1", description="ISSUED FOR CONSTRUCTION")],
        revision_clouds=[RevisionCloud(bbox=BBox(0, 0, 10, 10), tag="2", arc_count=8)],
    )
    project = Project(source_path="synthetic", sheets=[sheet])
    issues = check_cloud_matches_schedule(project, RuleConfig())
    assert len(issues) == 1
    assert issues[0].severity == "high"
    assert "'2'" in issues[0].description


def test_cloud_with_no_resolvable_tag_flagged_low():
    sheet = _sheet(
        revision_schedule=[RevisionEntry(rev_id="1", description="ISSUED FOR CONSTRUCTION")],
        revision_clouds=[RevisionCloud(bbox=BBox(0, 0, 10, 10), tag=None, arc_count=8)],
    )
    project = Project(source_path="synthetic", sheets=[sheet])
    issues = check_cloud_matches_schedule(project, RuleConfig())
    assert len(issues) == 1
    assert issues[0].severity == "low"


def test_no_clouds_on_amended_real_sample(amended_project):
    # samples/T2DPAA-T2D-C3S-BR-DRG-101000_1.pdf's amended sheets are
    # correctly drafted — every cloud tags the sheet's own AMEND No. 1,
    # which is already a schedule row — so this exercises the "correctly
    # finds nothing" path against real cloud geometry, not just synthetic
    # sheets.
    issues = check_cloud_matches_schedule(amended_project, RuleConfig())
    assert issues == []


# --- synthetic: cross-sheet reference resolution -------------------------
# (extraction/references.py's resolution algorithm itself is covered by
# tests/test_references.py; these exercise only the thin rule wrapper —
# does an unresolved Reference become the right Issue.)


def test_unresolved_reference_to_missing_sheet_flagged_high():
    sheet = _sheet()
    ref = Reference(
        ref_type="detail",
        tag="4",
        source_sheet_no="2871099",
        source_page_index=0,
        source_bbox=BBox(0, 0, 10, 10),
        target_sheet_hint="2871404",
        resolved=False,
        confidence=0.0,
    )
    project = Project(source_path="synthetic", sheets=[sheet], references=[ref])
    issues = check_reference_resolves(project, RuleConfig())
    assert len(issues) == 1
    assert issues[0].severity == "high"
    assert issues[0].suggested_fix == {"ref": "Detail 4/2871404"}


def test_unresolved_reference_to_existing_sheet_flagged_medium():
    sheet = _sheet()
    ref = Reference(
        ref_type="unknown",
        tag="5",
        source_sheet_no="2871099",
        source_page_index=0,
        source_bbox=BBox(0, 0, 10, 10),
        target_sheet_hint="2871100",
        resolved=False,
        confidence=0.3,
    )
    project = Project(source_path="synthetic", sheets=[sheet], references=[ref])
    issues = check_reference_resolves(project, RuleConfig())
    assert len(issues) == 1
    assert issues[0].severity == "medium"


def test_resolved_reference_not_flagged():
    sheet = _sheet()
    ref = Reference(
        ref_type="section",
        tag="1",
        source_sheet_no="2871099",
        source_page_index=0,
        source_bbox=BBox(0, 0, 10, 10),
        target_sheet_hint="2871100",
        resolved=True,
        target_sheet_no="2871100",
        confidence=1.0,
    )
    project = Project(source_path="synthetic", sheets=[sheet], references=[ref])
    assert check_reference_resolves(project, RuleConfig()) == []


# --- synthetic: spelling ---------------------------------------------------


def _word_sheet(text: str) -> Sheet:
    return _sheet(words=[TextWord(text=text, bbox=BBox(0, 0, 10, 10))])


def test_american_spelling_flagged_with_british_suggestion():
    project = Project(source_path="synthetic", sheets=[_word_sheet("SPECIALIZED")])
    issues = check_spelling(project, RuleConfig())
    assert len(issues) == 1
    assert issues[0].suggested_fix == {"corrected": "specialised"}
    assert "American spelling" in issues[0].description


def test_british_spelling_not_flagged():
    project = Project(source_path="synthetic", sheets=[_word_sheet("SPECIALISED")])
    assert check_spelling(project, RuleConfig()) == []


def test_genuine_typo_flagged():
    project = Project(source_path="synthetic", sheets=[_word_sheet("CONCRETEE")])
    issues = check_spelling(project, RuleConfig())
    assert len(issues) == 1
    assert "misspelling" in issues[0].description.lower()


def test_short_tokens_and_ids_not_checked():
    # Below the length heuristic, or containing digits — deliberately not
    # spellchecked (see spelling.py's documented heuristic).
    project = Project(source_path="synthetic", sheets=[_word_sheet("RL"), _word_sheet("PIL234301")])
    assert check_spelling(project, RuleConfig()) == []


def test_en_gb_variant_map_has_no_self_mappings():
    # Every entry should actually transform, not silently map a word to
    # itself (the millimetre/millimetres plural-suffix bug this caught).
    self_mapped = [w for w, mapped in BRITISH_TO_AMERICAN.items() if w == mapped]
    assert self_mapped == []
    assert AMERICAN_TO_BRITISH["millimeters"] == "millimetres"
