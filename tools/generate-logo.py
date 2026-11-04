#!/usr/bin/env python3
"""
Generate YFG YASTA brand logo as a multi-resolution .ico file.
"""
import struct
import io
from PIL import Image, ImageDraw, ImageFont


def make_logo(size: int) -> Image.Image:
    """Render the YFG YASTA logo at the given size."""
    scale = max(1, 256 // size) if size < 256 else 1
    master_size = size * scale
    img = Image.new("RGBA", (master_size, master_size), (244, 245, 247, 0))
    draw = ImageDraw.Draw(img)

    GREEN = (61, 166, 57)
    GREEN_LIGHT = (141, 198, 63)
    ORANGE = (247, 148, 29)
    ORANGE_LIGHT = (253, 185, 19)
    RED = (237, 28, 36)
    RED_DARK = (193, 16, 46)
    NAVY_DARK = (16, 16, 32)
    BLUE = (27, 117, 208)

    margin = master_size // 10
    logo_w = master_size - 2 * margin
    logo_h = master_size - 2 * margin

    top_h = int(logo_h * 0.65)
    top_y = margin
    letter_w = logo_w // 3
    gap = letter_w // 12

    def draw_gradient_rect(draw, x, y, w, h, color_top, color_bottom):
        for i in range(h):
            ratio = i / max(1, h - 1)
            r = int(color_top[0] + (color_bottom[0] - color_top[0]) * ratio)
            g = int(color_top[1] + (color_bottom[1] - color_top[1]) * ratio)
            b = int(color_top[2] + (color_bottom[2] - color_top[2]) * ratio)
            draw.line([(x, y + i), (x + w, y + i)], fill=(r, g, b, 255))

    def draw_letter_Y(draw, x, y, w, h, bg):
        thick = max(2, int(min(w, h) * 0.2))
        cx = x + w // 2
        mid_y = y + h // 2
        # Left diagonal
        draw.polygon([(x, y), (cx - thick // 2, mid_y), (cx + thick // 2, mid_y)], fill=bg)
        # Right diagonal
        draw.polygon([(x + w, y), (cx + thick // 2, mid_y), (cx - thick // 2, mid_y)], fill=bg)
        # Vertical stem
        draw.rectangle([cx - thick // 2, mid_y, cx + thick // 2, y + h], fill=bg)

    def draw_letter_F(draw, x, y, w, h, bg):
        thick = max(2, int(min(w, h) * 0.2))
        # Vertical stem
        draw.rectangle([x, y, x + thick, y + h], fill=bg)
        # Top horizontal
        draw.rectangle([x, y, x + w, y + thick], fill=bg)
        # Middle horizontal
        mid_y = y + h // 2
        draw.rectangle([x, mid_y - thick // 2, x + int(w * 0.7), mid_y + thick // 2], fill=bg)

    def draw_letter_G(draw, x, y, w, h, accent):
        thick = max(2, int(min(w, h) * 0.2))
        cx = x + w // 2
        cy = y + h // 2
        # Outer circle (filled)
        draw.ellipse([x, y, x + w, y + h], fill=None)
        # Cut inner circle
        draw.ellipse([x + thick, y + thick, x + w - thick, y + h - thick], fill=(244, 245, 247, 0))
        # Horizontal bar from center-right going inward
        draw.rectangle([cx, cy - thick // 2, x + w, cy + thick // 2], fill=(244, 245, 247, 0))
        # Blue accent (cursor/triangle) inside G
        tri_size = max(3, int(w * 0.18))
        draw.polygon([
            (cx + tri_size // 2, cy),
            (cx - tri_size, cy - tri_size),
            (cx - tri_size, cy + tri_size),
        ], fill=accent)

    # Draw Y (green gradient)
    draw_gradient_rect(draw, margin, top_y, letter_w - gap, top_h, GREEN_LIGHT, GREEN)
    draw_letter_Y(draw, margin, top_y, letter_w - gap, top_h, (244, 245, 247, 0))

    # Draw F (orange gradient)
    draw_gradient_rect(draw, margin + letter_w, top_y, letter_w - gap, top_h, ORANGE_LIGHT, ORANGE)
    draw_letter_F(draw, margin + letter_w, top_y, letter_w - gap, top_h, (244, 245, 247, 0))

    # Draw G (red gradient)
    draw_gradient_rect(draw, margin + 2 * letter_w, top_y, letter_w, top_h, RED, RED_DARK)
    draw_letter_G(draw, margin + 2 * letter_w, top_y, letter_w, top_h, BLUE)

    # Green swoosh arc over Y and F
    swoosh_thickness = max(2, master_size // 40)
    arc_bbox = [margin - max(2, master_size // 80), top_y - max(2, master_size // 40),
                margin + 2 * letter_w, top_y + top_h]
    for i in range(swoosh_thickness):
        draw.arc(arc_bbox, start=180, end=360, fill=(GREEN[0], GREEN[1], GREEN[2], 255),
                 width=max(1, master_size // 100))
        arc_bbox[1] += 1
        arc_bbox[3] += 1

    # Bottom row: YASTA text (only if large enough)
    if size >= 32:
        bottom_y = top_y + top_h + max(2, master_size // 30)
        bottom_h = logo_h - top_h - max(2, master_size // 30)
        try:
            font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", int(bottom_h * 0.65))
        except:
            font = ImageFont.load_default()

        text = "YASTA"
        spacing = int(letter_w * 0.18)
        char_widths = []
        total_w = 0
        for c in text:
            try:
                cw = int(draw.textlength(c, font=font))
            except:
                cw = int(bottom_h * 0.5)
            char_widths.append(cw)
            total_w += cw + spacing
        total_w -= spacing

        start_x = margin + (logo_w - total_w) // 2
        cx = start_x
        for i, c in enumerate(text):
            draw.text((cx, bottom_y), c, fill=(NAVY_DARK[0], NAVY_DARK[1], NAVY_DARK[2], 255), font=font)
            cx += char_widths[i] + spacing

    # Downscale
    if scale > 1:
        img = img.resize((size, size), Image.LANCZOS)
    return img


def build_ico(output_path: str):
    sizes = [256, 48, 32, 24, 16]
    png_data = []
    images = []
    for s in sizes:
        img = make_logo(s)
        images.append(img)
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        png_data.append(buf.getvalue())

    icon_dir = struct.pack("<HHH", 0, 1, len(sizes))
    entries = b""
    offset = 6 + 16 * len(sizes)
    for i, s in enumerate(sizes):
        w = 0 if s == 256 else s
        h = w
        data_size = len(png_data[i])
        entries += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, data_size, offset)
        offset += data_size

    ico_bytes = icon_dir + entries
    for png in png_data:
        ico_bytes += png

    with open(output_path, "wb") as f:
        f.write(ico_bytes)
    print(f"ICO written: {output_path} ({len(ico_bytes)} bytes)")

    # Also save the 256x256 PNG
    png_path = output_path.replace(".ico", ".png")
    images[0].save(png_path)
    print(f"PNG reference: {png_path}")


if __name__ == "__main__":
    import sys
    out = sys.argv[1] if len(sys.argv) > 1 else "yfgdesk-mark.ico"
    build_ico(out)
