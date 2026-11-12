"""Check the draft's local assets and create an installable OWML ZIP.

Run with Python 3. No third-party dependencies or image conversion required.
"""

import json
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile


ROOT = Path(__file__).resolve().parents[1]


def main():
    manifest = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
    body = json.loads((ROOT / "planets/timber-hearth.json").read_text(encoding="utf-8"))
    for name in ("default-config.json", "addon-manifest.json"):
        json.loads((ROOT / name).read_text(encoding="utf-8"))

    reels = body["Props"]["slideShows"]
    if len(reels) != 1 or len(reels[0]["slides"]) != 2:
        raise ValueError("This draft must contain exactly one reel with two slides")
    if any(i not in (0, 1) for i in reels[0]["displaySlides"]):
        raise ValueError("Reel thumbnails reference a missing slide")

    files = [
        "manifest.json", "default-config.json", "addon-manifest.json",
        manifest["filename"], "planets/timber-hearth.json",
        "README.md", "THIRD_PARTY_LICENSES.txt",
    ]
    files.extend(slide["imagePath"] for slide in reels[0]["slides"])
    for name in files:
        path = (ROOT / name).resolve()
        if ROOT not in path.parents or not path.is_file():
            raise ValueError(f"Missing or invalid package file: {name}")
    if (ROOT / manifest["filename"]).read_bytes()[:2] != b"MZ":
        raise ValueError("The loader DLL is missing or is not a PE binary")

    destination = ROOT / "dist"
    destination.mkdir(exist_ok=True)
    archive = destination / f'{manifest["uniqueName"]}-{manifest["version"]}.zip'
    with ZipFile(archive, "w", compression=ZIP_DEFLATED) as package:
        for name in files:
            package.write(ROOT / name, f'{manifest["uniqueName"]}/{name}')
    with ZipFile(archive) as package:
        if package.testzip() is not None:
            raise ValueError("ZIP integrity check failed")
        for name in files:
            if package.read(f'{manifest["uniqueName"]}/{name}') != (ROOT / name).read_bytes():
                raise ValueError(f"Packaged content mismatch: {name}")
    print(f"Packaged {len(files)} files: {archive}")
    print("Two original images included unchanged. In-game validation is still required.")


if __name__ == "__main__":
    main()
