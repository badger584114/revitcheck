"""Guard `revitcheck/en_gb_variants.py`'s curated variant list.

The list is data landed ahead of its rule — no Revit spelling check
exists yet — so these are map-level assertions rather than end-to-end
ones. They are kept because each guards a real mistake already made
once, and the whole reason the list was rescued out of the parked
PDF/DWG tree is that its content is hand-made judgement that a rewrite
would not reproduce.

The equivalent end-to-end tests (feeding a word through the actual
spelling rule and asserting the flag and the suggestion) live at the
`pdf-dwg-final` tag and should be reinstated alongside a Revit
`TextNote` spelling check, not rewritten from scratch.
"""

import pytest

from revitcheck.en_gb_variants import AMERICAN_TO_BRITISH, BRITISH_TO_AMERICAN


def test_no_self_mappings():
    """Every entry must actually transform, not silently map a word to
    itself. This caught a real bug: the plural forms ("millimetres") fell
    through `_RE_ER`'s "-re" -> "-er" rewrite unmapped, so every sheet's
    "ALL DIMENSIONS ARE IN MILLIMETRES" note was flagged as a
    misspelling — exactly the false positive the en-GB requirement
    exists to prevent."""

    assert [w for w, mapped in BRITISH_TO_AMERICAN.items() if w == mapped] == []
    assert AMERICAN_TO_BRITISH["millimeters"] == "millimetres"


class TestCentreFamily:
    """The "centre" family, added 2026-08-17 after profiling a real run
    showed `centerline` reported as a generic "possible misspelling"
    rather than as an American spelling with the British form suggested —
    the latter being the actual point of the requirement.

    Only bare "centre" had been mapped. `centreline`/`centred` carry a
    *medial* "-re", so the mechanical "-re" -> "-er" rewrite can never
    reach them; `centres` could have been reached and simply wasn't
    listed. That one matters most in practice: "AT 200 CENTRES" is
    standard reinforcement-spacing notation.
    """

    @pytest.mark.parametrize(
        "american,british",
        [
            ("center", "centre"),
            ("centers", "centres"),
            ("centerline", "centreline"),
            ("centerlines", "centrelines"),
            ("centered", "centred"),
        ],
    )
    def test_american_form_maps_to_the_british_one(self, american, british):
        assert AMERICAN_TO_BRITISH[american] == british
        assert BRITISH_TO_AMERICAN[british] == american

    def test_centring_is_deliberately_excluded(self):
        """"centring" is a real construction noun (temporary formwork
        supporting an arch during casting), not merely a spelling of
        "centering" — the dual-meaning case the module's own docstring
        rules out, alongside program/programme and metre/meter. Asserting
        the absence so nobody "completes" the family without reading
        why."""

        assert "centring" not in BRITISH_TO_AMERICAN
        assert "centering" not in AMERICAN_TO_BRITISH


def test_drafting_specific_variants_are_present():
    """A few civil/structural-specific pairs that a generic British word
    list would not carry, and that real sheets do use."""

    for american, british in (
        ("curb", "kerb"),
        ("aluminum", "aluminium"),
        ("sulfate", "sulphate"),
        ("gage", "gauge"),
    ):
        assert AMERICAN_TO_BRITISH[american] == british
