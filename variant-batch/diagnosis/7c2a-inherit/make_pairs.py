from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "sample-specimens.txt"
PAIR_DIR = ROOT / "pairs"
CROP = (12, 0, 244, 216)
HEADER = 30


def source_keys() -> list[str]:
    return [
        line.strip()
        for line in SOURCE.read_text(encoding="utf-8").splitlines()
        if line.strip() and not line.startswith("#")
    ]


def filename(key: str) -> str:
    return key.replace(":", "-") + ".png"


def pair_for(key: str) -> Image.Image:
    accepted = Image.open(ROOT / "accepted" / filename(key)).convert("RGB").crop(CROP)
    candidate = Image.open(ROOT / "candidate" / filename(key)).convert("RGB").crop(CROP)
    pair = Image.new("RGB", (accepted.width + candidate.width, HEADER + accepted.height), "#181818")
    pair.paste(accepted, (0, HEADER))
    pair.paste(candidate, (accepted.width, HEADER))
    draw = ImageDraw.Draw(pair)
    font = ImageFont.load_default()
    draw.text((5, 4), f"{key}  accepted (ed37e8f)", fill="white", font=font)
    draw.text((accepted.width + 5, 16), "candidate (48c16dc)", fill="white", font=font)
    return pair


def main() -> None:
    PAIR_DIR.mkdir(parents=True, exist_ok=True)
    pairs: list[Image.Image] = []
    for key in source_keys():
        pair = pair_for(key)
        pair.save(PAIR_DIR / f"pair-{filename(key)}")
        pairs.append(pair)

    sheet = Image.new("RGB", (pairs[0].width, sum(pair.height for pair in pairs)), "#181818")
    y = 0
    for pair in pairs:
        sheet.paste(pair, (0, y))
        y += pair.height
    sheet.save(ROOT / "contact-sheet-16-specimens-accepted-left-candidate-right.png")


if __name__ == "__main__":
    main()
