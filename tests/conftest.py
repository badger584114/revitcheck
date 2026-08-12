import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks import RuleConfig, run_checks  # noqa: E402
from pdfchecker.extraction.pipeline import ingest_pdf  # noqa: E402

SAMPLE = str(
    Path(__file__).resolve().parent.parent
    / "samples"
    / "BR06"
    / "T2DPAA-T2D-C3S-BR-DRG-101000.pdf"
)

# A second real set — same drawings, later revision — added once amended
# sheets with real revision clouds existed to calibrate
# extraction/revision_clouds.py against (see that module's docstring).
# Kept as its own fixture/file rather than replacing SAMPLE above: SAMPLE
# is deliberately the clean "AMEND No. 0 everywhere" set other tests rely
# on for their "correctly finds nothing" assertions.
AMENDED_SAMPLE = str(
    Path(__file__).resolve().parent.parent
    / "samples"
    / "BR06"
    / "T2DPAA-T2D-C3S-BR-DRG-101000_1.pdf"
)


@pytest.fixture(scope="session")
def project():
    # Session-scoped: ingesting the 37-page sample takes ~90s: every test
    # module that needs the real project shares this one run rather than
    # re-parsing per module.
    return ingest_pdf(SAMPLE)


@pytest.fixture(scope="session")
def amended_project():
    return ingest_pdf(AMENDED_SAMPLE)


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
