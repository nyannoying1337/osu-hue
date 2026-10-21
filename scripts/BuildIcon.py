"""Build the independent ribbon mark and Windows icon. Requires Pillow."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SCALE = 4
SIZE = 256
STROKE = 28
START = (72, 146)
CURVES = [((93, 146), (105, 104), (135, 104)), ((160, 104), (179, 124), (179, 151))]
STOPS = [(0, (255, 109, 158)), (.52, (175, 146, 255)), (1, (101, 217, 230))]


def curve_points(start, controls):
    points = [start]
    for a, b, end in controls:
        for i in range(1, 65):
            t = i / 64
            points.append(tuple((1-t)**3 * start[j] + 3*(1-t)**2*t*a[j]
                                + 3*(1-t)*t*t*b[j] + t**3*end[j] for j in (0, 1)))
        start = end
    return points


def stroke(draw, points):
    scaled = [(round(x*SCALE), round(y*SCALE)) for x, y in points]
    draw.line(scaled, fill=255, width=STROKE*SCALE, joint="curve")
    radius = STROKE*SCALE/2
    for x, y in (scaled[0], scaled[-1]):
        draw.ellipse((x-radius, y-radius, x+radius, y+radius), fill=255)


def main():
    pixels = SIZE*SCALE
    image = Image.new("RGBA", (pixels, pixels))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((8*SCALE, 8*SCALE, 248*SCALE, 248*SCALE), radius=58*SCALE,
                           fill=(19, 22, 32), outline=(49, 55, 74), width=SCALE)
    mask = Image.new("L", image.size)
    pen = ImageDraw.Draw(mask)
    stroke(pen, [(72, 68), (72, 184)])
    stroke(pen, curve_points(START, CURVES) + [(179, 184)])
    gradient = Image.new("RGBA", image.size)
    colours = ImageDraw.Draw(gradient)
    for x in range(pixels):
        t = max(0, min(1, (x/SCALE-58)/135))
        left, right = (STOPS[0], STOPS[1]) if t <= STOPS[1][0] else (STOPS[1], STOPS[2])
        fraction = (t-left[0])/(right[0]-left[0])
        rgb = tuple(round(a+(b-a)*fraction) for a, b in zip(left[1], right[1]))
        colours.line((x, 0, x, pixels), fill=rgb+(255,))
    image.paste(gradient, (0, 0), mask)
    image = image.resize((SIZE, SIZE), Image.Resampling.LANCZOS)
    assets = ROOT / "OsuHue/Assets"
    assets.mkdir(exist_ok=True)
    image.save(assets / "brand-mark.png")
    image.save(ROOT / "OsuHue/app.ico", sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
    svg_path = "M72 146 " + " ".join(f"C{a[0]} {a[1]} {b[0]} {b[1]} {end[0]} {end[1]}" for a, b, end in CURVES) + " L179 184"
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="osu!hue ribbon mark">
  <defs><linearGradient id="light" x1="58" y1="0" x2="193" y2="0" gradientUnits="userSpaceOnUse">
    <stop stop-color="#ff6d9e"/><stop offset=".52" stop-color="#af92ff"/><stop offset="1" stop-color="#65d9e6"/>
  </linearGradient></defs>
  <rect x="8" y="8" width="240" height="240" rx="58" fill="#131620" stroke="#31374a"/>
  <g fill="none" stroke="url(#light)" stroke-width="28" stroke-linecap="round" stroke-linejoin="round">
    <path d="M72 68 L72 184"/><path d="{svg_path}"/>
  </g>
</svg>
'''
    (ROOT / "docs/images/brand-mark.svg").write_text(svg, encoding="utf-8")
    print("Built SVG, PNG and multi-resolution Windows icon.")


if __name__ == "__main__":
    main()
