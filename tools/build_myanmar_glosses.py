"""Builds the Myanmar meanings Define shows, from the AKK English-Myanmar dictionary.

Usage:
    python tools/build_myanmar_glosses.py <dictionary.db> <LICENSE.txt> <output dir>

The inputs are the AKK dictionary's own database and licence
(https://github.com/aungkokomm/English-Myanmar-Dictionary-).

Writes <output dir>/akk-en-my.tsv.gz and <output dir>/LICENSE-AKK.txt.

The file is UTF-8 text, gzip-compressed, one record per line:

    G <tab> word <tab> pos <tab> gloss [<unit separator> gloss ...]

word is the lowercased single-word headword. pos is n, v, a or r, the letters
WordNet uses, so each gloss is found under the English meaning it translates.
AKK's own labels are mapped onto those four (adj -> a, adjs -> a, and so on);
a combined label such as "adj;adv" files the gloss under both, and rows with
any other label, or none, are left out.

Each (word, pos) keeps its first MAX_GLOSSES glosses in the AKK dictionary's
own order, which is the order the AKK app shows them in, after dropping rows
a one-glance popup cannot use: longer than MAX_LENGTH characters (these are
example sentences filed as glosses), or with fewer than two Myanmar letters
(stray numbering such as "၁၊").
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


def cleaned(definition: str) -> str:
    """One line, single-spaced, with the escaped slashes the AKK import left in."""
    return " ".join((definition or "").replace("\\/", "/").split())


def usable(gloss: str) -> bool:
    return 0 < len(gloss) <= MAX_LENGTH and len(MYANMAR_LETTER.findall(gloss)) >= 2


def main(db: Path, licence: Path, out_dir: Path) -> None:
    con = sqlite3.connect(f"file:{db.as_posix()}?mode=ro", uri=True)
    glosses = collections.defaultdict(list)  # (word, pos) -> [gloss]
    dropped = 0
    for headword, labels, definition in con.execute("select headword, pos, definition from entries order by id"):
        headword = (headword or "").strip()
        if not WORD.fullmatch(headword):
            continue

        gloss = cleaned(definition)
        if not usable(gloss):
            dropped += 1
            continue

        for label in (labels or "").split(";"):
            pos = POS.get(label.strip())
            if pos is None:
                continue
            kept = glosses[(headword.lower(), pos)]
            if gloss not in kept and len(kept) < MAX_GLOSSES:
                kept.append(gloss)

    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / "akk-en-my.tsv.gz"
    with gzip.open(out, "wt", encoding="utf-8", compresslevel=9, newline="\n") as f:
        f.write("# Myanmar meanings from the AKK English-Myanmar dictionary. See LICENSE-AKK.txt.\n")
        for (word, pos) in sorted(glosses):
            f.write(f"G\t{word}\t{pos}\t{UNIT_SEPARATOR.join(glosses[(word, pos)])}\n")

    shutil.copyfile(licence, out_dir / "LICENSE-AKK.txt")
    words = len({word for word, _pos in glosses})
    print(f"{len(glosses)} word/part-of-speech records for {words} words, {dropped} rows dropped, {out.stat().st_size} bytes")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]))
