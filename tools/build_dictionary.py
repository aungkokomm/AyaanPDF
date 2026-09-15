"""Builds the offline English dictionary Define reads, from Princeton WordNet 3.0.

Usage:
    python tools/build_dictionary.py <WNdb dict dir> <folder holding the *.exc lists> <output dir>

Inputs are the two archives from https://wordnetcode.princeton.edu/3.0/:
WNdb-3.0.tar.gz for data.* / index.* / index.sense, and WordNet-3.0.tar.gz
for noun.exc, verb.exc, adj.exc and adv.exc, which WNdb does not carry.

Writes <output dir>/wordnet-en.tsv.gz and <output dir>/LICENSE-WordNet.txt.

The file is UTF-8 text, gzip-compressed, one record per line:

    D <tab> lemma <tab> pos <tab> definition [<unit separator> definition ...]
    X <tab> pos <tab> inflected form <tab> base form

pos is n, v, a or r. A lemma's D lines are written most-used part of speech
first (summed WordNet tag counts), and each carries its first MAX_SENSES
senses in WordNet's own frequency order, definition only, without examples.
Multi-word lemmas are left out: Define looks up one selected word.
"""

import collections
import gzip
import re
import sys
from pathlib import Path

POS_FILES = [("n", "noun"), ("v", "verb"), ("a", "adj"), ("r", "adv")]

# index.sense sense keys name the part of speech by number; 5 is a satellite
# adjective, which lives in data.adj with the others.
SS_TYPE = {"1": "n", "2": "v", "3": "a", "4": "r", "5": "a"}

MAX_SENSES = 3
UNIT_SEPARATOR = "\x1f"


def licence_text(data_noun: Path) -> str:
    """The licence WordNet requires on every copy, read out of the data file's own header."""
    lines = []
    for raw in data_noun.read_text(encoding="latin-1").splitlines():
        if not raw.startswith("  "):
            break
        lines.append(re.sub(r"^\s+\d+ ?", "", raw).rstrip())
    return "\n".join(lines).strip() + "\n"


def definition_of(record: str) -> str:
    """The gloss's definition, without the quoted examples that follow it."""
    gloss = record.split(" | ", 1)[1].strip()
    return gloss.split('; "', 1)[0].strip().rstrip(";").strip()


def main(dict_dir: Path, exc_dir: Path, out_dir: Path) -> None:
    tag_counts = collections.Counter()
    for line in (dict_dir / "index.sense").read_text(encoding="latin-1").splitlines():
        key, _offset, _sense, tags = line.split()
        lemma, rest = key.split("%", 1)
        tag_counts[(lemma, SS_TYPE[rest[0]])] += int(tags)

    blocks = collections.defaultdict(list)  # lemma -> [(tags, pos order, pos, definitions)]
    for order, (pos, name) in enumerate(POS_FILES):
        data = (dict_dir / f"data.{name}").read_bytes()
        for line in (dict_dir / f"index.{name}").read_text(encoding="latin-1").splitlines():
            if line.startswith("  "):
                continue
            fields = line.split()
            lemma = fields[0]
            if "_" in lemma:
                continue

            synset_count = int(fields[2])
            pointer_count = int(fields[3])
            first_offset = 4 + pointer_count + 2
            offsets = fields[first_offset:first_offset + synset_count]

            definitions = []
            for offset in offsets[:MAX_SENSES]:
                at = int(offset)
                record = data[at:data.index(b"\n", at)].decode("latin-1")
                definitions.append(definition_of(record))

            blocks[lemma].append((tag_counts[(lemma, pos)], order, pos, definitions))

    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / "wordnet-en.tsv.gz"
    written = 0
    with gzip.open(out, "wt", encoding="utf-8", compresslevel=9, newline="\n") as f:
        f.write("# English definitions from Princeton WordNet 3.0. See LICENSE-WordNet.txt.\n")
        for lemma in sorted(blocks):
            for _tags, _order, pos, definitions in sorted(blocks[lemma], key=lambda b: (-b[0], b[1])):
                f.write(f"D\t{lemma}\t{pos}\t{UNIT_SEPARATOR.join(definitions)}\n")
                written += 1

        exceptions = 0
        for pos, name in POS_FILES:
            for line in (exc_dir / f"{name}.exc").read_text(encoding="latin-1").splitlines():
                parts = line.split()
                if len(parts) < 2 or "_" in parts[0]:
                    continue
                for base in parts[1:]:
                    if "_" not in base:
                        f.write(f"X\t{pos}\t{parts[0]}\t{base}\n")
                        exceptions += 1

    (out_dir / "LICENSE-WordNet.txt").write_text(licence_text(dict_dir / "data.noun"), encoding="utf-8", newline="\n")
    print(f"{written} definition records for {len(blocks)} words, {exceptions} irregular forms, {out.stat().st_size} bytes")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]))
