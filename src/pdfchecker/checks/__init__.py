"""Drafting check engine (PLANNING.md §4) — Stage 2 (PLANNING.md §9 step
2): title block completeness, spelling, revision consistency, plus
cross-sheet reference resolution (§4's "Cross-sheet consistency" row).
Importing this package registers every rule module's @register-decorated
functions into the catalog (catalog.py); run_checks() then executes
whichever of them a project's RuleConfig has enabled.

session_config.py (PLANNING.md §9 step 5) loads a session's uploaded
YAML/JSON rules file into that same RuleConfig, rather than a project
constructing one by hand as scripts/check.py and the tests still do for
their own defaults.
"""

from pdfchecker.checks.catalog import RuleConfig, all_rule_ids, run_checks  # noqa: F401
from pdfchecker.checks.issue import Issue  # noqa: F401
from pdfchecker.checks.session_config import LoadedSessionConfig, load_session_config  # noqa: F401

# Import for registration side-effects — each module's @register calls
# populate catalog._CATALOG when imported.
#
# `geometry` (Stage 3, PLANNING.md §5) belongs in this list just as much
# as the Stage 2 rule modules, and its absence was a real bug — fixed
# 2026-08-17 (backend review, finding 1.3). Without it the catalog held
# only the six drafting rules unless a caller happened to import
# `pdfchecker.checks.geometry` themselves, which meant a session config
# asking for `check_scope: drafting_and_geometry` silently ran *zero*
# geometry rules: no error, no warning, and an empty geometry result
# indistinguishable from "checked, nothing found". Importing every rule
# module here is what makes `all_rule_ids()` mean "every rule this
# codebase has", which is what both `RuleConfig.resolved_rule_ids()` and
# `session_config.py`'s catalog-membership warning assume.
from pdfchecker.checks import (  # noqa: F401,E402
    cross_sheet,
    geometry,
    revisions,
    spelling,
    title_block,
)
