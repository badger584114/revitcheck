"""Check-run report export — PLANNING.md §7.

§7 asks for "issue reference tag, full description, severity, sheet,
location, rule reference, drawn vs. expected value where applicable ...
the detailed companion to the minimal on-sheet markup (§8) — download
together as one package". The markup carries a terse `Label: payload`
note plus a `#NNN` tag; this is where that tag resolves to the whole
finding.

**Two formats, for two different readers.**

- `to_json()` — the machine-readable artifact: an API response, and the
  thing a frontend renders its filterable issue list from (§7's "in-app"
  half). Every entry carries `issue_id`, so this doubles as the input to
  the one capability §7 explicitly dropped: cross-run revision diffing
  was removed with §2's stateless decision, and §7 records that if it
  ever returns, the redesign is to accept "the user's own
  previously-downloaded report ... and diff against that supplied
  artifact instead of anything server-stored". A JSON report with stable
  ids in it *is* that artifact. Nothing here builds the diff — but this
  format is chosen so that it stays possible.
- `to_csv()` — §7's "Excel report". CSV rather than a real `.xlsx`
  because engineers open CSV in Excel without ceremony and it costs no
  new dependency (`openpyxl` would be one, for formatting nobody asked
  for). One row per issue, spreadsheet-filterable by severity, sheet or
  rule, which is how a reviewer actually triages a couple of hundred
  findings.

**Not built: a PDF report.** §7 says "PDF and/or Excel". The PDF half is
deliberately skipped rather than half-done — the marked-up PDF
(`pdf_markup.py`) already covers "read it on the drawing", and a second,
separately-laid-out PDF of the same table is layout work serving a reader
who is better served by the spreadsheet. Worth revisiting if a real
reviewer asks for it; not worth guessing at now.

**Self-contained, per §7's third bullet.** A report carries the run's own
context (source file, sheet count, scope, which rules actually ran, and
any coverage warnings) rather than pointing at server state that, per §2,
will not exist by the time anyone opens it.
"""

from __future__ import annotations

import csv
import io
import json
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Optional

from pdfchecker.checks.issue import Issue
from pdfchecker.markup.notes import markup_note
from pdfchecker.markup.pdf_markup import MarkupReportEntry, assign_tags

# §7: "drawn vs. expected value where applicable". Rules put those under
# their own keys in `suggested_fix`; this maps the real ones in the
# catalog onto the report's two generic columns so a spreadsheet can show
# one "Found" / "Expected" pair rather than a different column per rule.
_DRAWN_EXPECTED_KEYS = (
    ("drawn_mm", "stated_mm"),        # geometry.dimension_consistency
    ("derived", "stated"),            # geometry.setout_reconstruction
    ("word", "corrected"),            # spelling.en_gb
    ("schedule_latest_rev_id", "title_block_amend_no"),  # revision.schedule_matches_title_block
)

_CSV_COLUMNS = (
    "tag",
    "issue_id",
    "severity",
    "category",
    "rule_id",
    "sheet_no",
    "page",
    "description",
    "note",
    "found",
    "expected",
    "location",
    "marked_up",
)


def _drawn_expected(fix: Optional[dict]) -> tuple[str, str]:
    """§7's "drawn vs. expected where applicable" — `("", "")` when the
    rule reports no such pair, which is most of them (a missing title
    block field has no "found" value by definition)."""

    if not fix:
        return "", ""
    for found_key, expected_key in _DRAWN_EXPECTED_KEYS:
        if found_key in fix or expected_key in fix:
            return str(fix.get(found_key, "")), str(fix.get(expected_key, ""))
    return "", ""


@dataclass
class ReportEntry:
    """One row of §7's export — the full detail behind one `#NNN` tag on
    the marked-up sheet."""

    tag: str
    issue: Issue
    note: str
    marked_up: bool

    def to_dict(self) -> dict:
        found, expected = _drawn_expected(self.issue.suggested_fix)
        d = self.issue.to_dict()
        d.update(
            {
                "tag": self.tag,
                "note": self.note,
                "found": found,
                "expected": expected,
                "marked_up": self.marked_up,
            }
        )
        return d

    def to_row(self) -> dict:
        d = self.to_dict()
        bbox = d["bbox"]
        return {
            "tag": d["tag"],
            "issue_id": d["issue_id"],
            "severity": d["severity"],
            "category": d["category"],
            "rule_id": d["rule_id"],
            "sheet_no": d["sheet_no"] or "",
            "page": d["page_index"] + 1,  # 1-based: the report is read next to a printed set
            "description": d["description"],
            "note": d["note"],
            "found": d["found"],
            "expected": d["expected"],
            "location": "" if not bbox else f"{bbox['x0']:.0f},{bbox['y0']:.0f}",
            "marked_up": "yes" if d["marked_up"] else "no",
        }


@dataclass
class CheckReport:
    source_path: str
    sheet_count: int
    check_scope: str
    rules_run: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    entries: list[ReportEntry] = field(default_factory=list)
    generated_at: str = ""

    @property
    def counts_by_severity(self) -> dict:
        out: dict = {}
        for e in self.entries:
            out[e.issue.severity] = out.get(e.issue.severity, 0) + 1
        return out

    @property
    def counts_by_category(self) -> dict:
        out: dict = {}
        for e in self.entries:
            out[e.issue.category] = out.get(e.issue.category, 0) + 1
        return out

    def to_dict(self) -> dict:
        return {
            "source_path": self.source_path,
            "generated_at": self.generated_at,
            "sheet_count": self.sheet_count,
            "check_scope": self.check_scope,
            "rules_run": sorted(self.rules_run),
            "issue_count": len(self.entries),
            "counts_by_severity": self.counts_by_severity,
            "counts_by_category": self.counts_by_category,
            "warnings": list(self.warnings),
            "issues": [e.to_dict() for e in self.entries],
        }

    def to_json(self, indent: int = 2) -> str:
        return json.dumps(self.to_dict(), indent=indent)

    def to_csv(self) -> str:
        buf = io.StringIO()
        writer = csv.DictWriter(buf, fieldnames=list(_CSV_COLUMNS), lineterminator="\n")
        writer.writeheader()
        for entry in self.entries:
            writer.writerow(entry.to_row())
        return buf.getvalue()

    def write(self, path_stem: str) -> list[str]:
        """Writes `<stem>.json` and `<stem>.csv`, returning the paths —
        §7's "download together as one package", alongside the marked-up
        PDF the same run produced."""

        written = []
        for suffix, text in ((".json", self.to_json()), (".csv", self.to_csv())):
            path = path_stem + suffix
            with open(path, "w", encoding="utf-8", newline="") as fh:
                fh.write(text)
            written.append(path)
        return written


def build_report(
    result,
    issues: Optional[list[Issue]] = None,
    markup_entries: Optional[list[MarkupReportEntry]] = None,
) -> CheckReport:
    """Builds §7's report from a `session.SessionResult`.

    `issues` defaults to the whole run; pass a subset for §8 step 2's
    engineer selection (`checks.issue.select_by_id` turns a client's
    chosen ids back into Issues). Tags come from the same
    `markup/pdf_markup.py:assign_tags` the markup itself uses, over the
    same list — that shared call is what makes `#014` on a sheet and
    `#014` in the report the same finding.

    `markup_entries` is `render_markup`'s return value, used only to fill
    in `marked_up` — whether the issue actually got drawn, or had no
    `bbox` to draw at. Omitted, everything reports `marked_up=False`,
    which is honest for a report exported without a markup pass rather
    than a claim that nothing was drawable."""

    issues = result.issues if issues is None else issues
    # Joined on issue_id, not tag: a caller may legitimately have
    # rendered markup for the full run and then be reporting a selected
    # subset, in which case the two tag sequences differ by design.
    drawn_ids = {e.issue_id for e in (markup_entries or []) if e.rendered}

    entries = [
        ReportEntry(
            tag=tag,
            issue=issue,
            note=markup_note(issue),
            marked_up=issue.issue_id in drawn_ids,
        )
        for tag, issue in assign_tags(issues)
    ]
    return CheckReport(
        source_path=result.project.source_path,
        sheet_count=len(result.project.sheets),
        check_scope=result.check_scope,
        rules_run=list(result.rules_run),
        warnings=list(result.warnings),
        entries=entries,
        generated_at=datetime.now(timezone.utc).isoformat(timespec="seconds"),
    )
