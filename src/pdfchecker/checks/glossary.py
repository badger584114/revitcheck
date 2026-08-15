"""Glossary loading — PLANNING.md §4 "Custom dictionary / glossary
management": two tiers, both stored as data, both feeding the same
spellchecker. `RuleConfig.firm_glossary_path` / `project_glossary_path`
point at JSON files of the shape `{"words": [...]}`; there's no "Add to
dictionary" UI yet (no frontend exists), so populating these is manual for
now — the loading mechanism this module provides is what that UI would
write to once it exists, not a placeholder that gets replaced later.

A third, file-less source was added for §9 step 5's session-level rule
config (`checks/session_config.py`): `glossary.session_additions` in a
session's uploaded rules file — terms that only apply for that one run,
layered on top of the two JSON-backed tiers via `extra_words` rather than
written to either file.
"""

from __future__ import annotations

import json
from pathlib import Path


def load_glossary(*paths: str | None, extra_words: set[str] | None = None) -> set[str]:
    words: set[str] = set()
    for path in paths:
        if not path:
            continue
        p = Path(path)
        if not p.exists():
            continue
        data = json.loads(p.read_text())
        words.update(w.strip().lower() for w in data.get("words", []) if w.strip())
    if extra_words:
        words.update(w.strip().lower() for w in extra_words if w.strip())
    return words
