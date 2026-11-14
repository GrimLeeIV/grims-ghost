"""Text recognition with Tesseract (optional, runs entirely on this PC)."""

import subprocess

from . import util


class OcrError(Exception):
    pass


def available():
    return bool(util.which("tesseract"))


def read_png(png_bytes, lang="eng"):
    """Returns (text, line_count, language)."""
    if not available():
        raise OcrError("Text recognition needs Tesseract. Install the 'tesseract-ocr' (or 'tesseract') package, then try again.")
    try:
        r = subprocess.run(["tesseract", "stdin", "stdout", "-l", lang or "eng", "--psm", "3"],
                           input=png_bytes, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
    except subprocess.TimeoutExpired:
        raise OcrError("Text recognition took too long.")
    if r.returncode != 0:
        msg = r.stderr.decode("utf-8", "replace").strip().splitlines()
        raise OcrError("Tesseract couldn't read it: " + (msg[-1] if msg else f"exit {r.returncode}"))
    text = r.stdout.decode("utf-8", "replace").replace("\f", "")
    lines = [l.rstrip() for l in text.splitlines()]
    while lines and not lines[-1]:   # tidy: no trailing blanks, at most one empty line in a row
        lines.pop()
    tidy = []
    for l in lines:
        if l or (tidy and tidy[-1]):
            tidy.append(l)
    text = "\n".join(tidy)
    return text, sum(1 for l in tidy if l.strip()), lang or "eng"
