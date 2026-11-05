"""Builds Nib.ico from the two SVG masters: the bold small-size drawing for 16–40 px and the detailed
drawing for 48–256 px. Entries below 256 px are 32-bit BMPs (best compatibility); 256 px is PNG."""
import io, struct, subprocess, sys
from PIL import Image

SMALL = [16, 20, 24, 32, 40]
LARGE = [48, 64, 96, 128, 256]

def render(svg, size):
    png = subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), svg], check=True, capture_output=True).stdout
    return Image.open(io.BytesIO(png)).convert("RGBA")

def bmp_entry(img):
    w, h = img.size
    px = img.tobytes("raw", "BGRA")
    rows = [px[y * w * 4:(y + 1) * w * 4] for y in range(h)]
    xor = b"".join(reversed(rows))                      # bottom-up
    mask_row = ((w + 31) // 32) * 4
    alpha = img.getchannel("A").tobytes()
    mask = bytearray()
    for y in reversed(range(h)):
        row = bytearray(mask_row)
        for x in range(w):
            if alpha[y * w + x] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += row
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(mask), 0, 0, 0, 0)
    return header + xor + bytes(mask)

def png_entry(img):
    b = io.BytesIO(); img.save(b, "PNG", optimize=True); return b.getvalue()

def main(small_svg, full_svg, out):
    images = [(s, render(small_svg, s)) for s in SMALL] + [(s, render(full_svg, s)) for s in LARGE]
    blobs = [(s, png_entry(im) if s >= 256 else bmp_entry(im)) for s, im in images]
    offset = 6 + 16 * len(blobs)
    out_bytes = bytearray(struct.pack("<HHH", 0, 1, len(blobs)))
    for s, data in blobs:
        out_bytes += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in blobs:
        out_bytes += data
    open(out, "wb").write(out_bytes)

if __name__ == "__main__":
    main(*sys.argv[1:4])
