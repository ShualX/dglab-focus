"""Create the project's own vector-style app icon and record palette contrast."""
import json
import math
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
assets = root / "src" / "Focus.Windows" / "Assets"
assets.mkdir(parents=True, exist_ok=True)
scale = 4
image = Image.new("RGBA", (256 * scale, 256 * scale), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
def box(values):
    return tuple(int(v * scale) for v in values)
draw.rounded_rectangle(box((0, 0, 255, 255)), radius=48 * scale, fill="#1B1B1D")
draw.ellipse(box((40, 40, 216, 216)), outline="#595446", width=3 * scale)
draw.arc(box((40, 40, 216, 216)), start=-90, end=160, fill="#FFEB9B", width=8 * scale)
for i in range(12):
    angle = (i * 30 - 90) * math.pi / 180
    points = [128 + 109 * math.cos(angle), 128 + 109 * math.sin(angle),
              128 + 116 * math.cos(angle), 128 + 116 * math.sin(angle)]
    draw.line(box(points), fill="#FFEB9B", width=3 * scale)
draw.line(box((128, 80, 128, 131, 161, 151)), fill="#F4F2E9", width=11 * scale, joint="curve")
draw.ellipse(box((121, 121, 135, 135)), fill="#FFEB9B")
image.resize((256, 256), Image.Resampling.LANCZOS).save(assets / "focus.ico", sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

def lum(value):
    rgb = [int(value[i:i+2], 16) / 255 for i in (1, 3, 5)]
    linear = [v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in rgb]
    return sum(a * b for a, b in zip(linear, [0.2126, 0.7152, 0.0722]))
def contrast(a, b):
    x, y = sorted([lum(a), lum(b)])
    return round((y + .05) / (x + .05), 2)
checks = [{"text": a, "surface": b, "ratio": contrast(a, b)} for a, b in [
    ("#F4F2E9", "#1B1B1D"), ("#B5B4AD", "#1B1B1D"), ("#B5B4AD", "#242426"),
    ("#FFEB9B", "#1B1B1D"), ("#17150E", "#FFEB9B"), ("#FFAAAA", "#242426")]]
assert all(check["ratio"] >= 4.5 for check in checks)
(root / "docs" / "contrast.json").write_text(json.dumps(checks, indent=2) + "\n", encoding="utf-8")
print(json.dumps(checks))
