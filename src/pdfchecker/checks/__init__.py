"""Drafting check engine (PLANNING.md §4) — Stage 2 (PLANNING.md §9 step
2): title block completeness, spelling, revision consistency, plus
cross-sheet reference resolution (§4's "Cross-sheet consistency" row).
Importing this package registers every rule module's @register-decorated
functions into the catalog (catalog.py); run_checks() then executes
whichever of them a project's RuleConfig has enabled.
"""

from pdfchecker.checks.catalog import RuleConfig, all_rule_ids, run_checks  # noqa: F401
from pdfchecker.checks.issue import Issue  # noqa: F401

# Import for registration side-effects — each module's @register calls
# populate catalog._CATALOG when imported.
from pdfchecker.checks import cross_sheet, revisions, spelling, title_block  # noqa: F401,E402
