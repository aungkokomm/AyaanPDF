"""Writes PdfEditorApp/THIRD-PARTY-NOTICES.txt from the licence files of what ships.

Every text in the output is copied from a licence file that came with the
component: PDFium's from the pdfium-binaries release the vendored pdfium.dll
came from (render_core/vendor/pdfium/licenses), each Rust crate's from its
package in the cargo registry, each NuGet package's from the package itself,
and the .NET runtime's from its runtime pack. Nothing is written from memory.

Covered here: pdfium.dll, render_core.dll (and the crates compiled into it),
the .NET and Windows App SDK libraries, and the .NET runtime. Fonts and icons,
text recognition and the dictionaries keep their own notices beside them.

Run from the repository root after changing a dependency:

    python tools/make_third_party_notices.py
"""

import json
import os
import re
import subprocess
import sys
import textwrap
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "PdfEditorApp" / "THIRD-PARTY-NOTICES.txt"
PDFIUM = ROOT / "render_core" / "vendor" / "pdfium"
NUGET = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))
BACKSLASH = chr(92)

LICENCE_NAME = re.compile(r"^(licen[cs]e|copying|unlicense|notice|third.?party|thirdpartynotices)", re.I)


def read(path: Path) -> str:
    text = path.read_bytes().decode("utf-8", errors="replace").replace("\r\n", "\n").replace("\r", "\n")
    return text.strip("\n").rstrip() + "\n"


class Notices:
    """Components grouped under identical licence texts, in first-seen order."""

    def __init__(self):
        self.sections = []

    def section(self, title, intro):
        entries = {}
        self.sections.append((title, intro, entries))
        return entries


def add(entries, text, component):
    entries.setdefault(text, []).append(component)


# ---------------- PDFium ----------------

def pdfium(notices):
    entries = notices.section(
        "PDFium (pdfium.dll)",
        "PDFium renders and edits the pages. This build is pdfium-binaries' "
        "chromium/7961 (PDFium 152.0.7961.0), and it includes the libraries listed "
        "with it below.")
    licences = PDFIUM / "licenses"
    if not licences.is_dir():
        sys.exit(f"missing {licences}: unpack the pdfium-binaries release's licenses folder there")
    add(entries, read(licences / "pdfium.txt"), "PDFium")
    for f in sorted(licences.iterdir()):
        if f.name != "pdfium.txt":
            add(entries, read(f), f"{f.stem} (in PDFium)")
    add(entries, read(PDFIUM / "LICENSE"), "pdfium-binaries build scripts, by Benoit Blanchon")


# ---------------- Rust ----------------

def crate_files(directory: Path):
    return sorted(f for f in directory.iterdir() if f.is_file() and LICENCE_NAME.match(f.name))


def elect(expression: str, files):
    """The licence files to reproduce. Where MIT is one of the choices, MIT is
    taken and the Apache text it is offered beside is left out; anything the
    expression ANDs on (a BSD or Unicode licence) is always kept."""
    mit = [f for f in files if re.search(r"mit", f.name, re.I)]
    offers_mit = re.search(r"\bMIT\b", expression or "") is not None
    if offers_mit and mit:
        keep = []
        for f in files:
            name = f.name.lower()
            if "apache" in name or "unlicense" in name or "zlib" in name or "0bsd" in name:
                continue
            if f in mit or not re.match(r"^licen[cs]e(\.(md|txt))?$", name):
                keep.append(f)
        return keep or mit
    return files


def rust(notices):
    entries = notices.section(
        "Rust libraries in render_core.dll",
        "render_core.dll is Ayaan PDF's own drawing and document core. These "
        "libraries are compiled into it. Where a library offers a choice of "
        "licences, the MIT licence is the one taken.")
    meta = json.loads(subprocess.run(
        ["cargo", "metadata", "--format-version", "1", "--filter-platform", "x86_64-pc-windows-msvc"],
        cwd=ROOT / "render_core", check=True, capture_output=True).stdout)
    packages = {p["id"]: p for p in meta["packages"]}
    nodes = {n["id"]: n for n in meta["resolve"]["nodes"]}
    root = meta["resolve"]["root"]
    seen, stack = set(), [root]
    while stack:
        current = stack.pop()
        if current in seen:
            continue
        seen.add(current)
        for dep in nodes[current]["deps"]:
            if any(kind["kind"] is None for kind in dep["dep_kinds"]):
                stack.append(dep["pkg"])
    seen.discard(root)

    for package in sorted((packages[i] for i in seen), key=lambda p: (p["name"], p["version"])):
        name = f'{package["name"]} {package["version"]} ({package["license"]})'
        directory = Path(package["manifest_path"]).parent
        files = elect(package["license"], crate_files(directory))
        if not files:
            url = package.get("repository") or package.get("homepage") or ""
            add(entries, f'Licensed {package["license"]}. The package carries no licence file; see {url}\n', name)
            continue
        text = "\n".join(read(f) for f in files)
        add(entries, text, name)


# ---------------- .NET ----------------

def nuspec_field(nuspec: Path, field: str) -> str:
    match = re.search(rf"<{field}[^>]*>([^<]*)</{field}>", nuspec.read_text(encoding="utf-8", errors="replace"))
    return match.group(1).strip() if match else ""


def dotnet(notices):
    entries = notices.section(
        ".NET libraries",
        "The .NET and Windows App SDK libraries Ayaan PDF is built on, as shipped in its folder.")
    assets = json.loads((ROOT / "PdfEditorApp" / "obj" / "project.assets.json").read_text(encoding="utf-8"))
    target = next(k for k in assets["targets"] if k.endswith("/win-x64"))
    apache = None
    for name, info in sorted(assets["targets"][target].items()):
        if info.get("type") != "package":
            continue
        runtime = [p for p in info.get("runtime", {}) if not p.endswith("_._")]
        if not runtime and not info.get("native") and not info.get("runtimeTargets"):
            continue
        package_id, version = name.split("/")
        directory = NUGET / package_id.lower() / version
        files = crate_files(directory)
        component = f"{package_id} {version}"
        if files:
            add(entries, "\n".join(read(f) for f in files), component)
            continue
        nuspec = next(directory.glob("*.nuspec"))
        expression = nuspec_field(nuspec, "license")
        copyright_line = nuspec_field(nuspec, "copyright")
        url = nuspec_field(nuspec, "licenseUrl")
        if expression == "Apache-2.0":
            apache = apache or apache_text()
            add(entries, (copyright_line + "\n\n" if copyright_line else "") + apache, component)
        else:
            add(entries, f"{copyright_line}\nLicence terms: {url}\n".lstrip(), component)

    runtime_pack = next(d for d in assets["project"]["frameworks"].values())["downloadDependencies"]
    version = next(d["version"] for d in runtime_pack if d["name"] == "Microsoft.NETCore.App.Runtime.win-x64")
    version = version.strip("[]").split(",")[0].strip()
    pack = NUGET / "microsoft.netcore.app.runtime.win-x64" / version
    runtime_entries = notices.section(
        ".NET runtime",
        f"Ayaan PDF carries its own copy of the .NET {version} runtime.")
    add(runtime_entries, read(pack / "LICENSE.TXT"), f".NET runtime {version}")
    add(runtime_entries, read(pack / "THIRD-PARTY-NOTICES.TXT"), f".NET runtime {version}, third-party notices")

    sdk = [d for d in runtime_pack if d["name"] == "Microsoft.Windows.SDK.NET.Ref"]
    if sdk:
        sdk_version = sdk[0]["version"].strip("[]").split(",")[0].strip()
        sdk_dir = NUGET / "microsoft.windows.sdk.net.ref" / sdk_version
        for f in crate_files(sdk_dir):
            add(runtime_entries, read(f), f"Microsoft.Windows.SDK.NET and WinRT.Runtime {sdk_version}")


def apache_text() -> str:
    """The Apache 2.0 text, copied from a crate that ships it, for the two
    NuGet packages that name the licence without carrying its text."""
    for directory in (Path.home() / ".cargo" / "registry" / "src").glob("*/*"):
        f = directory / "LICENSE-APACHE"
        if f.is_file():
            text = read(f)
            if "Apache License" in text and "Version 2.0" in text and "END OF TERMS AND CONDITIONS" in text:
                return text
    sys.exit("no copy of the Apache 2.0 licence found in the cargo registry")


# ---------------- Writing ----------------

def write(notices):
    rule = "=" * 78
    lines = [
        "Third-party notices for Ayaan PDF",
        "=================================",
        "",
        "Ayaan PDF includes the software below. Each part keeps its own licence,",
        "and the licence of Ayaan PDF itself does not replace any of them.",
        "",
        "Other parts keep their notices beside them, in this folder:",
        f"  Assets{BACKSLASH}Fonts{BACKSLASH}THIRD-PARTY-NOTICES.txt    the stamp icons and the Oswald font",
        f"  Assets{BACKSLASH}Ocr{BACKSLASH}THIRD-PARTY-NOTICES-OCR.txt  text recognition: Tesseract, Leptonica,",
        "                                          the reading models",
        f"  Assets{BACKSLASH}Dictionary{BACKSLASH}LICENSE-*.txt          the dictionaries Define uses",
        "",
        "Contents",
    ]
    for number, (title, _, _) in enumerate(notices.sections, 1):
        lines.append(f"  {number}. {title}")
    lines += ["", "This file is generated by tools/make_third_party_notices.py.", ""]

    for number, (title, intro, entries) in enumerate(notices.sections, 1):
        lines += [rule, f"{number}. {title}", rule, "", textwrap.fill(intro, 78), ""]
        for text, components in entries.items():
            lines.append("-" * 78)
            for component in components:
                lines.append(component)
            lines += ["-" * 78, "", text.rstrip("\n"), ""]
    OUT.write_bytes(("\r\n".join(lines) + "\r\n").encode("utf-8"))
    count = sum(len(c) for _, _, e in notices.sections for c in e.values())
    print(f"wrote {OUT.relative_to(ROOT)}: {count} components, {OUT.stat().st_size // 1024} KB")


if __name__ == "__main__":
    notices = Notices()
    pdfium(notices)
    rust(notices)
    dotnet(notices)
    write(notices)
