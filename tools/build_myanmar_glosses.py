"""Builds the Myanmar meanings Define shows, from the AKK English-Myanmar dictionary.

Usage:
    python tools/build_myanmar_glosses.py <dictionary.db> <LICENSE.txt> <output dir>

The inputs are the AKK dictionary's own database and licence
(https://github.com/aungkokomm/English-Myanmar-Dictionary-).

Writes <output dir>/akk-en-my.tsv.gz and <output dir>/LICENSE-AKK.txt.

The file is UTF-8 text, gzip-compressed, one record per line:

    G <tab> word <tab> pos <tab> gloss [<unit separator> gloss ...]

word is the lowercased single word the entry is for. Many AKK headwords are
learner's-dictionary patterns rather than bare words, "say sth (to sb)",
"new (-er,-est)", "important (to sb/sth)", and those are read as the word they
are a pattern of; an entry whose headword is exactly the word keeps its
glosses ahead of pattern entries for the same word. pos is n, v, a or r, the
letters WordNet uses, so each gloss is found under the English meaning it
translates. AKK's own labels are mapped onto those four (adj -> a, adjs -> a,
and so on); a combined label such as "adj;adv" files the gloss under both, and
rows with any other label, or none, are left out.

Each (word, pos) keeps its first MAX_GLOSSES glosses in the AKK dictionary's
own order, which is the order the AKK app shows them in, after dropping rows
a one-glance popup cannot use:
  - longer than MAX_LENGTH characters, or ending the way spoken Burmese
    sentences end (လဲ, ဘူး, တယ် ...): these are example sentences filed as
    glosses, where a real gloss ends in literary သည်, သော or ခြင်း;
  - with fewer than two Myanmar letters (stray numbering such as "၁၊");
  - the same gloss again with only its spacing changed.
A bracket left without its partner at either end ("( ...", "... )") is trimmed.
"""

import collections
import gzip
import re
import shutil
import sqlite3
import sys
from pathlib import Path

MAX_GLOSSES = 3
MAX_LENGTH = 60
UNIT_SEPARATOR = "\x1f"

POS = {"n": "n", "ns": "n", "v": "v", "vs": "v", "adj": "a", "adjs": "a", "adv": "r"}

WORD = re.compile(r"[A-Za-z][A-Za-z'\-]*")
MYANMAR_LETTER = re.compile("[" + chr(0x1000) + "-" + chr(0x102A) + "]")
SPOKEN_ENDINGS = ("လဲ", "လား", "တယ်", "ဘူး", "မယ်", "နော်", "။", "?")

# The slots and notes of a learner's-dictionary pattern: bracketed notes such as
# "(to sb)" or "(-er,-est)", and the placeholders sth, sb, one's and the like.
BRACKETED = re.compile(r"\([^()]*\)")
PLACEHOLDER = re.compile(r"\b(?:sth|sb|sb's|one's|oneself|somebody|something|etc)\b", re.IGNORECASE)


def word_of(headword: str) -> tuple[str, int] | None:
    """The single word a headword is for, and 0 for a bare word or 1 for a pattern; None for a phrase.

    A headword listing alternatives separated by ";", such as
    "grateful (to sb) (for sth); grateful (to do sth); grateful (that_)", is
    for a word only when every alternative is for that same word.
    """
    alternatives = [one_word_of(part) for part in (headword or "").split(";") if part.strip()]
    if not alternatives or not all(alternatives):
        return None
    words = {word for word, _tier in alternatives}
    if len(words) != 1:
        return None
    return words.pop(), max(tier for _word, tier in alternatives)


def one_word_of(headword: str) -> tuple[str, int] | None:
    """word_of for a single alternative."""
    text = (headword or "").replace("\\/", "/").replace("\\'", "'").replace("\\-", "-").strip()
    if WORD.fullmatch(text):
        return text.lower(), 0

    previous = None
    while previous != text:
        previous, text = text, BRACKETED.sub(" ", text)
    text = PLACEHOLDER.sub(" ", text)
    text = " ".join(text.replace("/", " ").replace(",", " ").split())
    return (text.lower(), 1) if WORD.fullmatch(text) else None


def cleaned(definition: str) -> str:
    """One line, single-spaced, without escaped slashes or a bracket cut off from its partner."""
    text = " ".join((definition or "").replace("\\/", "/").split())
    while text.startswith(")") and text.count(")") > text.count("("):
        text = text[1:].strip()
    while text.endswith("(") and text.count("(") > text.count(")"):
        text = text[:-1].strip()
    return text


def usable(gloss: str) -> bool:
    return (0 < len(gloss) <= MAX_LENGTH
            and len(MYANMAR_LETTER.findall(gloss)) >= 2
            and not gloss.endswith(SPOKEN_ENDINGS))


def main(db: Path, licence: Path, out_dir: Path) -> None:
    con = sqlite3.connect(f"file:{db.as_posix()}?mode=ro", uri=True)
    candidates = collections.defaultdict(list)  # (word, pos) -> [(tier, id, gloss)]
    dropped = 0
    patterns = 0
    for row_id, headword, labels, definition in con.execute("select id, headword, pos, definition from entries order by id"):
        found = word_of(headword)
        if found is None:
            continue
        word, tier = found

        gloss = cleaned(definition)
        if not usable(gloss):
            dropped += 1
            continue

        patterns += tier
        for label in (labels or "").split(";"):
            pos = POS.get(label.strip())
            if pos is not None:
                candidates[(word, pos)].append((tier, row_id, gloss))

    glosses = {}
    for key, rows in candidates.items():
        kept, spellings = [], set()
        for _tier, _id, gloss in sorted(rows):
            spelling = gloss.replace(" ", "")
            if spelling not in spellings:
                spellings.add(spelling)
                kept.append(gloss)
            if len(kept) == MAX_GLOSSES:
                break
        glosses[key] = kept

    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / "akk-en-my.tsv.gz"
    with gzip.open(out, "wt", encoding="utf-8", compresslevel=9, newline="\n") as f:
        f.write("# Myanmar meanings from the AKK English-Myanmar dictionary. See LICENSE-AKK.txt.\n")
        for (word, pos) in sorted(glosses):
            f.write(f"G\t{word}\t{pos}\t{UNIT_SEPARATOR.join(glosses[(word, pos)])}\n")

    shutil.copyfile(licence, out_dir / "LICENSE-AKK.txt")
    words = len({word for word, _pos in glosses})
    print(f"{len(glosses)} word/part-of-speech records for {words} words, {patterns} rows read from patterns, "
          f"{dropped} rows dropped, {out.stat().st_size} bytes")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]))
