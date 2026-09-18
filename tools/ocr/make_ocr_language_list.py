"""Writes PdfEditorApp.Viewport/Ocr/OcrLanguageList.cs: the Tesseract
languages Recognize text can download, pinned to one tessdata_fast commit.

Each language carries its file's size and its git blob id (the SHA-1 of
"blob <size>" NUL then the bytes), both read from GitHub's tree listing for
that commit, so nothing has to be downloaded to build the list. The app checks
every download against both. The three languages that ship with the app match
their entries byte for byte (eng, hin, mya; checked with git hash-object).

Run again only to move to a newer tessdata_fast commit:

    python tools/ocr/make_ocr_language_list.py
"""

import json
import pathlib
import urllib.request

COMMIT = "87416418657359cb625c412a48b6e1d6d41c29bd"  # tessdata_fast, 2024-08-01
TREE = f"https://api.github.com/repos/tesseract-ocr/tessdata_fast/git/trees/{COMMIT}?recursive=1"
OUT = pathlib.Path(__file__).resolve().parents[2] / "PdfEditorApp.Viewport" / "Ocr" / "OcrLanguageList.cs"

# code: (English name, native name, script, BCP 47 language tags it answers to)
#
# Left out: eng, hin and mya (they ship with the app); osd and equ (not
# languages); frk (a link to deu_latf in the repository); the script/ models;
# and Chinese, Japanese and Korean, whose recognised text needs a font Windows
# only has as a collection (.ttc), which a PDF cannot embed.
NAMES = {
    "afr": ("Afrikaans", "Afrikaans", "Latin", "af"),
    "amh": ("Amharic", "አማርኛ", "Ethiopic", "am"),
    "ara": ("Arabic", "العربية", "Arabic", "ar"),
    "asm": ("Assamese", "অসমীয়া", "Bengali", "as"),
    "aze": ("Azerbaijani", "Azərbaycan", "Latin", "az"),
    "aze_cyrl": ("Azerbaijani (Cyrillic)", "Азәрбајҹан", "Cyrillic", "az"),
    "bel": ("Belarusian", "Беларуская", "Cyrillic", "be"),
    "ben": ("Bengali", "বাংলা", "Bengali", "bn"),
    "bod": ("Tibetan", "བོད་ཡིག", "Tibetan", "bo"),
    "bos": ("Bosnian", "Bosanski", "Latin", "bs"),
    "bre": ("Breton", "Brezhoneg", "Latin", "br"),
    "bul": ("Bulgarian", "Български", "Cyrillic", "bg"),
    "cat": ("Catalan", "Català", "Latin", "ca"),
    "ceb": ("Cebuano", "Cebuano", "Latin", "ceb"),
    "ces": ("Czech", "Čeština", "Latin", "cs"),
    "chr": ("Cherokee", "ᏣᎳᎩ", "Cherokee", "chr"),
    "cos": ("Corsican", "Corsu", "Latin", "co"),
    "cym": ("Welsh", "Cymraeg", "Latin", "cy"),
    "dan": ("Danish", "Dansk", "Latin", "da"),
    "deu": ("German", "Deutsch", "Latin", "de"),
    "deu_latf": ("German (Fraktur)", "Deutsch (Fraktur)", "Latin", ""),
    "div": ("Dhivehi", "ދިވެހި", "Thaana", "dv"),
    "dzo": ("Dzongkha", "རྫོང་ཁ", "Tibetan", "dz"),
    "ell": ("Greek", "Ελληνικά", "Greek", "el"),
    "enm": ("Middle English", "", "Latin", ""),
    "epo": ("Esperanto", "Esperanto", "Latin", "eo"),
    "est": ("Estonian", "Eesti", "Latin", "et"),
    "eus": ("Basque", "Euskara", "Latin", "eu"),
    "fao": ("Faroese", "Føroyskt", "Latin", "fo"),
    "fas": ("Persian", "فارسی", "Arabic", "fa"),
    "fil": ("Filipino", "Filipino", "Latin", "fil"),
    "fin": ("Finnish", "Suomi", "Latin", "fi"),
    "fra": ("French", "Français", "Latin", "fr"),
    "frm": ("Middle French", "", "Latin", ""),
    "fry": ("Western Frisian", "Frysk", "Latin", "fy"),
    "gla": ("Scottish Gaelic", "Gàidhlig", "Latin", "gd"),
    "gle": ("Irish", "Gaeilge", "Latin", "ga"),
    "glg": ("Galician", "Galego", "Latin", "gl"),
    "grc": ("Ancient Greek", "Ἑλληνική", "Greek", ""),
    "guj": ("Gujarati", "ગુજરાતી", "Gujarati", "gu"),
    "hat": ("Haitian Creole", "Kreyòl ayisyen", "Latin", "ht"),
    "heb": ("Hebrew", "עברית", "Hebrew", "he"),
    "hrv": ("Croatian", "Hrvatski", "Latin", "hr"),
    "hun": ("Hungarian", "Magyar", "Latin", "hu"),
    "hye": ("Armenian", "Հայերեն", "Armenian", "hy"),
    "iku": ("Inuktitut", "ᐃᓄᒃᑎᑐᑦ", "CanadianSyllabics", "iu"),
    "ind": ("Indonesian", "Bahasa Indonesia", "Latin", "id"),
    "isl": ("Icelandic", "Íslenska", "Latin", "is"),
    "ita": ("Italian", "Italiano", "Latin", "it"),
    "ita_old": ("Old Italian", "", "Latin", ""),
    "jav": ("Javanese", "Basa Jawa", "Latin", "jv"),
    "kan": ("Kannada", "ಕನ್ನಡ", "Kannada", "kn"),
    "kat": ("Georgian", "ქართული", "Georgian", "ka"),
    "kat_old": ("Old Georgian", "", "Georgian", ""),
    "kaz": ("Kazakh", "Қазақ", "Cyrillic", "kk"),
    "khm": ("Khmer", "ខ្មែរ", "Khmer", "km"),
    "kir": ("Kyrgyz", "Кыргызча", "Cyrillic", "ky"),
    "kmr": ("Kurdish (Kurmanji)", "Kurmancî", "Latin", "ku|kmr"),
    "lao": ("Lao", "ລາວ", "Lao", "lo"),
    "lat": ("Latin", "Latina", "Latin", "la"),
    "lav": ("Latvian", "Latviešu", "Latin", "lv"),
    "lit": ("Lithuanian", "Lietuvių", "Latin", "lt"),
    "ltz": ("Luxembourgish", "Lëtzebuergesch", "Latin", "lb"),
    "mal": ("Malayalam", "മലയാളം", "Malayalam", "ml"),
    "mar": ("Marathi", "मराठी", "Devanagari", "mr"),
    "mkd": ("Macedonian", "Македонски", "Cyrillic", "mk"),
    "mlt": ("Maltese", "Malti", "Latin", "mt"),
    "mon": ("Mongolian", "Монгол", "Cyrillic", "mn"),
    "mri": ("Maori", "Te Reo Māori", "Latin", "mi"),
    "msa": ("Malay", "Bahasa Melayu", "Latin", "ms"),
    "nep": ("Nepali", "नेपाली", "Devanagari", "ne"),
    "nld": ("Dutch", "Nederlands", "Latin", "nl"),
    "nor": ("Norwegian", "Norsk", "Latin", "no|nb|nn"),
    "oci": ("Occitan", "Occitan", "Latin", "oc"),
    "ori": ("Odia", "ଓଡ଼ିଆ", "Oriya", "or"),
    "pan": ("Punjabi", "ਪੰਜਾਬੀ", "Gurmukhi", "pa"),
    "pol": ("Polish", "Polski", "Latin", "pl"),
    "por": ("Portuguese", "Português", "Latin", "pt"),
    "pus": ("Pashto", "پښتو", "Arabic", "ps"),
    "que": ("Quechua", "Runa Simi", "Latin", "qu"),
    "ron": ("Romanian", "Română", "Latin", "ro"),
    "rus": ("Russian", "Русский", "Cyrillic", "ru"),
    "san": ("Sanskrit", "संस्कृतम्", "Devanagari", "sa"),
    "sin": ("Sinhala", "සිංහල", "Sinhala", "si"),
    "slk": ("Slovak", "Slovenčina", "Latin", "sk"),
    "slv": ("Slovenian", "Slovenščina", "Latin", "sl"),
    "snd": ("Sindhi", "سنڌي", "Arabic", "sd"),
    "spa": ("Spanish", "Español", "Latin", "es"),
    "spa_old": ("Old Spanish", "", "Latin", ""),
    "sqi": ("Albanian", "Shqip", "Latin", "sq"),
    "srp": ("Serbian", "Српски", "Cyrillic", "sr"),
    "srp_latn": ("Serbian (Latin)", "Srpski", "Latin", "sr"),
    "sun": ("Sundanese", "Basa Sunda", "Latin", "su"),
    "swa": ("Swahili", "Kiswahili", "Latin", "sw"),
    "swe": ("Swedish", "Svenska", "Latin", "sv"),
    "syr": ("Syriac", "ܣܘܪܝܝܐ", "Syriac", "syr"),
    "tam": ("Tamil", "தமிழ்", "Tamil", "ta"),
    "tat": ("Tatar", "Татар", "Cyrillic", "tt"),
    "tel": ("Telugu", "తెలుగు", "Telugu", "te"),
    "tgk": ("Tajik", "Тоҷикӣ", "Cyrillic", "tg"),
    "tha": ("Thai", "ไทย", "Thai", "th"),
    "tir": ("Tigrinya", "ትግርኛ", "Ethiopic", "ti"),
    "ton": ("Tongan", "Lea faka-Tonga", "Latin", "to"),
    "tur": ("Turkish", "Türkçe", "Latin", "tr"),
    "uig": ("Uyghur", "ئۇيغۇرچە", "Arabic", "ug"),
    "ukr": ("Ukrainian", "Українська", "Cyrillic", "uk"),
    "urd": ("Urdu", "اردو", "Arabic", "ur"),
    "uzb": ("Uzbek", "Oʻzbek", "Latin", "uz"),
    "uzb_cyrl": ("Uzbek (Cyrillic)", "Ўзбек", "Cyrillic", "uz"),
    "vie": ("Vietnamese", "Tiếng Việt", "Latin", "vi"),
    "yid": ("Yiddish", "ייִדיש", "Hebrew", "yi"),
    "yor": ("Yoruba", "Yorùbá", "Latin", "yo"),
}


def cs(text: str) -> str:
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def main() -> None:
    with urllib.request.urlopen(TREE) as response:
        tree = json.load(response)
    if tree.get("truncated"):
        raise SystemExit("the tree listing came back truncated")
    files = {
        entry["path"][: -len(".traineddata")]: entry
        for entry in tree["tree"]
        if entry["type"] == "blob" and "/" not in entry["path"] and entry["path"].endswith(".traineddata")
    }

    missing = sorted(set(NAMES) - set(files))
    if missing:
        raise SystemExit(f"not in tessdata_fast at {COMMIT}: {missing}")

    rows = []
    for code, (name, native, script, tags) in sorted(NAMES.items(), key=lambda kv: kv[1][0]):
        entry = files[code]
        rows.append(
            f"        new({cs(code)}, {cs(name)}, {cs(native)}, {cs(script)}, {cs(tags)}, "
            f"{entry['size']}, {cs(entry['sha'])}),"
        )

    OUT.write_text(
        "// Written by tools/ocr/make_ocr_language_list.py. Do not edit by hand.\n"
        "namespace PdfEditorApp.Viewport;\n\n"
        "/// <summary>\n"
        "/// The Tesseract languages Recognize text can download, from tessdata_fast\n"
        f"/// at commit {COMMIT[:7]}, with each file's exact size and git blob id.\n"
        "/// </summary>\n"
        "public static class OcrLanguageList\n"
        "{\n"
        f"    public const string Commit = {cs(COMMIT)};\n\n"
        "    /// <summary>Where a language's file is, with its code and \".traineddata\" after it.</summary>\n"
        f"    public const string Source = \"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/{COMMIT}/\";\n\n"
        "    public static readonly OcrLanguage[] All =\n"
        "    [\n"
        + "\n".join(rows)
        + "\n    ];\n"
        "}\n",
        encoding="utf-8",
        newline="\n",
    )
    print(f"{len(rows)} languages written to {OUT}")


if __name__ == "__main__":
    main()
