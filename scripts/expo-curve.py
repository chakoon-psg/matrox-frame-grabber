#!/usr/bin/env python3
"""Reads an --expo-scan measurement CSV and reports the flicker curve against exposure.

What this is for. A display dimmed by PWM reaches the sensor through the exposure window, and a
box integration of length T over a periodic signal of frequency f is scaled by |sinc(pi f T)|.
That is zero whenever f*T is a whole number, so the exposures at which the measured brightness
stops wandering are exactly the exposures that cancel the flicker. Reading the minima off the
curve gives the candidate frequencies: f = k / T_null.

Two exposures cannot do this. A pair can always be joined by either an additive or a
multiplicative story, and the first pair measured here was read the wrong way round for exactly
that reason. Three or more points show the shape, and the shape is not monotonic.

The wander is measured as the standard deviation of the region luma over the hold, divided by its
mean, so it is comparable across exposures whose brightness differs by three times.
"""
import argparse
import collections
import csv
import io
import math
import statistics

SETTLE_ROWS = 6   # rows to drop at the start of each hold, while the new exposure reaches the buffer


def load(path, drop):
    """Splits the rows into holds: one contiguous stretch at one exposure on one channel.

    Grouping by exposure value alone is wrong, and wrongly so in a way that hides itself. A scan
    may visit the same exposure twice -- deliberately, as a repeatability check, or because the
    list ends where the camera should be left -- and pooling two holds minutes apart reports their
    combined spread as if it were one hold's wander. Measured: the two 8000 us holds of one scan
    pooled to 4.55% on a channel whose holds were 1.7% and 1.6% separately.

    Returns a list of (channel, exposure_us, hold_index, rows), in file order.
    """
    holds = []
    current = {}
    counts = collections.Counter()
    for row in csv.DictReader(io.open(path, encoding="utf-8-sig")):
        if not row["luma"]:
            continue
        ch = row["channel"]
        us = int(float(row["exposure_us"]))
        if ch in current and current[ch][0] == us:
            current[ch][1].append(row)
            continue
        if ch in current:
            holds.append((ch, current[ch][0], counts[ch], current[ch][1]))
            counts[ch] += 1
        current[ch] = (us, [row])
    for ch in current:
        holds.append((ch, current[ch][0], counts[ch], current[ch][1]))

    return [(ch, us, i, rows[drop:]) for ch, us, i, rows in holds if len(rows) > drop]


def sinc_abs(x):
    """|sinc(pi x)| with the removable singularity at 0 filled in."""
    if abs(x) < 1e-12:
        return 1.0
    return abs(math.sin(math.pi * x) / (math.pi * x))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv")
    ap.add_argument("--drop", type=int, default=SETTLE_ROWS,
                    help=f"rows to drop at the start of each hold (default {SETTLE_ROWS})")
    args = ap.parse_args()

    holds = load(args.csv, args.drop)
    channels = sorted({ch for ch, _, _, _ in holds})
    by_hold = {(ch, i): (us, rows) for ch, us, i, rows in holds}
    hold_indices = sorted({i for _, _, i, _ in holds})

    # --- the curve -------------------------------------------------------------------
    print("relative wander (1 sigma of region luma / mean), per cent\n")
    header = "  #  exp_us " + "".join(f"{ch.replace('Camera ', 'CAM'):>10}" for ch in channels)
    print(header + f"{'mean':>10}{'fps':>8}{'luma':>8}")
    curve = []
    for i in hold_indices:
        cells, us, fps, lum = [], None, None, None
        for ch in channels:
            entry = by_hold.get((ch, i))
            if entry is None:
                cells.append(None)
                continue
            us, rows = entry
            values = [float(r["luma"]) for r in rows]
            cells.append(statistics.pstdev(values) / statistics.mean(values) * 100.0)
            if fps is None:
                fps = statistics.median(float(r["fps"]) for r in rows)
                lum = statistics.mean(values)
        seen = [c for c in cells if c is not None]
        if not seen or us is None:
            continue
        wander = statistics.mean(seen)
        curve.append((us, wander))
        print(f"  {i:<3d}{us:6d} "
              + "".join("        --" if c is None else f"{c:10.2f}" for c in cells)
              + f"{wander:10.2f}{fps:8.1f}{lum:8.1f}")

    # A repeated exposure is the honest check on everything else here: two holds at the same
    # exposure that disagree mean the scene was not still, and then no curve read off it means much.
    repeats = collections.defaultdict(list)
    for us, w in curve:
        repeats[us].append(w)
    shaky = {us: ws for us, ws in repeats.items() if len(ws) > 1}
    if shaky:
        print()
        print("repeated exposures - the scan own control:")
        for us, ws in sorted(shaky.items()):
            spread = max(ws) - min(ws)
            verdict = "consistent" if spread < 0.5 * min(ws) else "NOT REPEATABLE - the scene moved"
            print(f"  {us} us: " + ", ".join(f"{w:.2f}%" for w in ws) + f"   {verdict}")

    # --- minima ----------------------------------------------------------------------
    print("\nlocal minima (a null cancels the flicker; f = k / T_null):")
    seen_us, pts = set(), []
    for us, w in curve:                # first hold per exposure: a repeat is not a real wiggle
        if us not in seen_us:
            seen_us.add(us)
            pts.append((us, w))
    pts.sort()
    minima = [pts[i][0] for i in range(1, len(pts) - 1)
              if pts[i][1] < pts[i - 1][1] and pts[i][1] < pts[i + 1][1]]
    if not minima:
        print("  none inside the scanned range -- widen it")
    wander_at = dict(pts)
    for us in minima:
        cands = ", ".join(f"k={k} -> {k / (us * 1e-6):.0f} Hz" for k in range(1, 5))
        print(f"  {us} us  (wander {wander_at[us]:.2f}%)   {cands}")

    # --- which frequency explains the whole curve ------------------------------------
    print("\nbest fit over the whole curve, |sinc(pi f T)| scaled to the data:")
    best = []
    for f10 in range(500, 5001):            # 50.0 .. 500.0 Hz in 0.1 Hz steps
        f = f10 / 10.0
        model = [sinc_abs(f * us * 1e-6) for us, _ in pts]
        obs = [w for _, w in pts]
        denom = sum(m * m for m in model)
        if denom <= 0:
            continue
        gain = sum(m * o for m, o in zip(model, obs)) / denom
        resid = sum((o - gain * m) ** 2 for m, o in zip(model, obs))
        total = sum((o - statistics.mean(obs)) ** 2 for o in obs)
        best.append((resid, f, gain, 1.0 - resid / total if total > 0 else 0.0))
    best.sort()
    for resid, f, gain, r2 in best[:6]:
        print(f"  {f:7.1f} Hz   modulation {gain:5.2f}%   R2 {r2:6.3f}")

    if best:
        _, f, gain, r2 = best[0]
        print(f"\n  best: {f:.1f} Hz (R2 {r2:.3f}).  nulls at "
              + ", ".join(f"{k / f * 1e6:.0f} us" for k in range(1, 4))
              + f"  -> fps {1e6 / (1 / f * 1e6 + 45.0):.1f} at k=1")


if __name__ == "__main__":
    main()
