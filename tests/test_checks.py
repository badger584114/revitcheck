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
from pdfchecker.checks.en_gb_variants import AMERICAN_TO_BRITISH, BRITISH_TO_AMERICAN  # noqa: E402
from pdfchecker.checks.revisions import (  # noqa: E402
    check_schedule_matches_title_block,
    check_sequential_numbering,
)
from pdfchecker.checks.spelling import check_spelling  # noqa: E402
from pdfchecker.checks.title_block import check_required_fields  # noqa: E402
from pdfchecker.ir import (  # noqa: E402
    BBox,
    Project,
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
