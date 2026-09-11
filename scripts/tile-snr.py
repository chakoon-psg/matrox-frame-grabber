#!/usr/bin/env python3
"""Reads the detection region out of a snapshot PNG and reports the 8x8 tile brightness spread.

Why this exists: the brightness meter reports one number for the whole region, and that number
came out at luma 17-29 -- too dark for a 10% depth threshold to clear the measured noise. But the
detector does not work on the region mean, it works on tile medians. A region whose mean is dim
because most of it is dark background can still hold tiles bright enough to judge, and the region
mean cannot tell the two cases apart. This can.

The temporal noise term is supplied on the command line, because it cannot be measured from a
single frame -- it comes from the measurement CSV (1 sigma of the region luma over a run, which
came out at 0.7-0.9 DN and did not move when the exposure changed, i.e. it is additive).

Needs only ffmpeg and the standard library: this machine has neither PIL nor numpy.
"""
import argparse
import os
import statistics
import subprocess
import sys
import tempfile

COLUMNS = 8
ROWS = 8


def read_region_gray(png, x, y, w, h):
    """ffmpeg crops and converts; we read the raw gray8 plane back as one bytes object."""
    fd, path = tempfile.mkstemp(suffix=".gray")
    os.close(fd)
    try:
        subprocess.run(
            ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", png,
             "-vf", f"crop={w}:{h}:{x}:{y},format=gray", "-f", "rawvideo", path],
            check=True)
        data = open(path, "rb").read()
    finally:
        try: os.remove(path)
        except OSError: pass

    if len(data) != w * h:
        sys.exit(f"{png}: got {len(data)} bytes, expected {w*h} -- is the region inside the frame?")
    return data


def tile_bounds(total, divisions):
    """Even split with the remainder spread over the leading tiles, so no tile is empty."""
    edges = [(i * total) // divisions for i in range(divisions + 1)]
    return list(zip(edges[:-1], edges[1:]))


def tile_means(data, w, h):
    cols = tile_bounds(w, COLUMNS)
    rows = tile_bounds(h, ROWS)
    out = []
    for y0, y1 in rows:
        for x0, x1 in cols:
            total = 0
            count = 0
            for y in range(y0, y1):
                base = y * w
                total += sum(data[base + x0:base + x1])
                count += x1 - x0
            out.append(total / count)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("png")
    ap.add_argument("--region", required=True, metavar="WxH+X+Y",
                    help="detection region, e.g. 308x182+380+282")
    ap.add_argument("--noise-abs", type=float, default=0.0,
                    help="brightness-independent 1 sigma in DN (read noise and the like)")
    ap.add_argument("--noise-rel", type=float, default=0.0,
                    help="brightness-proportional 1 sigma as a fraction, e.g. 0.0175 for 1.75%%. "
                         "This is the flicker term: it does not shrink when the picture gets "
                         "brighter, so past a point no exposure buys any more margin")
    ap.add_argument("--depth", type=float, default=0.10,
                    help="depth threshold being considered (default 0.10)")
    ap.add_argument("--margin", type=float, default=4.0,
                    help="how many sigma the threshold must clear (default 4)")
    args = ap.parse_args()

    size, x, y = args.region.split("+")
    w, h = (int(v) for v in size.split("x"))
    x, y = int(x), int(y)

    data = read_region_gray(args.png, x, y, w, h)
    means = tile_means(data, w, h)

    # The brightness a tile must reach for `depth` to be worth `margin` sigma of the noise.
    #
    # Two noise terms, and they behave completely differently. The additive one (read noise) is a
    # fixed number of DN, so a brighter picture outruns it. The relative one (PWM flicker reaching
    # the sensor through the exposure window) is a fixed fraction of the signal, so brightness buys
    # nothing against it at all -- if margin x relative already exceeds the depth threshold, there
    # is no exposure that makes the threshold safe and the only fix is to null the flicker.
    #
    #   depth * luma  >  margin * sqrt(abs^2 + (rel * luma)^2)
    floor = args.depth ** 2 - (args.margin * args.noise_rel) ** 2
    if floor <= 0:
        needed = float("inf")
    elif args.noise_abs <= 0:
        needed = 0.0
    else:
        needed = args.margin * args.noise_abs / (floor ** 0.5)

    print(f"{os.path.basename(args.png)}  region {w}x{h} @ {x},{y}  "
          f"tile {w // COLUMNS}x{h // ROWS} px")
    print(f"  region mean {statistics.mean(means):6.2f}   "
          f"tile min {min(means):6.2f}   median {statistics.median(means):6.2f}   "
          f"max {max(means):6.2f}")
    print(f"  noise: {args.noise_abs:.2f} DN additive + {args.noise_rel:.2%} of signal (flicker)")
    if needed == float("inf"):
        print(f"  a {args.depth:.0%} depth CANNOT reach {args.margin:.0f} sigma at any brightness: "
              f"{args.margin:.0f} x {args.noise_rel:.2%} = {args.margin * args.noise_rel:.2%} "
              f"already exceeds it. Null the flicker or raise the threshold.")
    else:
        print(f"  a {args.depth:.0%} depth is worth {args.margin:.0f} sigma above luma {needed:.1f}")

    usable = sum(1 for m in means if m >= needed)
    print(f"  tiles clearing that: {usable}/{len(means)}  ({usable / len(means):.0%})")

    print("\n  tile map (luma, '*' = clears the threshold):")
    for r in range(ROWS):
        row = means[r * COLUMNS:(r + 1) * COLUMNS]
        cells = "".join(f"{m:6.0f}{'*' if m >= needed else ' '}" for m in row)
        print("   " + cells)


if __name__ == "__main__":
    main()
