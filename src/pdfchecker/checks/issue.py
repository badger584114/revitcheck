"""The Issue schema — CLAUDE.md: "Every rule's Issue output must carry a
precise location (point, bounding box, or entity handle — not just a sheet
number) and an optional suggested_fix." Built in from the first rule
written (this one), per PLANNING.md §8, so it isn't a later retrofit once
markup export exists.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional

from pdfchecker.ir import BBox


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

    def to_dict(self) -> dict:
        return {
            "rule_id": self.rule_id,
            "category": self.category,
            "sheet_no": self.sheet_no,
            "page_index": self.page_index,
            "description": self.description,
            "bbox": self.bbox.to_dict() if self.bbox else None,
            "severity": self.severity,
            "suggested_fix": self.suggested_fix,
        }
