"""British/American spelling-variant pairs, unambiguous cases only (no
program/programme, license/licence, practice/practise, metre/meter,
story/storey — these have genuine dual meanings or noun/verb splits that
would create false positives either direction).

PLANNING.md §1/§4/§10 all call for an **en-GB base dictionary**, and flag
`pyspellchecker`'s bundled dictionary as US-oriented — confirmed directly:
its default 'en' dictionary flags "specialised", "colour", "kerb",
"centre", "programme" as unknown. A full self-hosted LanguageTool server
(§10's actual recommended choice for this) needs a JVM + downloaded
language models not available in this dev environment, so this is a
pragmatic interim fix: layer a curated variant list on top of
`pyspellchecker` rather than block Stage 2 on standing up LanguageTool.
Swapping to a proper en-GB dictionary (LanguageTool, or a hunspell en_GB
`.dic`) is real follow-up work this list doesn't try to replace — it
covers common cases, not every word in the language.

Maps BRITISH -> AMERICAN. Both directions get used:
- British forms are added to the accepted-words set so they never flag.
- American forms found in text get flagged with the British correction as
  `suggested_fix` (PLANNING.md §8: `Spelling: <correct word>`), which is
  the actual point of the requirement — catching drafters defaulting to
  American spelling, not just suppressing false positives on British ones.
"""

from __future__ import annotations

# -ise (British) / -ize (American) — a large, productive category.
_ISE_IZE = [
    "specialise", "realise", "organise", "recognise", "authorise", "minimise",
    "optimise", "utilise", "finalise", "initialise", "standardise", "mobilise",
    "stabilise", "prioritise", "categorise", "characterise", "summarise",
    "capitalise", "normalise", "generalise", "localise", "materialise",
    "apologise", "criticise", "emphasise", "familiarise", "harmonise",
    "modernise", "neutralise", "civilise", "colonise", "deputise", "dramatise",
    "economise", "energise", "fertilise", "formalise", "fossilise", "galvanise",
    "homogenise", "hospitalise", "humanise", "hypnotise", "idolise", "immunise",
    "legalise", "liberalise", "maximise", "memorise", "mesmerise", "militarise",
    "miniaturise", "moisturise", "monopolise", "moralise", "motorise",
    "naturalise", "novelise", "ostracise", "oxidise", "patronise", "penalise",
    "personalise", "pluralise", "polarise", "popularise", "pressurise",
    "privatise", "publicise", "radicalise", "randomise", "rationalise",
    "reorganise", "revolutionise", "ritualise", "romanticise", "sanitise",
    "satirise", "scandalise", "scrutinise", "sensitise", "serialise",
    "solemnise", "stigmatise", "subsidise", "tantalise", "terrorise",
    "theorise", "tranquillise", "traumatise", "tyrannise", "unionise",
    "urbanise", "vandalise", "vaporise", "verbalise", "victimise", "vitalise",
    "vocalise", "vulgarise", "westernise", "symbolise", "sympathise",
    "synchronise", "visualise",
]

# -yse (British) / -yze (American).
_YSE_YZE = ["analyse", "catalyse", "electrolyse", "hydrolyse", "paralyse"]

# -our (British) / -or (American).
_OUR_OR = [
    "colour", "favour", "harbour", "honour", "humour", "labour",
    "neighbour", "rumour", "savour", "vapour", "behaviour", "endeavour",
    "flavour", "armour",
]

# -re (British) / -er (American) — genuinely unambiguous ones only.
# Bare "metre" is deliberately excluded (ambiguous with "meter" the
# measuring device), but the compound forms below aren't — "millimeter"
# as a device isn't a real word, so these are safe. Missing these was a
# real gap found running this against a real sample: every sheet's
# "ALL DIMENSIONS ARE IN MILLIMETRES" note was flagged as a misspelling
# with "millimeters" suggested — exactly the false positive this whole
# en-GB requirement exists to prevent, so worth calling out here rather
# than folding in silently.
_RE_ER = [
    "centre", "theatre", "fibre", "calibre", "sombre", "manoeuvre", "litre",
    "millimetre", "millimetres", "centimetre", "centimetres",
    "kilometre", "kilometres", "decimetre", "decimetres",
]

# -ogue (British) / -og (American).
_OGUE_OG = ["catalogue", "dialogue", "analogue"]

# Doubled-consonant British forms vs single-consonant American ones.
_DOUBLED = [
    ("travelling", "traveling"), ("travelled", "traveled"), ("traveller", "traveler"),
    ("modelling", "modeling"), ("modelled", "modeled"), ("cancelled", "canceled"),
    ("cancelling", "canceling"), ("labelled", "labeled"), ("labelling", "labeling"),
    ("signalling", "signaling"), ("signalled", "signaled"), ("levelled", "leveled"),
    ("levelling", "leveling"), ("fuelled", "fueled"), ("fuelling", "fueling"),
]

# Miscellaneous single-word variants, including some relevant to civil/
# structural drafting specifically (kerb, sulphate, aluminium). Explicit
# (british, american) pairs — including a few, like "favourite", whose
# British form doesn't end in one of the productive suffixes above, so
# the systematic inflection generation below wouldn't otherwise reach it.
_MISC = [
    ("kerb", "curb"), ("kerbs", "curbs"), ("tyre", "tire"), ("tyres", "tires"),
    ("aluminium", "aluminum"), ("sulphate", "sulfate"), ("sulphates", "sulfates"),
    ("sulphur", "sulfur"), ("grey", "gray"), ("greys", "grays"),
    ("mould", "mold"), ("moulds", "molds"), ("moulded", "molded"),
    ("moulding", "molding"), ("plough", "plow"), ("ploughed", "plowed"),
    ("jewellery", "jewelry"), ("cosy", "cozy"), ("artefact", "artifact"),
    ("artefacts", "artifacts"), ("defence", "defense"), ("offence", "offense"),
    ("pretence", "pretense"), ("gauge", "gage"), ("gauges", "gages"),
    ("gauged", "gaged"),
    ("favourite", "favorite"), ("favourites", "favorites"),  # "fav-our-ite" —
    # doesn't end in "our", so the -our/-or suffix rule below never reaches it.
    ("programme", "program"), ("programmes", "programs"),  # "programme" (a
    # schedule/plan) is the one sense that's unambiguous in a drafting
    # context — software's "program" isn't something a drawing set's text
    # would contain.
]


def _inflect_ise(base: str, target_suffix: str) -> list[tuple[str, str]]:
    """Given a British verb ending "...ise"/"...yse" and its American
    target suffix ("ize"/"yze"), generate the common inflected forms both
    directions — base, past tense (+d), 3rd person (+s), gerund (+ing,
    dropping the final 'e'). Missing these was the actual bug that let
    "SPECIALIZED"/"SPECIALISED" through unrecognized in testing — real
    text uses inflected forms far more than bare infinitives."""

    src_suffix = base[-3:]  # "ise" or "yse"
    stem = base[:-3]
    pairs = [(stem + src_suffix, stem + target_suffix)]
    pairs.append((stem + src_suffix + "d", stem + target_suffix + "d"))
    pairs.append((stem + src_suffix + "s", stem + target_suffix + "s"))
    pairs.append((stem + src_suffix[:-1] + "ing", stem + target_suffix[:-1] + "ing"))
    return pairs


def _inflect_our(base: str) -> list[tuple[str, str]]:
    """British noun/verb ending "...our" (colour, favour, ...) and its
    common inflections — base, plural/3rd-person (+s), past tense (+ed),
    gerund (+ing). Same rationale as _inflect_ise: real text is mostly
    inflected forms ("coloured", "endeavouring"), not bare nouns."""

    stem = base[:-3]
    return [
        (base, stem + "or"),
        (base + "s", stem + "ors"),
        (base + "ed", stem + "ored"),
        (base + "ing", stem + "oring"),
    ]


def _build_map() -> dict[str, str]:
    mapping: dict[str, str] = {}
    for word in _ISE_IZE:
        mapping.update(_inflect_ise(word, "ize"))
    for word in _YSE_YZE:
        mapping.update(_inflect_ise(word, "yze"))
    for word in _OUR_OR:
        mapping.update(_inflect_our(word))
    for word in _RE_ER:
        # Plurals end "...res" (millimetres), not "...re" — check that
        # first, or e.g. "millimetres" falls through unmapped (its
        # American form is "millimeters", not "millimetrer").
        if word.endswith("res"):
            mapping[word] = word[:-3] + "ers"
        elif word.endswith("re"):
            mapping[word] = word[:-2] + "er"
        else:
            mapping[word] = word
    for word in _OGUE_OG:
        mapping[word] = word[:-3]  # "catalogue" -> "catalog"
    for british, american in _DOUBLED:
        mapping[british] = american
    for british, american in _MISC:
        mapping[british] = american
    return mapping


BRITISH_TO_AMERICAN: dict[str, str] = _build_map()
AMERICAN_TO_BRITISH: dict[str, str] = {v: k for k, v in BRITISH_TO_AMERICAN.items()}
