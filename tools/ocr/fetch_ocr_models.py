"""Fetches the OCR models Ayaan PDF ships, checks them, and prepares them.

Run once on a build machine, before publishing:

    python tools/ocr/fetch_ocr_models.py

Writes PdfEditorApp/Assets/Ocr/:
  tessdata/eng.traineddata, hin.traineddata, mya.traineddata
      Tesseract's "fast" models. Fast rather than best: on this project's own
      benchmark the best models were no more accurate for English or Myanmar
      and 2 to 4 times slower, and only 0.1 points better for Hindi.
  myanmar-crnn-ocr.onnx
      mmpdfkit's Myanmar line model with its quantized convolutions turned back
      into float ones. As published, 98% of its time goes to ConvInteger, which
      ONNX Runtime has no fast x64 kernel for (60 s a page); converted, the text
      is identical and a page takes about 4 s. See onnx_convinteger_to_conv.py.
  LICENSE-*.txt and THIRD-PARTY-NOTICES-OCR.txt
      The licence texts, fetched from the projects themselves.

Every download is pinned by SHA-256, so a changed upstream file stops the
build here rather than shipping something nobody measured. Nothing is fetched
twice: files already present with the right hash are left alone.
"""
import hashlib
import pathlib
import sys
import urllib.request

sys.path.insert(0, str(pathlib.Path(__file__).parent))

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "PdfEditorApp" / "Assets" / "Ocr"

TESSDATA = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/"
MODELS = [
    ("tessdata/eng.traineddata", TESSDATA + "eng.traineddata",
     "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2"),
    ("tessdata/hin.traineddata", TESSDATA + "hin.traineddata",
     "4c73ffc59d497c186b19d1e90f5d721d678ea6b2e277b719bee4e2af12271825"),
    ("tessdata/mya.traineddata", TESSDATA + "mya.traineddata",
     "02aa6c25cfe9e583fa7b5d4131eac948f962308983f0f397df077dea58212b03"),
]

CRNN_URL = "https://huggingface.co/ksithu/myanmar-crnn-ocr/resolve/main/myanmar-crnn-ocr.onnx"
CRNN_SHA = "b656d957a93a4b7f7ddb254f28b082533752f647b950fc5af29b7e8a52893432"
CONVERTED_NAME = "myanmar-crnn-ocr.onnx"
CONVERTED_SHA = "2d79e892974b4966d80d7311cb288dbed9c76d453cc7f8f83e510768c9ddd5ee"

LICENSES = [
    ("LICENSE-tessdata_fast.txt", "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/LICENSE"),
    ("LICENSE-tesseract.txt", "https://raw.githubusercontent.com/tesseract-ocr/tesseract/main/LICENSE"),
    ("LICENSE-leptonica.txt", "https://raw.githubusercontent.com/DanBloomberg/leptonica/master/leptonica-license.txt"),
    ("LICENSE-mmpdfkit.txt", "https://raw.githubusercontent.com/kaungsithu/mmpdfkit/HEAD/LICENSE"),
    ("MODEL-CARD-myanmar-crnn-ocr.md", "https://huggingface.co/ksithu/myanmar-crnn-ocr/raw/main/README.md"),
]

NOTICE = """OCR components shipped with Ayaan PDF

Tesseract OCR engine 5.2, through the Tesseract NuGet package
(github.com/charlesw/tesseract).
    Apache License 2.0. See LICENSE-tesseract.txt.

Leptonica image library 1.82, which Tesseract uses.
    See LICENSE-leptonica.txt.

Microsoft Visual C++ runtime (msvcp140.dll, vcruntime140.dll,
vcruntime140_1.dll), which Tesseract's native libraries need.
    Microsoft redistributable files, shipped unchanged.

Tesseract "fast" models for English, Hindi and Myanmar
(github.com/tesseract-ocr/tessdata_fast).
    Apache License 2.0. See LICENSE-tessdata_fast.txt.

myanmar-crnn-ocr, the Myanmar line recognition model by Kaung Sithu
(huggingface.co/ksithu/myanmar-crnn-ocr).
    Apache License 2.0. See LICENSE-myanmar-crnn-ocr.txt and
    MODEL-CARD-myanmar-crnn-ocr.md.
    MODIFIED: its dynamically quantized convolutions (ConvInteger) were
    rewritten as the equivalent float convolutions, so it runs quickly on
    ONNX Runtime's CPU provider. Recognition output is unchanged.

The Myanmar line decoding and image preparation follow mmpdfkit by Kaung Sithu
(github.com/kaungsithu/mmpdfkit).
    MIT License. See LICENSE-mmpdfkit.txt.
"""


def sha256(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def fetch(url: str, dest: pathlib.Path) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    staging = dest.with_suffix(dest.suffix + ".part")
    with urllib.request.urlopen(url, timeout=120) as response, staging.open("wb") as f:
        while chunk := response.read(1 << 20):
            f.write(chunk)
    staging.replace(dest)


def fetch_pinned(url: str, dest: pathlib.Path, expected: str) -> None:
    if dest.exists() and sha256(dest) == expected:
        print(f"  have     {dest.relative_to(ROOT)}")
        return
    print(f"  fetching {dest.relative_to(ROOT)}")
    fetch(url, dest)
    actual = sha256(dest)
    if actual != expected:
        dest.unlink()
        raise SystemExit(f"{url}\n  expected sha256 {expected}\n  got             {actual}\nNot kept.")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)

    for name, url, expected in MODELS:
        fetch_pinned(url, OUT / name, expected)

    converted = OUT / CONVERTED_NAME
    if converted.exists() and sha256(converted) == CONVERTED_SHA:
        print(f"  have     {converted.relative_to(ROOT)}")
    else:
        original = OUT / "myanmar-crnn-ocr.int8.onnx"
        fetch_pinned(CRNN_URL, original, CRNN_SHA)
        print(f"  converting {original.name}")
        import subprocess
        subprocess.run([sys.executable, str(pathlib.Path(__file__).parent / "onnx_convinteger_to_conv.py"),
                        str(original), str(converted)], check=True)
        actual = sha256(converted)
        if actual != CONVERTED_SHA:
            converted.unlink()
            raise SystemExit(f"converted model sha256 {actual}, expected {CONVERTED_SHA}. Not kept.")
        original.unlink()

    for name, url in LICENSES:
        dest = OUT / name
        if not dest.exists():
            print(f"  fetching {dest.relative_to(ROOT)}")
            fetch(url, dest)
    # The model card names Apache 2.0; its full text is the same file tessdata ships.
    apache = OUT / "LICENSE-myanmar-crnn-ocr.txt"
    if not apache.exists():
        apache.write_bytes((OUT / "LICENSE-tessdata_fast.txt").read_bytes())
    (OUT / "THIRD-PARTY-NOTICES-OCR.txt").write_text(NOTICE, encoding="utf-8")

    print("OCR assets ready in", OUT.relative_to(ROOT))


if __name__ == "__main__":
    main()
