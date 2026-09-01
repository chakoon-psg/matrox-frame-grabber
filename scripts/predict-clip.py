#!/usr/bin/env python3
"""Works out what the detector should report for a clip, by reducing the clip the same way it does.

A blackout clip needs no tool: the events are where the script put them. A motion clip does. The
question it asks -- does the tile median fall past the threshold while the tiles disagree -- has no
answer that can be worked out by looking at the generator, because it depends on how six moving
blocks happen to land on an 8x8 grid frame after frame. Guessing produces a clip that either never
loads the gate or never stops firing, and either way the run against it means nothing.

So this reduces the clip itself. ffmpeg scaling to 8x8 with area flags is exactly a tile mean, and
depth and coherence follow from the same formulas the app uses.

Two things it is not.

It is a prediction in code space, not light space. The clip is played on a monitor and filmed, so a
gamma curve and the camera response sit between these numbers and what the app measures -- the same
gap the depth staircase already documents. Treat the event count as the answer key and the depths
as approximate.

And it reimplements the detector's state machine rather than calling it. That is a copy, and copies
drift. It is here to write a ground truth, not to verify the app: a disagreement between the two is
a reason to look at both, never grounds to trust this one.

Needs only ffmpeg and the standard library.
"""
import argparse
import os
import statistics
import subprocess
import sys
import tempfile

TILES = 64


def tile_means(clip):
    """One 64-value tile grid per frame. Area scaling to 8x8 is a box average, i.e. a tile mean."""
    fd, path = tempfile.mkstemp(suffix=".gray")
    os.close(fd)
    try:
        subprocess.run(
            ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", clip,
             "-vf", "scale=8:8:flags=area,format=gray", "-f", "rawvideo", path],
            check=True)
        data = open(path, "rb").read()
    finally:
        try: os.remove(path)
        except OSError: pass

    if len(data) % TILES:
        sys.exit(f"{clip}: {len(data)} bytes is not a whole number of 8x8 frames")
    return [list(data[i:i + TILES]) for i in range(0, len(data), TILES)]


def coherence(before, after):
    """|sum of deltas| / sum of |deltas|, over the tiles. FrameMetrics.Coherence, in Python."""
    signed = sum(a - b for a, b in zip(after, before))
    magnitude = sum(abs(a - b) for a, b in zip(after, before))
    return abs(signed) / magnitude if magnitude > 0 else 0.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("clip")
    ap.add_argument("--depth", type=float, default=0.05)
    ap.add_argument("--coherence", type=float, default=0.80)
    ap.add_argument("--debounce", type=int, default=20)
    ap.add_argument("--window", type=int, default=91)
    ap.add_argument("--warmup", type=int, default=30)
    ap.add_argument("--fps", type=float, default=60.0)
    ap.add_argument("--phases", default="",
                    help="comma-separated phase boundaries in seconds, e.g. 4,24,44")
    args = ap.parse_args()

    grids = tile_means(args.clip)
    print(f"{os.path.basename(args.clip)}: {len(grids)} frames at {args.fps:g} fps "
          f"({len(grids) / args.fps:.1f} s)")
    print(f"  thresholds: depth {args.depth}, coherence {args.coherence}, "
          f"debounce {args.debounce}, window {args.window}\n")

    window = []
    baseline = 0.0
    observed = 0
    in_event = False
    clear = 0
    start = 0
    max_depth = 0.0
    max_coh = 0.0
    events = []
    trace = []          # (frame, depth, coherence, judged)

    previous = None
    for frame, grid in enumerate(grids):
        median = statistics.median(grid)
        depth = max(0.0, 1.0 - median / baseline) if baseline > 0 else 0.0
        coh = coherence(previous, grid) if previous is not None else 0.0
        judged = observed >= args.warmup

        if not judged:
            window.append(median)
        else:
            anomalous = depth > args.depth and (in_event or coh > args.coherence)
            if anomalous:
                if not in_event:
                    in_event, start, max_depth, max_coh = True, frame, 0.0, 0.0
                clear = 0
                max_depth = max(max_depth, depth)
                max_coh = max(max_coh, coh)
            else:
                window.append(median)
                if in_event:
                    clear += 1
                    if clear >= args.debounce:
                        events.append((start, frame - clear, max_depth, max_coh))
                        in_event = False

        if len(window) > args.window:
            del window[0]
        if window:
            baseline = statistics.median(window)
        observed += 1
        previous = grid
        trace.append((frame, depth, coh, judged))

    if in_event:
        events.append((start, len(grids) - 1, max_depth, max_coh))

    # ---- phases ----
    bounds = [0.0] + [float(v) for v in args.phases.split(",") if v] + [len(grids) / args.fps]
    print(f"{'phase (s)':>16} {'frames':>8} {'depth p50':>10} {'p95':>8} {'max':>8} "
          f"{'coh p50':>8} {'over both gates':>16}")
    for i in range(len(bounds) - 1):
        lo, hi = bounds[i], bounds[i + 1]
        rows = [t for t in trace if t[3] and lo <= t[0] / args.fps < hi]
        if not rows:
            continue
        depths = sorted(t[1] for t in rows)
        cohs = sorted(t[2] for t in rows)
        both = sum(1 for t in rows if t[1] > args.depth and t[2] > args.coherence)

        def p(values, f):
            return values[min(len(values) - 1, int(round(f * (len(values) - 1))))]

        print(f"{lo:7.1f}-{hi:<8.1f} {len(rows):8d} {p(depths, .5):10.4f} {p(depths, .95):8.4f} "
              f"{depths[-1]:8.4f} {p(cohs, .5):8.3f} {both:16d}")

    print(f"\npredicted events: {len(events)}")
    for start_frame, end_frame, d, c in events:
        print(f"  frames {start_frame}-{end_frame}  t={start_frame / args.fps:6.2f}s  "
              f"{(end_frame - start_frame + 1) * 1000 / args.fps:7.1f} ms  "
              f"depth {d:.3f}  coh {c:.3f}")

    # The load the gate actually took: frames deep enough to fire that only coherence turned away.
    saves = sum(1 for f, d, c, judged in trace
                if judged and d > args.depth and c <= args.coherence)
    print(f"\nframes deep enough to fire that coherence rejected: {saves}")
    print("  (this is the load. A clip where it is zero has not tested the gate at all.)")


if __name__ == "__main__":
    main()
