"""The Issue schema — CLAUDE.md: "Every rule's Issue output must carry a
precise location (point, bounding box, or entity handle — not just a sheet
number) and an optional suggested_fix." Built in from the first rule
written (this one), per PLANNING.md §8, so it isn't a later retrofit once
markup export exists.

`issue_id` and `from_dict` were added 2026-08-17 (backend review finding
3.1). PLANNING.md §8's flow puts "engineer selects which issues to
include" between the check run and the markup, and §2 keeps nothing
server-side — so the selected subset has to travel from the client back
to the server and be matched against freshly-recomputed issues. That
needs a stable identity per Issue and a way back from JSON, neither of
which existed: `Issue` had `to_dict` but no inverse, wasn't hashable, and
the only handle on it was the `#NNN` tag, which `markup/pdf_markup.py`
assigns by list position and therefore renumbers whenever the list
changes.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass, field
from typing import Optional

from pdfchecker.ir import BBox

# Page-space coordinates are rounded before hashing. Two runs over the
# same drawing can differ in the last float bits — a geometry `bbox` comes
# out of a transform chain, and changing how that chain is computed (the
# 2026-08-17 IFC work moved bboxes by ~1e-9m while being deliberately
# equivalent) must not change an Issue's identity. 0.01pt is far finer
# than anything visible on a sheet, so this discards noise, not signal.
_COORD_PRECISION = 2


@dataclass
class Issue:
    rule_id: str
    category: str  # matches a §8 markup-label category, e.g. "spelling", "title_block", "revision"
    sheet_no: Optional[str]
    page_index: int
    description: str
    bbox: Optional[BBox] = None  # None only for issues with no single location (rare)
    severity: str = "medium"  # "low" | "medium" | "high" — informational vs actionable
    suggested_fix: Optional[dict] = None  # e.g. {"corrected": "concrete"} or {"drawn": 3.42, "expected": 3.40}

    @property
    def issue_id(self) -> str:
        """A stable, content-derived identity — the same finding on the
        same drawing gets the same id on every run, on any machine.

        Derived rather than stored so it cannot go stale, and computed
        from what *identifies* the finding: rule, sheet, page, location
        and description. Deliberately NOT from `suggested_fix` (a change
        to how a fix is phrased shouldn't re-identify the finding) and
        NOT from `severity` (a config change that re-tiers a rule
        shouldn't either).

        Two Issues that agree on all five inputs collide — but they would
        be indistinguishable to a reviewer too, since every visible
        property matches. Confirmed against both real sample sets: zero
        collisions across 221 issues (see tests/test_issue_identity.py).

        Truncated to 16 hex chars: ~64 bits, far beyond collision range
        for a few thousand issues, and short enough to read in a JSON
        report or a URL."""

        bbox_part = (
            "none"
            if self.bbox is None
            else ",".join(
                f"{v:.{_COORD_PRECISION}f}"
                for v in (self.bbox.x0, self.bbox.y0, self.bbox.x1, self.bbox.y1)
            )
        )
        payload = "\x1f".join(
            [self.rule_id, self.sheet_no or "", str(self.page_index), bbox_part, self.description]
        )
        return hashlib.sha256(payload.encode("utf-8")).hexdigest()[:16]

    def to_dict(self) -> dict:
        return {
            "issue_id": self.issue_id,
            "rule_id": self.rule_id,
            "category": self.category,
            "sheet_no": self.sheet_no,
            "page_index": self.page_index,
            "description": self.description,
            "bbox": self.bbox.to_dict() if self.bbox else None,
            "severity": self.severity,
            "suggested_fix": self.suggested_fix,
        }

    @classmethod
    def from_dict(cls, d: dict) -> "Issue":
        """Rebuilds an Issue from `to_dict` output. `issue_id` in the
        input is ignored — it's derived, so reconstructing recomputes it;
        a mismatch would mean the payload was edited, and silently
        trusting a client-supplied id is exactly how a selection step
        gets spoofed. Use `issue_id` on the result to compare."""

        bbox = d.get("bbox")
        return cls(
            rule_id=d["rule_id"],
            category=d["category"],
            sheet_no=d.get("sheet_no"),
            page_index=d["page_index"],
            description=d["description"],
            bbox=BBox.from_dict(bbox) if bbox else None,
            severity=d.get("severity", "medium"),
            suggested_fix=d.get("suggested_fix"),
        )


def select_by_id(issues: list[Issue], issue_ids) -> list[Issue]:
    """The engineer-selection step (PLANNING.md §8 step 2) as a function:
    given this run's issues and the ids a client chose, return those
    issues in their original order.

    Ids that match nothing are ignored rather than raising — on a
    stateless server the client's selection may have come from a
    *previous* run over since-revised drawings, so an id that no longer
    resolves means "that finding is gone", which is a normal outcome and
    not an error. Callers that need to know can compare lengths."""

    wanted = set(issue_ids)
    return [i for i in issues if i.issue_id in wanted]
