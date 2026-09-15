"""Builds the Hindi meanings Define shows, from the user's own English-Hindi dictionary.

Usage:
    python tools/build_hindi_glosses.py <English-Hindi Dictionary.csv> <Real Hindi.xlsx> <output dir>
    python tools/build_hindi_glosses.py --check-kruti <Real Hindi.xlsx> [sample size]

THE SOURCE is the CSV: columns eword, hword, egrammar, one meaning per row,
already Unicode Devanagari. It is far more complete than the older
spreadsheet (38,000 words against 22,000), and it has parts of speech, so
each Hindi line sits under the English meaning it translates, like Myanmar.

Its rows are not in order of importance ("book" lists ढेर and नियमावली before
पुस्तक), so each word's meanings are RANKED: meanings the older spreadsheet
also gives for that word come first, since the user checked that list, then
the CSV's own order. The older spreadsheet is typed in Kruti Dev 010 and is
converted to Unicode for that comparison; it contributes no meanings of its
own.

Cleaning:
  - part-of-speech labels are mapped onto n, v, a, r (TransitiveVerb, a stray
    lower-case "noun", "Adjective" with a quote after it); rows labelled
    anything else, and rows whose English is not a single word, are left out;
  - a meaning with Latin letters, a replacement character, or marks out of
    Unicode order is dropped; whitespace and line breaks inside a meaning
    collapse to single spaces;
  - ट्र is corrected to त्र where the CSV holds an old conversion slip
    (मिट्र, नियंट्रित): only when the त्र spelling of that same meaning occurs
    elsewhere in either list, so a real ट्र (राष्ट्र, ट्रेन) is never touched;
  - a meaning repeated with different spacing is kept once, and each word and
    part of speech keeps at most MAX_MEANINGS.

Writes <output dir>/hindi-en-hi.tsv.gz, one record per line:

    H <tab> word <tab> pos <tab> meaning [<unit separator> meaning ...]

--check-kruti checks the Kruti Dev conversion against known words and prints a
sample, for when that spreadsheet changes.

HOW KRUTI DEV BECOMES UNICODE. Kruti Dev draws Devanagari over ordinary ASCII
codes, so what the spreadsheet holds for पीछे is "ihNs":
  1. Each Kruti Dev code, longest first, becomes the Unicode it draws.
  2. A half letter followed by the stroke that completes it becomes the full
     letter: Kruti Dev types many letters as a half form plus "k".
  3. The short i sign, typed BEFORE its consonant because that is where it is
     drawn, moves after the consonant cluster it belongs to.
  4. The reph (र् drawn above), typed AFTER its syllable, moves in front of
     the consonant it sits over.
  5. Marks typed in drawing order are put in Unicode order: a nasal sign
     before a vowel sign (मंुह -> मुंह), a nukta after one (बढा़ -> बढ़ा).
Two habits of that spreadsheet, found by checking it, not guessed:
  - "W", Kruti Dev's ॅ, is how it types chandrabindu, often with an anusvara
    as well (nkWar is दाँत).
  - Excel turned the apostrophe into a curly one, so ’ stands for श where
    Kruti Dev would have ' (fu’kk is निशा).
"""

import collections
import csv
import gzip
import random
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

MAX_MEANINGS = 3
UNIT_SEPARATOR = "\x1f"
WORD = re.compile(r"[A-Za-z][A-Za-z'\-]*")

POS = {
    "noun": "n",
    "verb": "v", "transitiveverb": "v", "intransitiveverb": "v", "phrasalverb": "v",
    "adjective": "a", 'adjective"': "a",
    "adverb": "r",
}

TTA_RA = "ट्र"
TA_RA = "त्र"

I_SIGN = "\x01"   # stands in for ि until it is moved
REPH = "\x02"     # stands in for र् until it is moved

KRUTI_DEV = [
    # Independent vowels
    ("v‚", "ऑ"), ("vks", "ओ"), ("vkS", "औ"), ("vk", "आ"), ("v", "अ"),
    ("b±", "ईं"), ("bZ", "ई"), ("b", "इ"), ("m", "उ"), ("Å", "ऊ"),
    (",s", "ऐ"), (",", "ए"), ("_", "ऋ"),
    # Consonants, full and half
    ("d", "क"), ("D", "क्"), ("[k", "ख"), ("[", "ख्"), ("x", "ग"), ("X", "ग्"),
    ("?k", "घ"), ("?", "घ्"), ("³", "ङ"),
    ("p", "च"), ("P", "च्"), ("N", "छ"), ("t", "ज"), ("T", "ज्"),
    (">k", "झ"), (">", "झ्"), ("¥", "ञ"),
    ("V", "ट"), ("B", "ठ"), ("M", "ड"), ("<", "ढ"), (".k", "ण"), (".", "ण्"),
    ("r", "त"), ("R", "त्"), ("Fk", "थ"), ("F", "थ्"), ("n", "द"),
    ("/k", "ध"), ("/", "ध्"), ("èk", "ध"), ("è", "ध्"), ("u", "न"), ("U", "न्"),
    ("i", "प"), ("I", "प्"), ("Q", "फ"), ("¶", "फ्"), ("c", "ब"), ("C", "ब्"),
    ("Hk", "भ"), ("H", "भ्"), ("e", "म"), ("E", "म्"),
    (";", "य"), ("¸", "य्"), ("j", "र"), ("y", "ल"), ("Y", "ल्"), ("G", "ळ"),
    ("o", "व"), ("O", "व्"),
    ("'k", "श"), ("'", "श्"), ("’k", "श"), ("’", "श्"),
    ('"k', "ष"), ('"', "ष्"),
    ("l", "स"), ("L", "स्"), ("g", "ह"),
    # Conjuncts drawn as one glyph
    ("{k", "क्ष"), ("{", "क्ष्"), ("=", "त्र"), ("«", "त्र्"), ("K", "ज्ञ"), ("J", "श्र"),
    ("|", "द्य"), ("}", "द्व"), ("Ø", "क्र"), ("æ", "द्र"), ("ç", "प्र"), ("Ý", "फ्र"),
    ("#", "रु"), (":", "रू"), ("Ù", "त्त्"), ("ä", "क्त"), ("–", "दृ"), ("—", "कृ"),
    ("é", "न्न"), ("™", "न्न्"), (")", "द्ध"),
    ("à", "ह्न"), ("á", "ह्य"), ("â", "हृ"), ("ã", "ह्म"), ("º", "ह्"),
    ("í", "द्द"), ("ì", "ड्ड"), ("ï", "ड्ढ"), ("ê", "ट्ट"), ("ë", "ट्ठ"),
    ("î", "्य"), ("ª", "्र"),
    # Vowel signs and marks
    ("ks", "ो"), ("kS", "ौ"), ("k", "ा"), ("f", I_SIGN), ("h", "ी"), ("È", "ीं"),
    ("q", "ु"), ("w", "ू"), ("`", "ृ"), ("s", "े"), ("S", "ै"),
    ("a", "ं"), ("¡", "ँ"), ("%", "ः"), ("W", "ँ"), ("‚", "ॉ"),
    ("~", "्"), ("+", "़"), ("z", "्र"), ("Z", REPH),
    # Punctuation and digits
    ("]", ","), ("A", "।"), ("-", "."), ("¼", "("), ("½", ")"), ("&", "-"), ("@", "/"),
    ("(", ";"), ("¿", "{"), ("À", "}"), ("¾", "="), ("ñ", "॰"), ("Œ", "॰"), ("\\", "?"),
    ("å", "०"), ("ƒ", "१"), ("„", "२"), ("…", "३"), ("†", "४"),
    ("‡", "५"), ("ˆ", "६"), ("‰", "७"), ("Š", "८"), ("‹", "९"),
]
LONGEST = max(len(k) for k, _ in KRUTI_DEV)
TABLE = dict(KRUTI_DEV)

DEVANAGARI = chr(0x0900) + "-" + chr(0x097F)
JOINERS = chr(0x200C) + chr(0x200D)
CONSONANT = "[क-हळ]"
VOWEL_SIGNS = "ािीुूृॄेैोौॉ"
SIGNS = VOWEL_SIGNS + "ंँः़"
PLAIN_CHARACTERS = "[" + DEVANAGARI + JOINERS + r"\s,.;:()\[\]\-/?!{}=0-9]"
PLAIN = re.compile(PLAIN_CHARACTERS + "*")


def to_unicode(kruti: str) -> str:
    out, i = [], 0
    while i < len(kruti):
        for size in range(min(LONGEST, len(kruti) - i), 0, -1):
            piece = kruti[i:i + size]
            if piece in TABLE:
                out.append(TABLE[piece])
                i += size
                break
        else:
            out.append(kruti[i])
            i += 1
    text = "".join(out)

    # A half letter completed by the stroke Kruti Dev types after it.
    text = re.sub("्([ाोौ])", lambda m: "" if m.group(1) == "ा" else m.group(1), text)

    # The i sign goes after the whole consonant cluster that follows it.
    text = re.sub(I_SIGN + "((?:" + CONSONANT + "़?्)*" + CONSONANT + "़?)", r"\1ि", text)
    text = text.replace(I_SIGN, "ि")

    # The reph goes before the consonant (cluster) it is drawn over.
    text = re.sub("((?:" + CONSONANT + "्)*" + CONSONANT + "़?[" + SIGNS + "]*)" + REPH, r"र्\1", text)
    text = text.replace(REPH, "र्")

    # Marks into Unicode order: one nasal sign, after the vowel sign; nukta
    # before the vowel sign.
    text = re.sub("ँं|ंँ", "ँ", text)
    text = re.sub("([ंँ]+)([" + VOWEL_SIGNS + "]+)", r"\2\1", text)
    text = re.sub("([" + VOWEL_SIGNS + "]+)़", r"़\1", text)

    # The same vowel sign typed more than once, a slip the file makes (देेेना).
    text = re.sub("([" + VOWEL_SIGNS + "])\\1+", r"\1", text)
    return text


FAULTS = [
    ("bare ॅ", "ॅ"),
    ("nasal sign before a vowel sign", "[ंँ][" + VOWEL_SIGNS + "]"),
    ("nukta after a vowel sign", "[" + VOWEL_SIGNS + "]़"),
    ("half त्र before a consonant", "त्र्(?=[क-ह])"),
    ("two vowel signs in a row", "[" + VOWEL_SIGNS + "]{2}"),
]


def clean(text: str) -> bool:
    """Whether a meaning is plausible Unicode Devanagari rather than a conversion failure."""
    return (bool(re.search("[क-ह]", text))
            and PLAIN.fullmatch(text) is not None
            and not re.match("[" + SIGNS + "्]", text)
            and not any(re.search(pattern, text) for _name, pattern in FAULTS))


def read_rows(xlsx: Path):
    m = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
    z = zipfile.ZipFile(xlsx)
    shared = []
    if "xl/sharedStrings.xml" in z.namelist():
        for si in ET.fromstring(z.read("xl/sharedStrings.xml")).findall(m + "si"):
            shared.append("".join(t.text or "" for t in si.iter(m + "t")))
    sheet = ET.fromstring(z.read("xl/worksheets/sheet1.xml"))
    for row in sheet.find(m + "sheetData").findall(m + "row")[1:]:
        cells = {}
        for c in row.findall(m + "c"):
            v = c.find(m + "v")
            if c.get("t") == "s" and v is not None:
                value = shared[int(v.text)]
            elif c.get("t") == "inlineStr":
                value = "".join(t.text or "" for t in c.iter(m + "t"))
            else:
                value = v.text if v is not None else ""
            cells[re.sub(r"\d", "", c.get("r"))] = value or ""
        yield cells.get("A", "").strip(), cells.get("B", "")


def meanings_of(kruti: str) -> tuple[list[str], list[str]]:
    kept, rejected = [], []
    for part in to_unicode(kruti).split(","):
        meaning = " ".join(part.split()).strip(" .;")
        if not meaning:
            continue
        if clean(meaning):
            if meaning not in kept:
                kept.append(meaning)
        else:
            rejected.append(meaning)
    return kept, rejected


KNOWN = [
    ("ihNs", "पीछे"), ("R;kx nsuk", "त्याग देना"), ("NksM+ nsuk", "छोड़ देना"),
    ("uhpk fn[kkuk", "नीचा दिखाना"), ("vour djuk", "अवनत करना"), ("inkour djuk", "पदावनत करना"),
    ("yfTtr dj nsuk", "लज्जित कर देना"), ("de djuk", "कम करना"), ("?kVuk", "घटना"),
    ("NwV", "छूट"), ("dkVuk", "काटना"), ("dlkbZ [kkuk", "कसाई खाना"), ("cwpM+[kkuk", "बूचड़खाना"),
    ("vik{k", "अपाक्ष"), ("pfdr", "चकित"), ("dk;Z", "कार्य"), ("/keZ", "धर्म"), ("fgUnh", "हिन्दी"),
    (";k=k", "यात्रा"), ("i=", "पत्र"), ("fe=", "मित्र"), ("iq=h", "पुत्री"), ("jkf=", "रात्रि"),
    ("fu’kk", "निशा"), ("vk’kadk", "आशंका"), ("nkWar", "दाँत"), ("xkWao", "गाँव"), ("eaqg", "मुंह"),
    ("pUnzek", "चन्द्रमा"), ("fo|kFkhZ", "विद्यार्थी"), ("ehBk", "मीठा"), ("c<k+uk", "बढ़ाना"),
]


def check_kruti(xlsx: Path, sample: int) -> None:
    print("known words:")
    failures = 0
    for kruti, expected in KNOWN:
        got = to_unicode(kruti)
        failures += got != expected
        print(f"  {'ok  ' if got == expected else 'FAIL'} {kruti!r:24} -> {got}  (expected {expected})")
    print(f"  {len(KNOWN) - failures} of {len(KNOWN)} right")

    converted = []
    for word, kruti in read_rows(xlsx):
        kept, _rejected = meanings_of(kruti)
        if WORD.fullmatch(word) and kept:
            converted.append((word, kept))
    print(f"\n{len(converted)} words with a usable meaning; random sample of {sample}:")
    random.seed(11)
    for word, kept in random.sample(converted, min(sample, len(converted))):
        print(f"  {word:18} {' | '.join(kept)}")


def build(csv_path: Path, xlsx: Path, out_dir: Path) -> None:
    # What the user-checked spreadsheet says each word means, to rank by.
    verified = collections.defaultdict(set)
    for word, kruti in read_rows(xlsx):
        if WORD.fullmatch(word):
            verified[word.lower()].update(meanings_of(kruti)[0])

    candidates = collections.defaultdict(list)  # (word, pos) -> [(row, meaning)]
    dropped = collections.Counter()
    with open(csv_path, encoding="utf-8-sig", newline="") as f:
        rows = csv.reader(f)
        next(rows)
        for order, row in enumerate(rows):
            if len(row) < 3:
                dropped["short row"] += 1
                continue
            word = row[0].strip().lower()
            pos = POS.get(row[2].strip().lower())
            meaning = " ".join(row[1].split()).strip(" .;,:")
            if not WORD.fullmatch(word):
                dropped["not one English word"] += 1
            elif pos is None:
                dropped["another part of speech"] += 1
            elif not meaning or not clean(meaning):
                dropped["unusable meaning"] += 1
            else:
                candidates[(word, pos)].append((order, meaning))

    spelled = {m for items in candidates.values() for _o, m in items}
    spelled |= {m for meanings in verified.values() for m in meanings}
    repaired = 0

    def corrected(meaning: str) -> str:
        nonlocal repaired
        if TTA_RA in meaning and meaning.replace(TTA_RA, TA_RA) in spelled:
            repaired += 1
            return meaning.replace(TTA_RA, TA_RA)
        return meaning

    glosses = {}
    for (word, pos), items in candidates.items():
        good = verified.get(word, set())
        fixed = [(order, corrected(meaning)) for order, meaning in items]
        kept, spellings = [], set()
        for _order, meaning in sorted(fixed, key=lambda item: (item[1] not in good, item[0])):
            spelling = meaning.replace(" ", "")
            if spelling in spellings:
                continue
            spellings.add(spelling)
            kept.append(meaning)
            if len(kept) == MAX_MEANINGS:
                break
        glosses[(word, pos)] = kept

    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / "hindi-en-hi.tsv.gz"
    with gzip.open(out, "wt", encoding="utf-8", compresslevel=9, newline="\n") as f:
        f.write("# Hindi meanings from the user's own English-Hindi dictionary.\n")
        for (word, pos) in sorted(glosses):
            f.write(f"H\t{word}\t{pos}\t{UNIT_SEPARATOR.join(glosses[(word, pos)])}\n")

    words = len({word for word, _pos in glosses})
    print(f"{len(glosses)} word/part-of-speech records for {words} words, "
          f"{repaired} ट्र slips corrected, dropped rows: {dict(dropped)}, {out.stat().st_size} bytes")


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "--check-kruti":
        check_kruti(Path(sys.argv[2]), int(sys.argv[3]) if len(sys.argv) > 3 else 40)
    elif len(sys.argv) == 4:
        build(Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]))
    else:
        sys.exit(__doc__)
