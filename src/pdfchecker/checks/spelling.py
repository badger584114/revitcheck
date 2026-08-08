"""Spelling — PLANNING.md §4: en-GB base dictionary + firm/project
glossary. See en_gb_variants.py's docstring for why this is
`pyspellchecker` + a curated British/American variant list rather than a
proper en-GB dictionary (LanguageTool needs a JVM not available here) —
an interim, documented approximation, not a silent substitution.

Every flagged occurrence gets its own Issue with the specific word's own
location (not deduplicated per sheet) — PLANNING.md §8's markup export
needs a location per instance to box, and a term appearing many times on
one sheet legitimately means many markup points, not one.
"""

from __future__ import annotations

import re

from spellchecker import SpellChecker

from pdfchecker.checks.catalog import RuleConfig, register
from pdfchecker.checks.en_gb_variants import AMERICAN_TO_BRITISH, BRITISH_TO_AMERICAN
from pdfchecker.checks.glossary import load_glossary
from pdfchecker.checks.issue import Issue
from pdfchecker.ir import Project

_WORD_RE = re.compile(r"^[A-Za-z]+$")
# Tokens shorter than this are overwhelmingly engineering abbreviations on
# these sheets (RL, EL, BH, SSM, ...) rather than short real words —
# a coarse heuristic, documented as such rather than pretending precision.
_MIN_WORD_LEN = 4


def _build_checker(glossary: set[str]) -> SpellChecker:
    sc = SpellChecker(language="en")
    sc.word_frequency.load_words(BRITISH_TO_AMERICAN.keys())  # never flag correct British spellings
    sc.word_frequency.load_words(glossary)
    return sc


@register("spelling.en_gb")
def check_spelling(project: Project, config: RuleConfig) -> list[Issue]:
    glossary = load_glossary(config.firm_glossary_path, config.project_glossary_path)
    sc = _build_checker(glossary)

    issues = []
    for sheet in project.sheets:
        for word in sheet.words:
            token = word.text.strip(".,:;()\"'")
            if not _WORD_RE.match(token) or len(token) < _MIN_WORD_LEN:
                continue
            lower = token.lower()
            if lower in glossary:
                continue

            if lower in AMERICAN_TO_BRITISH:
                # Correctly spelled English, just the wrong locale for this
                # project — the actual point of the en-GB requirement, not
                # just suppressing false positives on British spellings.
                issues.append(
                    Issue(
                        rule_id="spelling.en_gb",
                        category="spelling",
                        sheet_no=sheet.sheet_no,
                        page_index=sheet.page_index,
                        description=f"American spelling '{token}' — project uses British English",
                        bbox=word.bbox,
                        severity="low",
                        suggested_fix={"corrected": AMERICAN_TO_BRITISH[lower]},
                    )
                )
                continue

            if sc.unknown([lower]):
                correction = sc.correction(lower)
                issues.append(
                    Issue(
                        rule_id="spelling.en_gb",
                        category="spelling",
                        sheet_no=sheet.sheet_no,
                        page_index=sheet.page_index,
                        description=f"Possible misspelling: '{token}'",
                        bbox=word.bbox,
                        severity="low",
                        suggested_fix={"corrected": correction} if correction else None,
                    )
                )
    return issues
