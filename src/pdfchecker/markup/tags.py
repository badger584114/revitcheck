"""Shared reference-tag numbering — PLANNING.md §8: "each issue gets a
short reference tag (e.g. #014) ... matching an entry in the full
exported report." That report is one shared artifact per check run, not
one per output format, so every markup output (PDF, DXF/DWG) has to
assign the *same* tag to the *same* Issue — factored out here once a
second consumer (`dxf_markup.py`) needed the exact ordering
`pdf_markup.py` already had, rather than risking the two drifting apart
if each format computed its own.
"""

from __future__ import annotations

from pdfchecker.checks.issue import Issue

# PLANNING.md §8 doesn't specify a tag order; page-then-severity-then-
# rule_id groups a sheet's most actionable issues first, matching how an
# engineer would triage a printed set page by page.
_SEVERITY_ORDER = {"high": 0, "medium": 1, "low": 2}


def assign_tags(issues: list[Issue]) -> list[tuple[str, Issue]]:
    """`[(tag, issue), ...]` in the shared, deterministic order — same
    `Issue` object, same tag, regardless of which markup format calls
    this for a given check run's `issues` list."""

    ordered = sorted(
        issues,
        key=lambda i: (i.page_index, _SEVERITY_ORDER.get(i.severity, len(_SEVERITY_ORDER)), i.rule_id),
    )
    return [(f"#{idx:03d}", issue) for idx, issue in enumerate(ordered, start=1)]
