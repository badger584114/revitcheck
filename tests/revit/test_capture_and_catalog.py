"""Capture round-tripping, rule registration, and rule isolation."""

import json

import pytest

import revitcheck.checks  # noqa: F401 - the import under test, see TestRuleRegistration
from revitcheck import Issue, RuleConfig, all_rule_ids, run_checks, sort_issues
from revitcheck import capture
from revitcheck.catalog import register
from revitcheck.ir import Provenance


class TestRuleRegistration:
    """Guards the failure that cost the PDF side a whole check scope.

    `checks/__init__.py` omitted a rule module, so a scoped run executed
    zero rules and looked identical to a clean result. These tests
    import only `revitcheck.checks` — never a rule module directly —
    because importing the module under test would paper over exactly
    the bug being guarded.
    """

    def test_expected_rules_are_registered(self):
        assert "revit.dimension_provenance" in all_rule_ids()
        assert "revit.dimension_override_consistency" in all_rule_ids()
        assert "revit.capture_coverage" in all_rule_ids()

    def test_default_config_enables_rules_registered_after_it_was_built(self):
        # The default must resolve at run time, not snapshot the catalog
        # at construction time — otherwise import order silently decides
        # which rules run.
        config = RuleConfig()

        @register("revit.test_registered_late")
        def _late(model, cfg):
            return []

        assert "revit.test_registered_late" in config.resolved_rule_ids()

    def test_unknown_rule_id_in_config_is_not_an_error(self, make):
        config = RuleConfig(enabled_rule_ids={"revit.not_built_yet"})
        assert run_checks(make.model(), config) == []


class TestRuleIsolation:
    def test_a_raising_rule_becomes_an_issue_not_a_crash(self, make):
        @register("revit.test_explodes")
        def _explodes(model, cfg):
            raise RuntimeError("boom")

        config = RuleConfig(enabled_rule_ids={"revit.test_explodes"})
        issues = run_checks(make.model(), config)

        assert len(issues) == 1
        assert issues[0].category == "coverage"
        assert issues[0].severity == "high"
        assert "boom" in issues[0].description

    def test_one_bad_rule_does_not_suppress_a_good_one(self, make):
        @register("revit.test_explodes_too")
        def _explodes(model, cfg):
            raise RuntimeError("boom")

        @register("revit.test_finds_something")
        def _finds(model, cfg):
            return [Issue(rule_id="revit.test_finds_something", category="x", description="found")]

        config = RuleConfig(
            enabled_rule_ids={"revit.test_explodes_too", "revit.test_finds_something"}
        )
        issues = run_checks(make.model(), config)

        assert len(issues) == 2
        assert "found" in [i.description for i in issues]
        assert any("boom" in i.description for i in issues)


class TestCaptureCoverage:
    def test_extraction_errors_surface_as_an_issue(self, make):
        model = make.model(errors=["dimension 12: nope", "view 3: nope"])
        issues = run_checks(model, RuleConfig(enabled_rule_ids={"revit.capture_coverage"}))
        assert len(issues) == 1
        assert "2 element(s)" in issues[0].description

    def test_clean_capture_reports_nothing(self, make):
        issues = run_checks(
            make.model(), RuleConfig(enabled_rule_ids={"revit.capture_coverage"})
        )
        assert issues == []

    def test_long_error_lists_are_truncated(self, make):
        model = make.model(errors=["err {0}".format(i) for i in range(20)])
        issues = run_checks(model, RuleConfig(enabled_rule_ids={"revit.capture_coverage"}))
        assert "+15 more" in issues[0].description


class TestCaptureRoundTrip:
    def test_model_survives_json(self, make):
        views = [make.view(10, name="SECTION A-A")]
        dims = [
            make.dimension(1, 10, [make.model_ref(), make.drafted_ref()], value_mm=1234.5),
            make.dimension(2, 10, [make.datum_ref()], override="500 MIN."),
        ]
        original = make.model(views=views, dimensions=dims, errors=["one problem"])

        restored = capture.loads(capture.dumps(original))

        assert restored.doc_title == original.doc_title
        assert restored.extraction_errors == ["one problem"]
        assert [v.name for v in restored.views] == ["SECTION A-A"]
        assert restored.dimensions[0].segments[0].value_mm == 1234.5
        assert restored.dimensions[1].segments[0].value_override == "500 MIN."
        assert restored.dimensions[0].references[1].view_specific is True

    def test_checks_give_the_same_answer_before_and_after_a_round_trip(self, make):
        # The whole workflow rests on this: a capture taken at work must
        # produce the same findings when replayed on a laptop.
        views = [make.view(10), make.view(11, name="DETAIL", sheet_no="S102")]
        dims = [
            make.dimension(1, 10, [make.drafted_ref(), make.drafted_ref(201)]),
            make.dimension(2, 10, [make.model_ref()]),
            make.dimension(3, 11, [make.model_ref(), make.drafted_ref()]),
        ]
        original = make.model(views=views, dimensions=dims)
        config = RuleConfig(enabled_rule_ids={"revit.dimension_provenance"})

        before = run_checks(original, config)
        after = run_checks(capture.loads(capture.dumps(original)), config)

        assert [i.issue_id for i in before] == [i.issue_id for i in after]

    def test_schema_version_is_written(self, make):
        payload = json.loads(capture.dumps(make.model()))
        assert payload["schema_version"] == capture.SCHEMA_VERSION

    def test_a_newer_capture_is_refused_rather_than_misread(self, make):
        payload = json.loads(capture.dumps(make.model()))
        payload["schema_version"] = capture.SCHEMA_VERSION + 1
        with pytest.raises(ValueError, match="schema version"):
            capture.from_dict(payload)

    def test_file_round_trip(self, make, tmp_path):
        path = str(tmp_path / "m.capture.json")
        capture.save(make.model(views=[make.view(10)]), path)
        assert capture.load(path).views[0].element_id == 10


class TestIssueOrdering:
    def test_sheets_are_not_interleaved_by_severity(self):
        # The report prints a heading per sheet, so a severity-major
        # order splits a sheet's findings across two headings. Sheet
        # groups must stay contiguous.
        issues = [
            Issue(rule_id="r", category="c", description="a", severity="medium", sheet_no="S101"),
            Issue(rule_id="r", category="c", description="b", severity="high", sheet_no="S102"),
            Issue(rule_id="r", category="c", description="c", severity="high", sheet_no="S101"),
        ]
        assert [i.sheet_no for i in sort_issues(issues)] == ["S101", "S101", "S102"]

    def test_severity_orders_within_a_sheet(self):
        issues = [
            Issue(rule_id="r", category="c", description="low", severity="low", sheet_no="S101"),
            Issue(rule_id="r", category="c", description="high", severity="high", sheet_no="S101"),
        ]
        assert [i.description for i in sort_issues(issues)] == ["high", "low"]

    def test_sheetless_findings_sort_last(self):
        issues = [
            Issue(rule_id="r", category="coverage", description="model-wide", severity="high"),
            Issue(rule_id="r", category="c", description="on a sheet", severity="low", sheet_no="S101"),
        ]
        assert [i.description for i in sort_issues(issues)] == ["on a sheet", "model-wide"]


class TestIssueIdentity:
    def test_same_finding_hashes_the_same(self):
        a = Issue(rule_id="r", category="c", description="d", element_id=5, view_id=1)
        b = Issue(rule_id="r", category="c", description="d", element_id=5, view_id=1)
        assert a.issue_id == b.issue_id

    def test_severity_is_not_part_of_identity(self):
        # Re-tiering a rule in config must not re-identify its findings,
        # or a reviewer's selection is lost on every config tweak.
        a = Issue(rule_id="r", category="c", description="d", severity="high")
        b = Issue(rule_id="r", category="c", description="d", severity="low")
        assert a.issue_id == b.issue_id

    def test_different_element_is_a_different_finding(self):
        a = Issue(rule_id="r", category="c", description="d", element_id=5)
        b = Issue(rule_id="r", category="c", description="d", element_id=6)
        assert a.issue_id != b.issue_id

    def test_dict_round_trip(self):
        original = Issue(
            rule_id="r",
            category="geometry",
            description="d",
            severity="high",
            element_id=5,
            view_id=1,
            view_name="V",
            sheet_no="S101",
            suggested_fix={"provenance": Provenance.DRAFTED},
        )
        restored = Issue.from_dict(original.to_dict())
        assert restored == original
        assert restored.issue_id == original.issue_id
