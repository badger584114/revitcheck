import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks import RuleConfig, run_checks  # noqa: E402
from pdfchecker.extraction.pipeline import ingest_pdf  # noqa: E402

SAMPLE = str(
    Path(__file__).resolve().parent.parent
    / "samples"
    / "T2DPAA-T2D-C3S-BR-DRG-101000.pdf"
)


@pytest.fixture(scope="session")
def project():
    # Session-scoped: ingesting the 37-page sample takes ~90s: every test
    # module that needs the real project shares this one run rather than
    # re-parsing per module.
    return ingest_pdf(SAMPLE)


@pytest.fixture(scope="session")
def real_issues(project):
    # A full spelling pass over 37 pages is slow (sc.correction() does an
    # edit-distance search per unknown word) — session-scoped so every
    # test asserting against it shares one run instead of re-running the
    # whole document's spellcheck per assertion.
    config = RuleConfig(
        firm_glossary_path="config/firm_glossary.json",
        project_glossary_path="config/project_glossary.json",
    )
    return run_checks(project, config)
