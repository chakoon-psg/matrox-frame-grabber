#!/usr/bin/env python3
"""Reads a measurement CSV and reports the depth and coherence distributions per channel.

This is the sampled half of the threshold question. The detector counts its own tail across every
frame -- the deepest fall on a normal frame, how many crowded the gate, how many coherence turned
away -- because the CSV holds one frame in sixty-two and a tail cannot be read from that. What the
CSV does hold is the shape: whether depth sits in a tight band or wanders, whether coherence on a
still panel is near zero or near one, and whether either drifts over a run.

Read the two together. The detector's maximum sets the floor a threshold has to clear; these
percentiles say whether that maximum was one freak frame or the edge of a crowded population.

Needs only the standard library.
"""
import argparse
import collections
import csv
import io
import statistics


def percentile(values, fraction):
    """Nearest-rank percentile. No interpolation: these are samples, not an estimate of a curve."""
    if not values:
        return 0.0
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, int(round(fraction * (len(ordered) - 1)))))
    return ordered[index]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv")
    ap.add_argument("--depth-threshold", type=float, default=0.10)
    ap.add_argument("--coherence-threshold", type=float, default=0.80)
    ap.add_argument("--warmup", type=int, default=6,
                    help="rows to drop at the start, while the baseline is still filling")
    args = ap.parse_args()

    rows = list(csv.DictReader(io.open(args.csv, encoding="utf-8-sig")))
    by = collections.OrderedDict()
    for row in rows:
        by.setdefault(row["channel"], []).append(row)

    print(f"{args.csv}\n")
    print(f"{'channel':10} {'n':>6} {'depth p50':>10} {'p95':>8} {'p99':>8} {'max':>8} "
          f"{'coh p50':>8} {'p95':>8} {'max':>8}")

    for channel, channel_rows in by.items():
        channel_rows = channel_rows[args.warmup:]
        # A frame with no baseline yet reports depth 0 and coherence 0; those rows say nothing
        # about the floor, so they are not part of the distribution.
        judged = [r for r in channel_rows if float(r["baseline"]) > 0]
        if not judged:
            print(f"{channel:10}   no judged frames in this file")
            continue

        depth = [float(r["depth"]) for r in judged]
        coherence = [float(r["coherence"]) for r in judged]

        print(f"{channel:10} {len(judged):6d} "
              f"{percentile(depth, 0.50):10.4f} {percentile(depth, 0.95):8.4f} "
              f"{percentile(depth, 0.99):8.4f} {max(depth):8.4f} "
              f"{percentile(coherence, 0.50):8.3f} {percentile(coherence, 0.95):8.3f} "
              f"{max(coherence):8.3f}")

    print("\nwhat the depth threshold would have to clear, from these samples")
    print("  (the detector's own per-frame maximum is the number that governs; this is the shape)")
    for channel, channel_rows in by.items():
        judged = [r for r in channel_rows[args.warmup:] if float(r["baseline"]) > 0]
        if not judged:
            continue
        depth = [float(r["depth"]) for r in judged]
        # Frames where the tiles agreed are the ones coherence would not have stopped -- but only
        # those below the threshold, because a sample above it either fired or is part of an event.
        # Including them measures the fault instead of the floor: one run reported a 0.1x margin
        # entirely because a 0.90 event frame was in the set.
        agreeing = [float(r["depth"]) for r in judged
                    if float(r["coherence"]) > args.coherence_threshold
                    and float(r["depth"]) < args.depth_threshold]
        worst = max(agreeing) if agreeing else 0.0
        margin = args.depth_threshold / worst if worst > 0 else float("inf")
        print(f"  {channel}: {len(agreeing)} of {len(judged)} samples had coherence above "
              f"{args.coherence_threshold:.2f};")
        print(f"      deepest of those {worst:.4f}"
              + (f", so a {args.depth_threshold:.2f} threshold has {margin:.1f}x margin"
                 if worst > 0 else ", so nothing sampled came close"))
        near = sum(1 for d in depth
                   if args.depth_threshold * 0.5 < d < args.depth_threshold)
        print(f"      {near} samples past half the threshold "
              f"({100.0 * near / len(judged):.2f}% of judged)")

    print("\ndrift over the run (does the floor hold, or does it climb?)")
    for channel, channel_rows in by.items():
        judged = [r for r in channel_rows[args.warmup:] if float(r["baseline"]) > 0]
        if len(judged) < 20:
            continue
        half = len(judged) // 2
        first = [float(r["depth"]) for r in judged[:half]]
        second = [float(r["depth"]) for r in judged[half:]]
        base_first = statistics.mean(float(r["baseline"]) for r in judged[:half])
        base_second = statistics.mean(float(r["baseline"]) for r in judged[half:])
        print(f"  {channel}: depth p95 {percentile(first, 0.95):.4f} -> "
              f"{percentile(second, 0.95):.4f};  "
              f"baseline {base_first:.2f} -> {base_second:.2f} "
              f"({100.0 * (base_second - base_first) / base_first:+.2f}%)")


if __name__ == "__main__":
    main()
