#!/usr/bin/env python3
"""Reads an event window written by TileHistory and says what shape the darkening had.

The rig keeps producing events where all three panels fall 75-90% together for around 800 ms, at
times unrelated to anything being played. Whether those are a fault or something in the room
decides what the detection rate even means, and it cannot be settled by the depth and duration in
the log -- those are the same for every cause.

The 64 tiles at full frame rate do settle it, because the causes differ in where and when:

    a panel switching off      every tile falls together, within a frame or two
    something passing in front tiles fall in order, spread over many frames, and by position
    room lighting or power     every tile falls together, and so does every camera
    content changing           tiles move in different directions

So this reports the onset spread -- how many frames separate the first tile crossing halfway down
from the last -- alongside a map of how far each tile fell and when. A tight spread with an even
map is a real dimming of the whole surface. A spread of tens of frames with a gradient across the
map is something moving.

Compare the same event across channels afterwards: a common cause lands on all three at the same
board timestamp, which is the one clock they share.

Needs only the standard library.
"""
import argparse
import csv
import io
import os
import statistics

COLUMNS = 8
ROWS = 8
TILES = COLUMNS * ROWS


def load(path):
    frames, times, grids = [], [], []
    for row in csv.reader(io.open(path, encoding="utf-8-sig")):
        if not row or row[0] == "frame":
            continue
        frames.append(int(row[0]))
        times.append(float(row[1]))
        grids.append([float(v) if v else None for v in row[2:2 + TILES]])
    return frames, times, grids


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv")
    ap.add_argument("--fraction", type=float, default=0.5,
                    help="onset is when a tile has covered this much of its OWN fall (default half). "
                         "A fixed depth would not do: on a 46%% event a 50%% criterion caught 7 tiles "
                         "of 64 and the spread was then measured from those 7")
    ap.add_argument("--min-fall", type=float, default=0.05,
                    help="ignore tiles that never fell this far; their onset is noise (default 5%%)")
    args = ap.parse_args()

    frames, times, grids = load(args.csv)
    if len(frames) < 10:
        raise SystemExit(f"{args.csv}: only {len(frames)} frames, too few to read a shape from")

    span = times[-1] - times[0]
    fps = (len(frames) - 1) / span if span > 0 else 0.0
    print(f"{os.path.basename(args.csv)}: frames {frames[0]}-{frames[-1]} "
          f"({len(frames)}), {span * 1000:.0f} ms, {fps:.1f} fps\n")

    # Baseline per tile from the quietest quarter of the window, which is the preroll.
    lead = max(5, len(frames) // 4)
    base = []
    for i in range(TILES):
        vals = [g[i] for g in grids[:lead] if g[i] is not None]
        base.append(statistics.median(vals) if vals else None)

    # --- the median over time, so the event shape is visible at all ---
    medians = []
    for g in grids:
        vals = [v for v in g if v is not None]
        medians.append(statistics.median(vals) if vals else None)
    quiet = statistics.median([m for m in medians[:lead] if m is not None])
    deepest = min((m for m in medians if m is not None), default=quiet)
    print(f"  region median {quiet:.1f} -> {deepest:.1f}  "
          f"(depth {1 - deepest / quiet:.3f})" if quiet else "  no baseline")

    # --- when each tile crossed halfway down, and how far it fell ---
    # Two passes: how far each tile eventually fell, then when it covered half of that. Scale-free,
    # so a 12% event and a 90% one are read the same way.
    onset = [None] * TILES
    fell = [0.0] * TILES
    for i in range(TILES):
        if base[i] is None or base[i] <= 0:
            continue
        seen = [g[i] for g in grids if g[i] is not None]
        if not seen:
            continue
        fell[i] = 1.0 - min(seen) / base[i]

    for i in range(TILES):
        if base[i] is None or base[i] <= 0 or fell[i] < args.min_fall:
            continue
        floor = base[i] * (1.0 - args.fraction * fell[i])
        for n, g in enumerate(grids):
            if g[i] is not None and g[i] <= floor:
                onset[i] = n
                break

    crossed = [n for n in onset if n is not None]
    print(f"  tiles that fell at least {args.min_fall:.0%}: {len(crossed)}/{TILES}"
          f"   (onset taken at {args.fraction:.0%} of each tile's own fall)")

    if len(crossed) >= 2:
        spread = max(crossed) - min(crossed)
        ms = spread * 1000.0 / fps if fps > 0 else 0.0
        print(f"  onset spread: {spread} frames ({ms:.0f} ms) "
              f"between the first tile and the last")
        verdict = ("the whole surface went together - a real dimming, not something moving"
                   if ms <= 40 else
                   "the fall swept across - something moved in front of it" if ms >= 150 else
                   "between the two; read the maps below")
        print(f"  -> {verdict}")
    elif len(crossed) == 1:
        print("  only one tile fell: a local change, not a dimming of the panel")
    else:
        print("  no tile fell that far: the event was shallower than --fraction")

    def show(title, values, fmt, blank):
        print(f"\n  {title}")
        for r in range(ROWS):
            cells = []
            for c in range(COLUMNS):
                v = values[r * COLUMNS + c]
                cells.append(blank if v is None else format(v, fmt))
            print("   " + "".join(f"{x:>7}" for x in cells))

    show("how far each tile fell", [f if f > 0 else None for f in fell], ".0%", "-")
    show("frame each tile crossed, relative to the first",
         [None if n is None else n - min(crossed) for n in onset] if crossed else [None] * TILES,
         "d", "-")


if __name__ == "__main__":
    main()
