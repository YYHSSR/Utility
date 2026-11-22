"""Generate the multi-resolution Windows .ico asset from the owner's source PNG."""

from pathlib import Path
from PIL import Image, ImageDraw

REPO_ROOT = Path(__file__).resolve().parent.parent
SOURCE_PNG = REPO_ROOT / "assets" / "Utility-icon-source.png"
TARGET_ICO = REPO_ROOT / "assets" / "Utility.ico"
PREVIEW_PNG = Path(
    r"C:\Users\zf\.gemini\antigravity\brain\3848649a-0b8a-4940-b020-7372fef5587f\scratch\icon-preview.png"
)
ICON_SIZES = [16, 24, 32, 48, 64, 128, 256]


def create_rounded_mask(size: int, radius_ratio: float = 0.16) -> Image.Image:
    """Build an anti-aliased rounded-square alpha mask at 4x supersampling."""
    scale = 4
    large_size = size * scale
    radius = int(round(large_size * radius_ratio))
    mask = Image.new("L", (large_size, large_size), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle(
        [(0, 0), (large_size - 1, large_size - 1)],
        radius=radius,
        fill=255,
    )
    return mask.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    if not SOURCE_PNG.exists():
        raise FileNotFoundError(f"Missing source icon: {SOURCE_PNG}")

    with Image.open(SOURCE_PNG) as src:
        rgba = src.convert("RGBA")
        # Frame the face, eyes, smile, bangs, X hairpin, and upper ribbons tightly
        # in a square crop so small taskbar/Explorer sizes (16, 24, 32 px) stay clear.
        crop_box = (240, 20, 1080, 860)
        cropped = rgba.crop(crop_box)

    frames: list[Image.Image] = []
    for size in ICON_SIZES:
        resized = cropped.resize((size, size), Image.Resampling.LANCZOS)
        mask = create_rounded_mask(size)
        alpha = resized.getchannel("A")
        combined_alpha = Image.composite(alpha, Image.new("L", (size, size), 0), mask)
        resized.putalpha(combined_alpha)
        frames.append(resized)

    TARGET_ICO.parent.mkdir(parents=True, exist_ok=True)
    # Save starting from largest frame with append_images for proper ICO multi-resolution embedding.
    largest_first = list(reversed(frames))
    largest_first[0].save(
        TARGET_ICO,
        format="ICO",
        append_images=largest_first[1:],
    )

    # Generate a horizontal preview strip for visual inspection.
    PREVIEW_PNG.parent.mkdir(parents=True, exist_ok=True)
    strip_w = sum(ICON_SIZES) + 24 * (len(ICON_SIZES) + 1)
    strip_h = 300
    strip = Image.new("RGBA", (strip_w, strip_h), (30, 34, 42, 255))
    x = 24
    for size, frame in zip(ICON_SIZES, frames):
        y = (strip_h - size) // 2
        strip.alpha_composite(frame, (x, y))
        x += size + 24
    strip.save(PREVIEW_PNG, format="PNG")
    print(f"Generated {TARGET_ICO} ({TARGET_ICO.stat().st_size} bytes)")
    print(f"Generated preview {PREVIEW_PNG}")


if __name__ == "__main__":
    main()
