"""Generate PDF fixtures. Requires Pillow, pypdf and fonttools."""
import argparse
from pathlib import Path

from fontTools.pens.qu2cuPen import Qu2CuPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.svgLib.path import parse_path
from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFont
from pypdf import PdfReader, PdfWriter
from pypdf.generic import DecodedStreamObject, DictionaryObject, NameObject, NumberObject

FOLDER = Path(__file__).resolve().parent
FONT = "C:/Windows/Fonts/arial.ttf"


def scan(output):
    image = Image.new("RGB", (1650, 2340), "white")
    draw = ImageDraw.Draw(image)
    left, top, right, bottom = 158, 1868, 1611, 2300
    draw.rectangle((left, top, right, bottom), outline="black", width=4)
    draw.line((668, top, 668, bottom), fill="black", width=4)
    draw.line((668, 1986, right, 1986), fill="black", width=4)
    draw.line((1218, 1986, 1218, bottom), fill="black", width=4)
    draw.line((1218, 2143, right, 2143), fill="black", width=4)
    draw.line((668, 2182, 1218, 2182), fill="black", width=4)
    font = ImageFont.truetype(FONT, 55)
    draw.text((780, 1894), "АБВГ.123456.001", font=font, fill="black")
    draw.text((738, 2010), "Корпус", font=font, fill="black")
    draw.text((738, 2084), "основной", font=font, fill="black")
    image.save(output / "scan-form1.pdf", resolution=200)
    numbered = image.copy()
    title = ImageDraw.Draw(numbered)
    title.line((1218, 2182, 1611, 2182), fill="black", width=4)
    title.line((1375, 2143, 1375, 2182), fill="black", width=4)
    title.text((1315, 2145), "1", font=ImageFont.truetype(FONT, 31), fill="black")
    continuation = Image.new("RGB", image.size, "white")
    stamp = ImageDraw.Draw(continuation)
    stamp.rectangle((left, 2182, right, bottom), outline="black", width=4)
    for x in (668, 1532):
        stamp.line((x, 2182, x, bottom), fill="black", width=4)
    for y in (2221, 2260):
        stamp.line((left, y, 668, y), fill="black", width=4)
    stamp.text((780, 2206), "АБВГ.123456.001", font=font, fill="black")
    stamp.text((1554, 2228), "2", font=ImageFont.truetype(FONT, 44), fill="black")
    continuation.save(output / "scan-numbered-batch.pdf", resolution=200, save_all=True, append_images=[numbered])
    # A specification continuation whose table shares the stamp's side rules.
    stamp.rectangle((left, 78, right, 2182), outline="black", width=4)
    for x in (left + 38, left + 78, left + 130, left + 650, left + 1120):
        stamp.line((x, 78, x, 2182), fill="black", width=4)
    for y in range(180, 2182, 55):
        stamp.line((left, y, right, y), fill="black", width=3)
    tilted = continuation.rotate(.18, Image.Resampling.BICUBIC, expand=False, fillcolor="white")
    tilted.save(output / "scan-continuation-skewed.pdf", resolution=200)
    # A clipped bottom rule and a tilted, sideways scan with text in both cells.
    draw.rectangle((690, 1880, 1590, 1970), fill="white")
    draw.text((780, 1894), "АБВГ.123456-02", font=font, fill="black")
    skewed = image.crop((0, 0, image.width, bottom - 6)).rotate(
        1.4, Image.Resampling.BICUBIC, expand=True, fillcolor="white")
    skewed.transpose(Image.Transpose.ROTATE_90).save(output / "scan-skewed.pdf", resolution=200)


class PdfPen:
    def __init__(self, output):
        self.output = output

    def emit(self, operator, *points):
        coordinates = " ".join(f"{value:.3f}" for point in points for value in point)
        self.output.append(f"{coordinates} {operator}".strip())

    def moveTo(self, point):
        self.emit("m", point)

    def lineTo(self, point):
        self.emit("l", point)

    def curveTo(self, *points):
        self.emit("c", *points)

    def closePath(self):
        self.emit("h")

    def endPath(self):
        pass


def outlines(output):
    with TTFont(FONT) as font:
        glyphs = font.getGlyphSet()
        cmap = font.getBestCmap()
        scale = 14 / font["head"].unitsPerEm
        content = [
            "0.7 w", "55 14 525 156 re S", "239 14 m 239 170 l S",
            "239 128 m 580 128 l S", "437 14 m 437 128 l S",
            "437 71 m 580 71 l S", "239 57 m 437 57 l S"
        ]
        for value, y in [("ABCD.123456.001", 142), ("Main housing", 98)]:
            x = 255
            for character in value:
                glyph = glyphs[cmap[ord(character)]]
                path = SVGPathPen(glyphs)
                glyph.draw(path)
                if path.getCommands():
                    content.append(f"q {scale:.9f} 0 0 {scale:.9f} {x:.5f} {y} cm")
                    parse_path(path.getCommands(), Qu2CuPen(PdfPen(content), max_err=1, all_cubic=True))
                    content.append("f Q")
                x += glyph.width * scale
    writer = PdfWriter()
    page = writer.add_blank_page(595, 842)
    stream = DecodedStreamObject()
    stream.set_data("\n".join(content).encode("ascii"))
    page[NameObject("/Contents")] = writer._add_object(stream)
    writer.write(output / "outline-form1.pdf")


def inclined_scan(output, name, page_size=(155.52, 57.6), image_size=None):
    source = FOLDER / f"{name}.jpg"
    with Image.open(source) as image:
        width, height = image.size
        color_space = "/DeviceGray" if image.mode == "L" else "/DeviceRGB"

    writer = PdfWriter()
    page = writer.add_blank_page(*page_size)
    scan_image = DecodedStreamObject()
    scan_image.set_data(source.read_bytes())
    scan_image.update({
        NameObject("/Type"): NameObject("/XObject"),
        NameObject("/Subtype"): NameObject("/Image"),
        NameObject("/Width"): NumberObject(width),
        NameObject("/Height"): NumberObject(height),
        NameObject("/ColorSpace"): NameObject(color_space),
        NameObject("/BitsPerComponent"): NumberObject(8),
        NameObject("/Filter"): NameObject("/DCTDecode"),
    })
    page[NameObject("/Resources")] = DictionaryObject({
        NameObject("/XObject"): DictionaryObject({NameObject("/Scan"): writer._add_object(scan_image)})
    })
    stream = DecodedStreamObject()
    image_width, image_height = image_size or page_size
    stream.set_data(f"q {image_width} 0 0 {image_height} 0 0 cm /Scan Do Q".encode("ascii"))
    page[NameObject("/Contents")] = writer._add_object(stream)
    writer.write(output / f"{name}.pdf")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    output = parser.parse_args().output
    output.mkdir(parents=True, exist_ok=True)
    scan(output)
    # Keep the original scale and 1 pt top margin; resampling changes OCR.
    inclined_scan(output, "scan-gasket", (595, 842), (595, 841))
    inclined_scan(output, "scan-connector", (595, 842), (595, 841))
    for name in ("scan-assembly", "scan-nut", "scan-specification", "scan-sideways"):
        inclined_scan(output, name, (595, 842), (595, 841))
    source = PdfReader(output / "scan-form1.pdf")
    writer = PdfWriter()
    writer.add_page(source.pages[0]).rotate(90)
    writer.write(output / "scan-rotated.pdf")
    locked = PdfWriter()
    locked.add_page(source.pages[0])
    locked.encrypt("fixture-password", algorithm="RC4-128")
    locked.write(output / "protected.pdf")
    outlines(output)
    inclined_scan(output, "scan-italic")
    inclined_scan(output, "scan-italic-faded")


if __name__ == "__main__":
    main()
