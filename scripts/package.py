"""Check the draft's local assets and create an installable OWML ZIP.

Build src/CustomReels.csproj first, then run with Python 3.
No third-party Python dependencies or image conversion required.
"""

import json
from pathlib import Path
import struct
from zipfile import ZIP_DEFLATED, ZipFile


ROOT = Path(__file__).resolve().parents[1]


def main():
    manifest = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
    planet_files = ["planets/ring-world.json", "planets/timber-hearth.json"]
    bodies = [json.loads((ROOT / name).read_text(encoding="utf-8"))
              for name in planet_files]
    for name in ("default-config.json", "addon-manifest.json"):
        json.loads((ROOT / name).read_text(encoding="utf-8"))

    reels = []
    for body in bodies:
        planet_reels = body["Props"]["slideShows"]
        if len(planet_reels) != 1 or len(planet_reels[0]["slides"]) != 3:
            raise ValueError("Each test location must contain one reel with three slides")
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
        path = sources[name].resolve()
        if ROOT not in path.parents or not path.is_file():
            raise ValueError(f"Missing or invalid package file: {name}")
    if sources[manifest["filename"]].read_bytes()[:2] != b"MZ":
        raise ValueError("The loader DLL is missing or is not a PE binary")

    third_image = (ROOT / reels[0]["slides"][2]["imagePath"]).read_bytes()
    if (third_image[:8] != b"\x89PNG\r\n\x1a\n"
            or third_image[12:16] != b"IHDR"
            or len(third_image) < 24
            or struct.unpack(">II", third_image[16:24]) != (1024, 1024)):
        raise ValueError("The third slide must be a 1024x1024 PNG")

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
    print("Two test locations with matching slides; third PNG is 1024x1024. In-game validation is still required.")


if __name__ == "__main__":
    main()
