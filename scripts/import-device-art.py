"""Import official TWS artwork from a local VivoTwsApp extraction (requires Pillow).

Usage: python scripts/import-device-art.py --source .vs/VivoTwsApp
Only static, transparent images are embedded; the app never reads the source APK
or downloads animations. The generated catalog is also the runtime model map.
"""

import argparse
import hashlib
import json
from pathlib import Path
import re

from PIL import Image


ROOT = Path(__file__).resolve().parent.parent
DEFAULT_MODELS = {"vivotwsair3pro": 169}


def normalize(name):
    return "".join(c.lower() for c in name if c.isalnum())


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def first_image(folder, pattern):
    paths = sorted(p for p in folder.glob(pattern) if p.suffix.lower() in (".png", ".webp"))
    if not paths:
        raise FileNotFoundError(f"No {pattern} image in {folder}")
    return paths[0]


def closed_case_bounds(image):
    """Find the separate, widest object in the official closed-case loop frame.

    The frame contains two individual earbuds to the left of the closed case.
    Transparent columns separate all three. Keep the original antialiased edge;
    never erase pixels, mirror an earbud, or synthesize a closed lid.
    """
    projection = image.getchannel("A").resize((image.width, 1), Image.Resampling.BOX)
    runs = []
    start = None
    for x in range(image.width + 1):
        visible = x < image.width and projection.getpixel((x, 0)) >= 2
        if visible and start is None:
            start = x
        elif not visible and start is not None:
            if x - start > 15:
                runs.append((start, x))
            start = None
    if len(runs) != 3:
        raise ValueError(f"Expected separate left/right/case objects, got {runs}")
    case = max(runs, key=lambda run: run[1] - run[0])
    if case != runs[-1]:
        raise ValueError("Closed case must be the widest and rightmost object")
    # Crop in the empty gap so none of the faint case edge is lost.
    return ((runs[-2][1] + case[0]) // 2, 0, image.width, image.height)


def export_image(source, target, case_frame=False):
    with Image.open(source) as raw:
        image = raw.convert("RGBA")
    crop = (0, 0, image.width, image.height)
    if case_frame:
        crop = closed_case_bounds(image)
        image = image.crop(crop)
    bounds = image.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError(f"Empty image: {source}")
    crop = (crop[0] + bounds[0], crop[1] + bounds[1], crop[0] + bounds[2], crop[1] + bounds[3])
    image = image.crop(bounds)
    image.thumbnail((512, 512), Image.Resampling.LANCZOS)
    target.parent.mkdir(parents=True, exist_ok=True)
    image.save(target, optimize=True)
    return {"crop": list(crop), "size": list(image.size), "sha256": sha256(target)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ROOT / ".vs/VivoTwsApp")
    args = parser.parse_args()
    source = args.source.resolve()
    models = json.loads((source / "analysis/tws_models.json").read_text(encoding="utf-8"))
    profile_source = (ROOT / "src/VivoPods.Core/Models/DeviceProfile.cs").read_text(encoding="utf-8")
    names = re.findall(r'\("((?:vivo|iQOO) TWS [^"\n]+)"\s*,', profile_source)
    if not names or len(names) != len(set(names)):
        raise ValueError("Could not read unique TWS profiles")

    output = ROOT / "src/VivoPods.App/Assets/Devices"
    catalog, provenance, exported = [], [], {}
    apk = source / "extracted/base.apk"
    apk_hash = sha256(apk)
    for name in names:
        choices = [model for model in models if normalize(model["name"]) == normalize(name)]
        if not choices:
            raise ValueError(f"No official model for {name}")
        preferred = DEFAULT_MODELS.get(normalize(name), choices[0]["model"])
        model = next(model for model in choices if model["model"] == preferred)
        model_id = model["model"]
        file_model = model["bitmap_data"]["file_model"]
        downloaded = source / f"downloads/model_{model_id}/extracted"
        if downloaded.exists():
            directory = f"model-{model_id}"
            response = json.loads((downloaded.parent / "api2_response.json").read_text(encoding="utf-8"))
            archive = response["data"]["zip"]
            if isinstance(archive, str):
                archive = json.loads(archive)
            zip_path = downloaded.parent / f"model_{model_id}.zip"
            archive_hash = sha256(zip_path)
            if archive_hash != archive["md5"]:
                raise ValueError(f"Resource archive checksum mismatch: {model_id}")
            origin = {"url": response["redirect"] + archive["url"], "version": response["data"]["version"], "sha256": archive_hash}
            parts = {
                "left": first_image(downloaded / "left", "*"),
                "right": first_image(downloaded / "right", "*"),
                "case": first_image(downloaded / "after_tip_circle", "*.webp"),
            }
        else:
            directory = f"apk-{file_model}"
            local = source / f"extracted/apk/assets/flash_connect/{file_model}"
            origin = {"apk": "com.android.vivo.tws.vivotws", "sha256": apk_hash}
            parts = {part: first_image(local, stem + "_1.*") for part, stem in (("left", "left"), ("right", "right"), ("case", "close"))}

        catalog.append({"Name": name, "Model": model_id, "Directory": directory})
        assets = {}
        for part, path in parts.items():
            target = output / directory / f"{part}.png"
            if target not in exported:
                exported[target] = export_image(path, target, case_frame=downloaded.exists() and part == "case")
            assets[part] = {"source": path.relative_to(source).as_posix(), "sourceSha256": sha256(path),
                            "target": target.relative_to(ROOT).as_posix(), **exported[target]}
        provenance.append({"name": name, "model": model_id, "fileModel": file_model, "origin": origin, "assets": assets})

    (output / "catalog.json").write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (ROOT / "docs/device-art-sources.json").write_text(json.dumps(provenance, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Imported {len(catalog)} TWS models, {len(exported)} images ({sum(p.stat().st_size for p in exported):,} bytes).")
    print(f"vivo: {sum(item['Name'].startswith('vivo ') for item in catalog)}, iQOO: {sum(item['Name'].startswith('iQOO ') for item in catalog)}")


if __name__ == "__main__":
    main()
