"""Check the draft's local assets and create an installable OWML ZIP.

Build src/CustomReels.csproj first, then run with Python 3.
No third-party Python dependencies or image conversion required.
"""

import argparse
import json
from pathlib import Path
import struct
from zipfile import ZIP_DEFLATED, ZipFile


ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--validate-only", action="store_true",
                        help="Validate content without a built DLL or creating an archive")
    args = parser.parse_args()
    manifest = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
    planet_files = ["planets/ring-world.json", "planets/timber-hearth.json"]
    bodies = [json.loads((ROOT / name).read_text(encoding="utf-8"))
              for name in planet_files]
    for name in ("default-config.json", "addon-manifest.json"):
        json.loads((ROOT / name).read_text(encoding="utf-8"))

    reels = []
    for body in bodies:
        planet_reels = body["Props"]["slideShows"]
        if len(planet_reels) != 1 or not planet_reels[0]["slides"]:
            raise ValueError("Each test location must contain one nonempty reel")
        reel = planet_reels[0]
        if any(type(i) is not int or not 0 <= i < len(reel["slides"])
               for i in reel["displaySlides"]):
            raise ValueError("Reel thumbnails reference a missing slide")
        reels.append(reel)
    if reels[0]["slides"] != reels[1]["slides"]:
        raise ValueError("Both test locations must use the same slides and audio")

    files = [
        "manifest.json", "default-config.json", "addon-manifest.json",
        manifest["filename"], *planet_files,
        "README.md", "THIRD_PARTY_LICENSES.txt",
    ]
    files.extend(slide["imagePath"] for slide in reels[0]["slides"])
    for slide in reels[0]["slides"]:
        for field in ("beatAudio", "backdropAudio"):
            audio = slide.get(field, "")
            extension = Path(audio).suffix
            if extension.lower() in (".wav", ".ogg", ".mp3"):
                if extension != extension.lower():
                    raise ValueError(f"Audio extensions must be lowercase for New Horizons: {audio}")
                files.append(audio)
    files = list(dict.fromkeys(files))
    sources = {name: ROOT / name for name in files}
    sources[manifest["filename"]] = ROOT / "build" / manifest["filename"]
    for name in files:
        if args.validate_only and name == manifest["filename"]:
            continue
        path = sources[name].resolve()
        if ROOT not in path.parents or not path.is_file():
            raise ValueError(f"Missing or invalid package file: {name}")
    if not args.validate_only and sources[manifest["filename"]].read_bytes()[:2] != b"MZ":
        raise ValueError("The loader DLL is missing or is not a PE binary")

    for slide in reels[0]["slides"]:
        name = slide["imagePath"]
        data = sources[name].read_bytes()
        if (len(data) < 24 or data[:8] != b"\x89PNG\r\n\x1a\n"
                or data[12:16] != b"IHDR"
                or struct.unpack(">II", data[16:24]) != (1024, 1024)):
            raise ValueError(f"Slide must be a 1024x1024 PNG: {name}")

    summary = f"Two test locations with {len(reels[0]['slides'])} matching 1024x1024 slides and audio."
    if args.validate_only:
        print(summary + " Content validation passed; DLL not checked and no archive created.")
        return

    destination = ROOT / "dist"
    destination.mkdir(exist_ok=True)
    archive = destination / f'{manifest["uniqueName"]}-{manifest["version"]}.zip'
    with ZipFile(archive, "w", compression=ZIP_DEFLATED) as package:
        for name in files:
            package.write(sources[name], f'{manifest["uniqueName"]}/{name}')
    with ZipFile(archive) as package:
        if package.testzip() is not None:
            raise ValueError("ZIP integrity check failed")
        for name in files:
            if package.read(f'{manifest["uniqueName"]}/{name}') != sources[name].read_bytes():
                raise ValueError(f"Packaged content mismatch: {name}")
    print(f"Packaged {len(files)} files: {archive}")
    print(summary + " In-game validation is still required.")


if __name__ == "__main__":
    main()
