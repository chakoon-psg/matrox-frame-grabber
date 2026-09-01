#!/usr/bin/env python3
"""Finds the lit edges of the panel in a snapshot and compares them with the detection region.

Why. The tile map showed the bottom row of every region sitting at luma 6-8 while the rest of the
region ran 30-100: the region reaches past the bottom of the screen onto unlit bezel. Those tiles
cannot contribute -- there is no signal in them to dim -- so they cost detector coverage for
nothing. A region is also worth growing if the panel extends past it.

Rather than judge that off a picture, this reads the row and column mean profiles around the
current region and reports where the light actually starts and stops, so the trim is a number.

Needs only ffmpeg and the standard library.
"""
import argparse
import os
import subprocess
import sys
import tempfile

# Luma below which a row or column carries no signal worth judging. Well clear of the unlit
# background measured at 6-8 DN, and below the ~26 DN a tile needs for a 10% depth to be worth
# four sigma of the 0.65 DN additive floor -- so the answer is "lit or not", not "usable or not".
LIT_FLOOR = 20.0


def read_gray(png, w, h):
    fd, path = tempfile.mkstemp(suffix=".gray")
    os.close(fd)
    try:
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", png,
                        "-vf", "format=gray", "-f", "rawvideo", path], check=True)
        data = open(path, "rb").read()
    finally:
        try: os.remove(path)
        except OSError: pass
    if len(data) != w * h:
        sys.exit(f"{png}: {len(data)} bytes, expected {w*h} for {w}x{h}")
    return data


def row_means(data, w, x0, x1, y0, y1):
    return [sum(data[y * w + x0:y * w + x1]) / (x1 - x0) for y in range(y0, y1)]


def col_means(data, w, x0, x1, y0, y1):
    return [sum(data[y * w + x] for y in range(y0, y1)) / (y1 - y0) for x in range(x0, x1)]


def panel_edges(profile, start, fraction=0.25):
    """The panel's first and last index, found by scanning inward from each end.

    Not the longest lit run, which is what this used to do and which gets the answer wrong on a
    real screen: an AVN display has dark bands inside it -- a bezel line between areas, a black UI
    strip -- and the longest-run rule cuts the region at the first of them, throwing away lit
    panel on the far side. Measured: a band at luma 11 ten rows inside the region was read as the
    panel's top edge while rows above it ran 28-52.

    Scanning inward instead only ever finds the outermost transition, so interior structure cannot
    move it. The background is taken as the profile's floor, and the edge is where the profile
    first rises a quarter of the way from that floor to its peak.
    """
    if not profile:
        return None
    floor = min(profile)
    peak = max(profile)
    if peak - floor <= 0:
        return None
    bar = floor + fraction * (peak - floor)

    first = next((i for i, v in enumerate(profile) if v >= bar), None)
    last = next((i for i in range(len(profile) - 1, -1, -1) if profile[i] >= bar), None)
    if first is None or last is None:
        return None
    return start + first, start + last


def bar(v, peak):
    return "#" * int(round(40 * v / peak)) if peak > 0 else ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("png")
    ap.add_argument("--region", required=True, metavar="WxH+X+Y")
    ap.add_argument("--frame", default="1024x772")
    ap.add_argument("--look", type=int, default=80,
                    help="pixels to inspect beyond the region on each side (default 80)")
    ap.add_argument("--floor", type=float, default=LIT_FLOOR)
    ap.add_argument("--profile", action="store_true", help="print the full profiles")
    args = ap.parse_args()

    fw, fh = (int(v) for v in args.frame.split("x"))
    size, rx, ry = args.region.split("+")
    rw, rh = (int(v) for v in size.split("x"))
    rx, ry = int(rx), int(ry)

    data = read_gray(args.png, fw, fh)

    # Look outside the region as well: the panel may extend past it.
    sx0, sx1 = max(0, rx - args.look), min(fw, rx + rw + args.look)
    sy0, sy1 = max(0, ry - args.look), min(fh, ry + rh + args.look)

    rows = row_means(data, fw, rx, rx + rw, sy0, sy1)
    cols = col_means(data, fw, sx0, sx1, ry, ry + rh)

    print(f"{os.path.basename(args.png)}   region {rw}x{rh} @ {rx},{ry}   "
          f"inspected {sx0}..{sx1} x {sy0}..{sy1}")

    vspan = panel_edges(rows, sy0)
    hspan = panel_edges(cols, sx0)
    if vspan is None or hspan is None:
        sys.exit("  nothing above the floor -- wrong region, or the panel was dark")

    print(f"  panel rows    {vspan[0]}..{vspan[1]}   (region has {ry}..{ry + rh - 1})")
    print(f"  panel columns {hspan[0]}..{hspan[1]}   (region has {rx}..{rx + rw - 1})")

    # Keep the region inside the lit area on every side. Trimming is safe; growing is only
    # suggested, because the region was aimed at the part of the screen that matters and the
    # rest of the panel may be deliberately outside it.
    nx0, nx1 = max(rx, hspan[0]), min(rx + rw - 1, hspan[1])
    ny0, ny1 = max(ry, vspan[0]), min(ry + rh - 1, vspan[1])
    nw, nh = nx1 - nx0 + 1, ny1 - ny0 + 1

    # ChannelRoi keeps offsets and sizes even, so round inwards to stay inside the light.
    nx0 += nx0 % 2
    ny0 += ny0 % 2
    nw -= nw % 2
    nh -= nh % 2

    print(f"\n  region trimmed to the light: {nw}x{nh} @ {nx0},{ny0}")
    if (nw, nh, nx0, ny0) == (rw, rh, rx, ry):
        print("  -> already inside the lit area on every side; nothing to trim")
    else:
        print(f"  -> drops {rh - nh} rows and {rw - nw} columns "
              f"({100 * (1 - nw * nh / (rw * rh)):.0f}% of the area, all of it unlit)")

    grow = []
    if hspan[0] < rx: grow.append(f"{rx - hspan[0]} px on the left")
    if hspan[1] > rx + rw - 1: grow.append(f"{hspan[1] - (rx + rw - 1)} px on the right")
    if vspan[0] < ry: grow.append(f"{ry - vspan[0]} px above")
    if vspan[1] > ry + rh - 1: grow.append(f"{vspan[1] - (ry + rh - 1)} px below")
    if grow:
        print("  panel also extends " + ", ".join(grow) + " (not applied -- your call)")

    if args.profile:
        peak = max(max(rows), max(cols))
        print("\n  row profile (y: mean over the region's columns)")
        for i, v in enumerate(rows):
            y = sy0 + i
            mark = "|" if ry <= y < ry + rh else " "
            print(f"   {y:4d}{mark}{v:7.1f}  {bar(v, peak)}")


if __name__ == "__main__":
    main()
