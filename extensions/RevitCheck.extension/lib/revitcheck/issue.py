"""The Issue schema, Revit edition.

Carried over from `pdfchecker.checks.issue` with one deliberate change,
and it is the change that makes the whole pivot worth it. The PDF Issue
located a finding as `page_index` + `bbox` in page points, because the
check ran on an export and the fix happened somewhere else — so the
location had to be precise enough to draw a box on a sheet and hand it
to a drafter. Here the check runs inside the document being fixed, so a
location is an `element_id`: pyRevit turns that into a link that selects
and zooms the element directly. PLANNING.md §8's entire markup-export
apparatus exists to bridge a gap that no longer exists.

What is kept, because it was reasoned through once already and the
reasoning still holds:

- **`issue_id` is derived, not stored** — the same finding on the same
  model gets the same id on every run, on any machine, so a reviewer's
  selection can survive a re-run.
- **What feeds the hash is what *identifies* the finding** — rule,
  location, description. Not `severity` (re-tiering a rule in config
  must not re-identify its findings) and not `suggested_fix` (rewording
  a fix must not either).
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Any, Dict, Optional


@dataclass
class Issue:
    rule_id: str
    category: str
    description: str
    severity: str = "medium"  # "low" | "medium" | "high"
    # The element to select and zoom to. None only for findings with no
    # single element — a model-wide coverage warning, for instance.
    element_id: Optional[int] = None
    view_id: Optional[int] = None
    view_name: Optional[str] = None
    sheet_no: Optional[str] = None
    suggested_fix: Optional[Dict[str, Any]] = None

    @property
    def issue_id(self) -> str:
        parts = [
            self.rule_id,
            str(self.element_id if self.element_id is not None else ""),
            str(self.view_id if self.view_id is not None else ""),
            self.sheet_no or "",
            self.description,
        ]
        digest = hashlib.sha256("\x1f".join(parts).encode("utf-8"))
        return digest.hexdigest()[:12]

    def to_dict(self) -> Dict[str, Any]:
        return {
            "issue_id": self.issue_id,
            "rule_id": self.rule_id,
            "category": self.category,
            "description": self.description,
            "severity": self.severity,
            "element_id": self.element_id,
            "view_id": self.view_id,
            "view_name": self.view_name,
            "sheet_no": self.sheet_no,
            "suggested_fix": self.suggested_fix,
        }

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "Issue":
        """Inverse of `to_dict`. `issue_id` is ignored on the way in —
        it is derived from the other fields, so accepting a stored one
        would let a hand-edited file claim an identity its content does
        not actually hash to."""
        return cls(
            rule_id=data["rule_id"],
            category=data["category"],
            description=data["description"],
            severity=data.get("severity", "medium"),
            element_id=data.get("element_id"),
            view_id=data.get("view_id"),
            view_name=data.get("view_name"),
            sheet_no=data.get("sheet_no"),
            suggested_fix=data.get("suggested_fix"),
        )


_SEVERITY_ORDER = {"high": 0, "medium": 1, "low": 2}


def sort_issues(issues):
    """Sheet-major, most severe first within each sheet.

    Severity-major was the first cut and it was wrong for this output:
    the report groups by sheet, so ordering by severity first interleaves
    the groups and can print the same sheet heading twice. It is also the
    wrong shape for the work — an engineer reviews a drawing set one
    sheet at a time, and the per-issue severity marks keep severity
    scannable within a sheet without reordering the whole list around it.

    Findings with no sheet (model-wide coverage warnings) sort last
    rather than into the middle of the set.
    """
    return sorted(
        issues,
        key=lambda i: (
            i.sheet_no is None,
            i.sheet_no or "",
            _SEVERITY_ORDER.get(i.severity, 99),
            i.view_name or "",
            i.rule_id,
        ),
    )
