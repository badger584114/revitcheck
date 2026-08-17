"""pdfchecker/session.py — the end-to-end session orchestrator, plus the
rule-registration regression guard for the 2026-08-17 backend review's
finding 1.3.

Deliberately built on a tiny synthetic PDF rather than the real 37-page
sample: `run_session` does its own ingestion internally, so it can't
reuse conftest's session-scoped `project` fixture, and a second real
ingest would add ~125s to a suite that already runs 12m50s. What's under
test here is orchestration — stage sequencing, scope handling, coverage
warnings, scratch lifecycle — none of which needs real drawing content.
The real DXF files are used where the assertion genuinely depends on
them (the numeric-suffix join failing to match).
"""

from __future__ import annotations

import sys
from pathlib import Path

import fitz
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks import RuleConfig, all_rule_ids  # noqa: E402
from pdfchecker.checks.session_config import load_session_config  # noqa: E402
from pdfchecker.session import SessionWorkspace, _apply_scope, run_session  # noqa: E402

SAMPLES = Path(__file__).resolve().parent.parent / "samples"


@pytest.fixture(scope="module")
def tiny_pdf(tmp_path_factory) -> str:
    """A two-page PDF with a little real text — enough for ingestion and
    every drafting rule to run over, fast enough to build per module."""

    path = tmp_path_factory.mktemp("session") / "tiny.pdf"
    doc = fitz.open()
    for n in range(2):
        page = doc.new_page(width=841, height=595)
        page.insert_text((72, 72), f"GENERAL ARRANGEMENT SHEET {n + 1}")
        page.insert_text((72, 96), "CONCRETE COLOUR AND CENTRE LINE")
    doc.save(str(path))
    doc.close()
    return str(path)


# --- finding 1.3: geometry rules must register without an explicit import ---


class TestRuleRegistration:
    """The bug: `checks/__init__.py` imported four rule modules for their
    @register side-effects and omitted `geometry`, so `drafting_and_
    geometry` silently ran zero geometry rules. These assertions
    deliberately rely only on `pdfchecker.checks` — importing
    `pdfchecker.checks.geometry` here would re-create the very workaround
    that hid the bug (as tests/test_session_config.py had to do)."""

    def test_geometry_rules_are_in_the_catalog(self):
        assert [r for r in all_rule_ids() if r.startswith("geometry.")]

    def test_default_config_enables_geometry_rules(self):
        assert [r for r in RuleConfig().resolved_rule_ids() if r.startswith("geometry.")]

    def test_default_is_not_snapshotted_at_construction_time(self):
        """The second-order trap: `enabled_rule_ids` used to default to
        `set(all_rule_ids())` evaluated in the field default, so a config
        built before a rule module was imported excluded it forever.
        A `None` default resolved at run time is what fixes that."""

        config = RuleConfig()
        assert config.enabled_rule_ids is None
        assert config.resolved_rule_ids() == set(all_rule_ids())

    def test_explicit_selection_still_wins(self):
        config = RuleConfig(enabled_rule_ids={"spelling.en_gb"})
        assert config.resolved_rule_ids() == {"spelling.en_gb"}
        assert config.is_enabled("spelling.en_gb")
        assert not config.is_enabled("geometry.dimension_consistency")

    def test_resolved_ids_drop_rules_with_no_implementation(self):
        """session_config.py accepts a rule id the catalog doesn't
        implement (§6's spec-derived ids arrive that way). `resolved_rule_
        ids()` reports what will really run, not what was asked for."""

        config = RuleConfig(enabled_rule_ids={"spelling.en_gb", "regex_text_check"})
        assert config.resolved_rule_ids() == {"spelling.en_gb"}

    def test_session_config_geometry_scope_yields_geometry_rules(self, tmp_path):
        """The end-to-end form of the bug, via the real loader and the
        project's own example config — this is what returned `[]`."""

        loaded = load_session_config(SAMPLES.parent / "config" / "session_example.yaml")
        assert loaded.check_scope == "drafting_and_geometry"
        assert [r for r in loaded.rule_config.enabled_rule_ids if r.startswith("geometry.")]
        assert not any("no geometry.* rules are registered" in w for w in loaded.warnings)


# --- scope handling ---


class TestScope:
    def test_drafting_only_strips_geometry(self):
        scoped = _apply_scope(RuleConfig(), "drafting_only")
        assert not [r for r in scoped.resolved_rule_ids() if r.startswith("geometry.")]
        assert "spelling.en_gb" in scoped.resolved_rule_ids()

    def test_geometry_scope_keeps_everything(self):
        scoped = _apply_scope(RuleConfig(), "drafting_and_geometry")
        assert [r for r in scoped.resolved_rule_ids() if r.startswith("geometry.")]

    def test_does_not_mutate_the_callers_config(self):
        config = RuleConfig()
        _apply_scope(config, "drafting_only")
        assert config.enabled_rule_ids is None
        assert [r for r in config.resolved_rule_ids() if r.startswith("geometry.")]

    def test_tolerances_survive_scoping(self):
        config = RuleConfig(survey_tolerance_mm=42.0, setout_critical_layers=["C-BEARING"])
        scoped = _apply_scope(config, "drafting_only")
        assert scoped.survey_tolerance_mm == 42.0
        assert scoped.setout_critical_layers == ["C-BEARING"]

    def test_invalid_scope_raises(self, tiny_pdf):
        with pytest.raises(ValueError, match="check_scope"):
            run_session(tiny_pdf, check_scope="geometry_only")


# --- scratch lifecycle (§2's stateless-by-design constraint) ---


class TestWorkspace:
    def test_creates_and_purges(self, tmp_path):
        ws = SessionWorkspace(str(tmp_path))
        created = Path(ws.dir)
        (created / "scratch.txt").write_text("session data")
        assert created.exists()

        ws.purge()
        assert not created.exists()
        assert ws.dir is None

    def test_purge_is_idempotent(self, tmp_path):
        ws = SessionWorkspace(str(tmp_path))
        ws.purge()
        ws.purge()

    def test_purge_leaves_the_parent_directory_alone(self, tmp_path):
        """The parent is where the scratch dir gets made, never something
        purge() may delete — it can be a caller-supplied location."""

        sibling = tmp_path / "not-ours.txt"
        sibling.write_text("caller's file")
        ws = SessionWorkspace(str(tmp_path))
        ws.purge()
        assert sibling.exists()
        assert tmp_path.exists()

    def test_subdir_after_purge_raises(self, tmp_path):
        ws = SessionWorkspace(str(tmp_path))
        ws.purge()
        with pytest.raises(RuntimeError, match="purged"):
            ws.subdir("dwg_in")


# --- orchestration ---


class TestRunSession:
    def test_drafting_only_end_to_end(self, tiny_pdf):
        with run_session(tiny_pdf, check_scope="drafting_only") as result:
            assert len(result.project.sheets) == 2
            assert result.check_scope == "drafting_only"
            assert [s.name for s in result.stages] == ["ingest_pdf", "run_checks"]
            assert not [r for r in result.rules_run if r.startswith("geometry.")]
            assert result.seconds > 0

    def test_progress_reports_every_planned_stage(self, tiny_pdf):
        seen = []
        with run_session(tiny_pdf, check_scope="drafting_only", progress=lambda *a: seen.append(a)):
            pass
        assert [name for name, _, _ in seen] == ["ingest_pdf", "run_checks"]
        # the denominator a progress bar needs, and a 1-based index
        assert {total for _, _, total in seen} == {2}
        assert [idx for _, idx, _ in seen] == [1, 2]

    def test_geometry_scope_without_inputs_warns_rather_than_looking_clean(self, tiny_pdf):
        """The failure mode this warning exists for: geometry rules run
        against nothing and report zero issues, which reads identically
        to a clean geometry result."""

        with run_session(tiny_pdf, check_scope="drafting_and_geometry") as result:
            assert [r for r in result.rules_run if r.startswith("geometry.")]
            assert any("no DWG/DXF/IFC input was supplied" in w for w in result.warnings)

    def test_geometry_inputs_on_a_drafting_only_run_warn(self, tiny_pdf):
        dxf = sorted((SAMPLES / "BR06" / "dxf").glob("*.dxf"))
        with run_session(tiny_pdf, dxf_paths=[str(dxf[0])], check_scope="drafting_only") as result:
            assert any("were not ingested" in w for w in result.warnings)
            # ...and genuinely weren't: no sheet picked up DXF geometry
            assert all(s.dxf_sheet is None for s in result.project.sheets)
            assert "ingest_dxf" not in [s.name for s in result.stages]

    def test_unmatched_dxf_is_reported_not_silent(self, tiny_pdf):
        """A real DXF that joins to no sheet is the quiet-failure case:
        every geometry rule skips a sheet with no `dxf_sheet`, so a total
        join failure produces the same empty result as a clean set."""

        dxf = sorted((SAMPLES / "BR06" / "dxf").glob("*.dxf"))
        assert dxf, "expected committed sample DXF files"
        with run_session(tiny_pdf, dxf_paths=[str(dxf[0])], check_scope="drafting_and_geometry") as result:
            assert "ingest_dxf" in [s.name for s in result.stages]
            assert any("matched a sheet in the PDF" in w for w in result.warnings)

    def test_result_purges_scratch_on_context_exit(self, tiny_pdf, tmp_path):
        dxf = sorted((SAMPLES / "BR06" / "dxf").glob("*.dxf"))
        with run_session(
            tiny_pdf,
            dxf_paths=[str(dxf[0])],
            check_scope="drafting_and_geometry",
            scratch_parent_dir=str(tmp_path),
        ) as result:
            scratch = Path(result.workspace.dir)
            assert scratch.exists()
        assert not scratch.exists()

    def test_to_dict_carries_the_run_not_the_ir(self, tiny_pdf):
        with run_session(tiny_pdf, check_scope="drafting_only") as result:
            d = result.to_dict()
        assert d["sheet_count"] == 2
        assert d["check_scope"] == "drafting_only"
        assert d["issue_count"] == len(d["issues"])
        assert [s["name"] for s in d["stages"]] == ["ingest_pdf", "run_checks"]
        # §2: the extracted IR must not ride along in an API response
        assert "sheets" not in d and "project" not in d

    def test_supplied_config_is_used(self, tiny_pdf):
        config = RuleConfig(enabled_rule_ids={"spelling.en_gb"})
        with run_session(tiny_pdf, config=config, check_scope="drafting_only") as result:
            assert result.rules_run == ["spelling.en_gb"]
            assert all(i.rule_id == "spelling.en_gb" for i in result.issues)
