"""Builds the Hindi meanings Define shows, from an English-Hindi word list typed in Kruti Dev.

Usage:
    python tools/build_hindi_glosses.py <Real Hindi.xlsx> <output dir>
    python tools/build_hindi_glosses.py <Real Hindi.xlsx> --check [sample size]

The word list is a spreadsheet of English words (column A) and Hindi meanings
(column B), comma-separated. Its Hindi is typed in Kruti Dev 010, a legacy
font that draws Devanagari over ordinary ASCII codes, so what the file holds
for पीछे is the text "ihNs". Unicode text has to be made from it:

  1. Each Kruti Dev code, longest first, becomes the Unicode it draws.
  2. A half letter followed by the stroke that completes it becomes the full
     letter: Kruti Dev types many letters as a half form plus "k".
  3. The short i sign, typed BEFORE its consonant because that is where it is
     drawn, moves after the consonant cluster it belongs to.
  4. The reph (र् drawn above), typed AFTER its syllable, moves in front of
     the consonant it sits over.
  5. Marks typed in drawing order are put in Unicode order: a nasal sign
     before a vowel sign (मंुह -> मुंह), a nukta after one (बढा़ -> बढ़ा).

Two habits of THIS file, found by checking it, not guessed:
  - "W", Kruti Dev's ॅ, is how it types chandrabindu, often with an anusvara
    as well (nkWar is दाँत); Hindi words have no use for a bare ॅ.
  - Excel turned the apostrophe into a curly one, so ’ stands for श where
    Kruti Dev would have ' (fu’kk is निशा).

--check converts everything, checks a set of known words, reports what did
not convert cleanly and how often each known fault still occurs, and prints a
random sample for a Hindi reader to judge.

Writes <output dir>/hindi-en-hi.tsv.gz, one record per line:

    H <tab> word <tab> meaning [<unit separator> meaning ...]
"""

import collections
import gzip
import random
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

MAX_MEANINGS = 4
UNIT_SEPARATOR = "\x1f"
WORD = re.compile(r"[A-Za-z][A-Za-z'\-]*")

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
CONSONANT = "[क-हळ]"
VOWEL_SIGNS = "ािीुूृॄेैोौॉ"
SIGNS = VOWEL_SIGNS + "ंँः़"
PLAIN = re.compile("[" + DEVANAGARI + r"\s,.;:()\-/?!{}=0-9]*")


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
    """Whether a converted meaning is plausible Devanagari rather than a conversion failure."""
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


def check(xlsx: Path, sample: int) -> None:
    print("known words:")
    failures = 0
    for kruti, expected in KNOWN:
        got = to_unicode(kruti)
        failures += got != expected
        print(f"  {'ok  ' if got == expected else 'FAIL'} {kruti!r:24} -> {got}  (expected {expected})")
    print(f"  {len(KNOWN) - failures} of {len(KNOWN)} right")

    rows = [(w, d) for w, d in read_rows(xlsx) if WORD.fullmatch(w) and d.strip()]
    leftovers = collections.Counter()
    examples = {}
    kept_total = rejected_total = 0
    converted = []
    everything = []
    for word, kruti in rows:
        everything.append(to_unicode(kruti))
        kept, rejected = meanings_of(kruti)
        kept_total += len(kept)
        rejected_total += len(rejected)
        for bad in rejected:
            odd = set(re.sub("[" + DEVANAGARI + r"\s,.;:()\-/?!{}=0-9]", "", bad)) or {"(a known fault)"}
            for ch in odd:
                leftovers[ch] += 1
                examples.setdefault(ch, (word, kruti, bad))
        if kept:
            converted.append((word, kruti, kept))

    print(f"\n{len(rows)} single-word rows, {len(converted)} with a usable meaning; "
          f"{kept_total} meanings kept, {rejected_total} rejected")
    print("why meanings were rejected, most common first:")
    for ch, n in leftovers.most_common(20):
        word, kruti, bad = examples[ch]
        print(f"  {ch!r:18} x{n:<5} e.g. {word}: {kruti!r} -> {bad}")

    text = "\n".join(everything)
    print("\nknown faults still in the converted text:")
    for name, pattern in FAULTS:
        hits = list(re.finditer(pattern, text))
        shown = [text[max(0, h.start() - 6):h.end() + 3].replace("\n", " / ") for h in hits[:3]]
        print(f"  {name:32} {len(hits):5}  {shown}")

    print(f"\nrandom sample of {sample}:")
    random.seed(11)
    for word, kruti, kept in random.sample(converted, min(sample, len(converted))):
        print(f"  {word:18} {' | '.join(kept)}")


def build(xlsx: Path, out_dir: Path) -> None:
    meanings = collections.defaultdict(list)
    for word, kruti in read_rows(xlsx):
        if not WORD.fullmatch(word):
            continue
        kept, _rejected = meanings_of(kruti)
        bucket = meanings[word.lower()]
        for meaning in kept:
            if meaning not in bucket and len(bucket) < MAX_MEANINGS:
                bucket.append(meaning)

    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / "hindi-en-hi.tsv.gz"
    with gzip.open(out, "wt", encoding="utf-8", compresslevel=9, newline="\n") as f:
        f.write("# Hindi meanings, converted from a Kruti Dev English-Hindi word list.\n")
        for word in sorted(meanings):
            if meanings[word]:
                f.write(f"H\t{word}\t{UNIT_SEPARATOR.join(meanings[word])}\n")
    print(f"{sum(1 for v in meanings.values() if v)} words, {out.stat().st_size} bytes")


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[2] == "--check":
        check(Path(sys.argv[1]), int(sys.argv[3]) if len(sys.argv) > 3 else 40)
    elif len(sys.argv) == 3:
        build(Path(sys.argv[1]), Path(sys.argv[2]))
    else:
        sys.exit(__doc__)
