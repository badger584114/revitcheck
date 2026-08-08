"""Title block completeness — PLANNING.md §4's "Standards/conventions"
row: "title block fields complete". First of the three Stage 2 checks
(PLANNING.md §9 step 2), and the simplest: every sheet's title block must
carry the fields a project requires (config, not a fixed list — different
projects/firms have different mandatory fields).
"""

from __future__ import annotations

from pdfchecker.checks.catalog import RuleConfig, register
from pdfchecker.checks.issue import Issue
from pdfchecker.extraction.titleblock import TITLE_BLOCK_BAND_FRACTION
from pdfchecker.ir import BBox, Project


@register("title_block.required_fields_present")
def check_required_fields(project: Project, config: RuleConfig) -> list[Issue]:
    issues = []
    for sheet in project.sheets:
        # No single point to box for an *absent* field — per PLANNING.md
        # §8, a missing-annotation issue gets a marker at the region it
        # should have been in, not a precise box (nothing exists to box).
        # The title-block band itself is the best available location.
        band_bbox = BBox(
            x0=0,
            y0=sheet.page_height * (1 - TITLE_BLOCK_BAND_FRACTION),
            x1=sheet.page_width,
            y1=sheet.page_height,
        )
        for field_name in config.required_title_block_fields:
            if not sheet.title_block.get(field_name):
                issues.append(
                    Issue(
                        rule_id="title_block.required_fields_present",
                        category="title_block",
                        sheet_no=sheet.sheet_no,
                        page_index=sheet.page_index,
                        description=f"Title block field '{field_name}' is missing or unreadable",
                        bbox=band_bbox,
                        severity="high",
                        suggested_fix=None,  # nothing to auto-derive; a human has to supply the field
                    )
                )
    return issues
