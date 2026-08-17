"""Stable `Issue` identity and the §7 report export (both 2026-08-17,
backend review findings 3.1/3.2).

PLANNING.md §8 puts "engineer selects which issues to include" between
the check run and the markup, and §2 keeps nothing server-side — so a
selected subset has to travel client -> server and be matched against
freshly-recomputed issues. That needs an id that is the same for the same
finding on every run, and a way back from JSON. Neither existed.

These tests are mostly about the *properties* that make the id usable
rather than its value: determinism, insensitivity to things that
shouldn't re-identify a finding, sensitivity to things that should, and
survival through a JSON round-trip.
"""

from __future__ import annotations

import csv
import io
import json
import random
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks.issue import Issue, select_by_id  # noqa: E402
from pdfchecker.ir import BBox  # noqa: E402
from pdfchecker.markup.pdf_markup import assign_tags  # noqa: E402
from pdfchecker.markup.report import build_report  # noqa: E402


def _issue(**overrides) -> Issue:
    base = dict(
        rule_id="spelling.en_gb",
        category="spelling",
        sheet_no="2871006",
        page_index=3,
        description="Possible misspelling: 'CONCERETE'",
        bbox=BBox(465.31, 1385.80, 537.49, 1401.94),
        severity="low",
        suggested_fix={"word": "CONCERETE", "corrected": "concrete"},
    )
    base.update(overrides)
    return Issue(**base)


class TestIssueId:
    def test_is_deterministic(self):
        assert _issue().issue_id == _issue().issue_id

    def test_is_short_and_hex(self):
        issue_id = _issue().issue_id
        assert len(issue_id) == 16
        int(issue_id, 16)  # raises if not hex

    @pytest.mark.parametrize(
        "field,value",
        [
            ("rule_id", "spelling.other"),
            ("sheet_no", "2871007"),
            ("page_index", 4),
            ("description", "Possible misspelling: 'CONDIUT'"),
            ("bbox", BBox(100.0, 200.0, 150.0, 220.0)),
        ],
    )
    def test_identifying_fields_change_the_id(self, field, value):
        assert _issue(**{field: value}).issue_id != _issue().issue_id

    @pytest.mark.parametrize(
        "field,value",
        [
            ("severity", "high"),
            ("suggested_fix", {"word": "CONCERETE", "corrected": "CONCRETE!"}),
            ("category", "something_else"),
        ],
    )
    def test_non_identifying_fields_do_not(self, field, value):
        """A config change that re-tiers a rule's severity, or a reworded
        `suggested_fix`, must not turn one finding into a different one —
        otherwise a client's saved selection silently stops matching."""

        assert _issue(**{field: value}).issue_id == _issue().issue_id

    def test_float_noise_does_not_change_identity(self):
        """bboxes come out of a transform chain. The 2026-08-17 IFC work
        moved some by ~1e-9m while being deliberately equivalent — that
        must not re-identify every geometry finding."""

        noisy = _issue(bbox=BBox(465.310000001, 1385.799999998, 537.49, 1401.94))
        assert noisy.issue_id == _issue().issue_id

    def test_a_real_difference_still_registers(self):
        """The rounding above discards noise, not signal — 0.01pt is the
        boundary, and a visible move is far larger."""

        moved = _issue(bbox=BBox(475.31, 1385.80, 547.49, 1401.94))
        assert moved.issue_id != _issue().issue_id

    def test_bbox_none_is_stable_and_distinct(self):
        a = _issue(bbox=None)
        assert a.issue_id == _issue(bbox=None).issue_id
        assert a.issue_id != _issue().issue_id


class TestRoundTrip:
    def test_to_dict_from_dict_preserves_everything(self):
        original = _issue()
        restored = Issue.from_dict(original.to_dict())
        assert restored == original
        assert restored.issue_id == original.issue_id

    def test_survives_real_json(self):
        restored = Issue.from_dict(json.loads(json.dumps(_issue().to_dict())))
        assert restored.issue_id == _issue().issue_id

    def test_bbox_none_round_trips(self):
        restored = Issue.from_dict(_issue(bbox=None).to_dict())
        assert restored.bbox is None

    def test_supplied_id_is_ignored_not_trusted(self):
        """`issue_id` is derived, so `from_dict` recomputes it. Trusting a
        client-supplied id is how a selection step gets spoofed."""

        payload = _issue().to_dict()
        payload["issue_id"] = "0000000000000000"
        assert Issue.from_dict(payload).issue_id == _issue().issue_id


class TestTagOrdering:
    def _many(self):
        # 20 issues sharing page/severity/rule_id — the real tie case:
        # one sheet's spelling findings all have identical sort keys.
        return [_issue(description=f"Possible misspelling: 'W{n:02d}'") for n in range(20)]

    def test_tags_are_stable_under_input_reordering(self):
        issues = self._many()
        expected = {i.issue_id: tag for tag, i in assign_tags(issues)}
        for seed in range(5):
            shuffled = issues[:]
            random.Random(seed).shuffle(shuffled)
            assert {i.issue_id: tag for tag, i in assign_tags(shuffled)} == expected

    def test_tags_are_contiguous_from_one(self):
        tags = [tag for tag, _ in assign_tags(self._many())]
        assert tags == [f"#{n:03d}" for n in range(1, 21)]

    def test_severity_still_leads_within_a_page(self):
        issues = [
            _issue(severity="low", description="low one"),
            _issue(severity="high", description="high one"),
            _issue(severity="medium", description="medium one"),
        ]
        ordered = [i.severity for _, i in assign_tags(issues)]
        assert ordered == ["high", "medium", "low"]


class TestSelectById:
    def test_returns_chosen_issues_in_original_order(self):
        issues = [_issue(description=f"w{n}") for n in range(5)]
        chosen = [issues[3].issue_id, issues[1].issue_id]
        assert select_by_id(issues, chosen) == [issues[1], issues[3]]

    def test_unknown_ids_are_ignored_not_raised(self):
        """On a stateless server a client's selection may come from a run
        over since-revised drawings — an id that no longer resolves means
        "that finding is gone", a normal outcome."""

        issues = [_issue()]
        assert select_by_id(issues, ["deadbeefdeadbeef"]) == []

    def test_empty_selection_yields_nothing(self):
        assert select_by_id([_issue()], []) == []


class _FakeResult:
    """`build_report` only reads these five attributes off a
    SessionResult; a stand-in keeps these tests off a ~12s real ingest."""

    def __init__(self, issues):
        self.issues = issues
        self.check_scope = "drafting_and_geometry"
        self.rules_run = ["spelling.en_gb", "geometry.dimension_consistency"]
        self.warnings = ["a coverage warning"]

        class _P:
            source_path = "samples/BR06/example.pdf"
            sheets = [object()] * 37

        self.project = _P()


class TestReport:
    def _report(self, n=3, **kw):
        issues = [_issue(description=f"Possible misspelling: 'W{i}'") for i in range(n)]
        return build_report(_FakeResult(issues), **kw), issues

    def test_carries_run_context_not_server_state(self):
        """§7: each report is self-contained — per §2 there is no server
        state left to point at by the time anyone opens it."""

        report, _ = self._report()
        d = report.to_dict()
        assert d["source_path"] == "samples/BR06/example.pdf"
        assert d["sheet_count"] == 37
        assert d["check_scope"] == "drafting_and_geometry"
        assert d["rules_run"] == sorted(["spelling.en_gb", "geometry.dimension_consistency"])
        assert d["warnings"] == ["a coverage warning"]

    def test_counts_match_entries(self):
        report, issues = self._report(n=4)
        d = report.to_dict()
        assert d["issue_count"] == len(issues)
        assert sum(d["counts_by_severity"].values()) == len(issues)
        assert sum(d["counts_by_category"].values()) == len(issues)

    def test_tags_match_the_markup_pass(self):
        """The whole point of the tag: #014 on a sheet and #014 in the
        report must be the same finding. Both go through assign_tags."""

        report, issues = self._report(n=5)
        assert [e.tag for e in report.entries] == [t for t, _ in assign_tags(issues)]

    def test_every_entry_carries_its_stable_id(self):
        report, _ = self._report()
        assert all(e.to_dict()["issue_id"] == e.issue.issue_id for e in report.entries)

    def test_json_is_valid_and_reconstructable(self):
        """The JSON report doubles as the artifact §7 names for a future
        stateless cross-run diff, so Issues must come back out of it."""

        report, issues = self._report(n=3)
        parsed = json.loads(report.to_json())
        restored = [Issue.from_dict(i) for i in parsed["issues"]]
        # Compared as sets: the report is deliberately tag-ordered, not
        # in the order the caller happened to accumulate issues.
        assert {i.issue_id for i in restored} == {i.issue_id for i in issues}
        assert [i.issue_id for i in restored] == [e.issue.issue_id for e in report.entries]

    def test_csv_has_a_header_and_one_row_per_issue(self):
        report, issues = self._report(n=4)
        rows = list(csv.DictReader(io.StringIO(report.to_csv())))
        assert len(rows) == len(issues)
        assert rows[0]["issue_id"] == report.entries[0].issue.issue_id

    def test_csv_page_is_one_based(self):
        """The report is read next to a printed set, where page 1 is the
        first sheet — page_index is 0-based internally."""

        report, _ = self._report(n=1)
        rows = list(csv.DictReader(io.StringIO(report.to_csv())))
        assert rows[0]["page"] == "4"  # page_index 3

    def test_drawn_vs_expected_columns_populated(self):
        """§7 asks for "drawn vs. expected value where applicable"."""

        report, _ = self._report(n=1)
        rows = list(csv.DictReader(io.StringIO(report.to_csv())))
        assert rows[0]["found"] == "CONCERETE"
        assert rows[0]["expected"] == "concrete"

    def test_no_drawn_expected_pair_is_blank_not_invented(self):
        report = build_report(_FakeResult([_issue(suggested_fix={"field": "drawing_no"})]))
        rows = list(csv.DictReader(io.StringIO(report.to_csv())))
        assert rows[0]["found"] == "" and rows[0]["expected"] == ""

    def test_marked_up_reflects_the_markup_pass(self):
        from pdfchecker.markup.pdf_markup import MarkupReportEntry

        report, issues = self._report(n=2)
        entry = MarkupReportEntry(
            tag="#001",
            issue_id=issues[0].issue_id,
            rule_id=issues[0].rule_id,
            category=issues[0].category,
            severity=issues[0].severity,
            sheet_no=issues[0].sheet_no,
            page_index=issues[0].page_index,
            description=issues[0].description,
            note="Spelling: concrete",
            rendered=True,
        )
        report = build_report(_FakeResult(issues), markup_entries=[entry])
        by_id = {e.issue.issue_id: e.marked_up for e in report.entries}
        assert by_id[issues[0].issue_id] is True
        assert by_id[issues[1].issue_id] is False

    def test_report_of_a_selected_subset(self):
        """§8 step 2 end to end: markup rendered for the whole run, report
        exported for the engineer's chosen subset. The two tag sequences
        differ by design, which is why `marked_up` joins on issue_id."""

        _, issues = self._report(n=5)
        chosen = select_by_id(issues, [issues[4].issue_id, issues[0].issue_id])
        report = build_report(_FakeResult(issues), issues=chosen)
        assert len(report.entries) == 2
        assert [e.tag for e in report.entries] == ["#001", "#002"]

    def test_write_produces_both_files(self, tmp_path):
        report, _ = self._report(n=2)
        written = report.write(str(tmp_path / "report"))
        assert [Path(p).name for p in written] == ["report.json", "report.csv"]
        assert all(Path(p).stat().st_size > 0 for p in written)
