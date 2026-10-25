"""Generates the SlothBoss app icon (a calm sloth face on a moss-green tile).

Usage: python scripts/make-icon.py
Writes apps/windows/src/SlothBoss.App/Assets/SlothBoss.ico and SlothBoss.png (1024px master).
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "apps" / "windows" / "src" / "SlothBoss.App" / "Assets"
S = 1024


def draw() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((40, 40, S - 40, S - 40), radius=220, fill=(47, 93, 80, 255))  # moss tile
    d.ellipse((150, 190, S - 150, S - 150), fill=(122, 92, 66, 255))  # fur
    d.ellipse((230, 300, S - 230, S - 200), fill=(240, 226, 200, 255))  # face
    # Drooping eye patches give the unhurried sloth look.
    d.polygon([(300, 470), (470, 430), (480, 520), (330, 600)], fill=(84, 60, 42, 255))
    d.polygon([(S - 300, 470), (S - 470, 430), (S - 480, 520), (S - 330, 600)], fill=(84, 60, 42, 255))
    d.ellipse((392, 470, 432, 510), fill=(25, 20, 16, 255))
    d.ellipse((S - 432, 470, S - 392, 510), fill=(25, 20, 16, 255))
    d.ellipse((472, 580, 552, 630), fill=(60, 44, 32, 255))  # nose
    d.arc((432, 600, 592, 720), start=20, end=160, fill=(60, 44, 32, 255), width=14)  # smile
    return img


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    img = draw()
    img.save(OUT / "SlothBoss.png")
    img.save(OUT / "SlothBoss.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
