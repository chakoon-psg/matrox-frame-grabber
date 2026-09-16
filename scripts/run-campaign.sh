#!/usr/bin/env bash
# A campaign of one-hour runs with all seven kinds on.
#
# Two failures shaped this script, both of them silent.
#
# Sixteen hours once ran against a board another instance was holding: every run exited
# zero, logged "grabbing for 3600s", allocated no camera and wrote no summary. The app
# refuses that now (exit 2), and this loop stops on the first run that leaves no summary
# rather than carrying on for another fifteen hours.
#
# Then the check for "left a summary" cried wolf. It counted "grab stopped" lines and
# compared before with after - and mil-errors.log rotates, so an hour that worked
# perfectly came back with fewer lines than it started with and was called a failure.
# The count is gone; the check now looks for a summary stamped after the run began.
#
# Nothing else must run on this machine while this goes. A build on the same box is a
# better explanation for a handful of lost frames than anything in the detectors.
set -u

EXE="src/bin/x64/Release/net10.0-windows/MatroxFrameGrabber.exe"
LOG="$LOCALAPPDATA/MatroxFrameGrabber/mil-errors.log"
HOURS="${1:-4}"
SECONDS_EACH="${2:-3600}"

if tasklist 2>/dev/null | grep -qi matroxframegrabber; then
  echo "ABORT: a MatroxFrameGrabber.exe is already running - it would take some of the cameras"
  exit 1
fi

for i in $(seq 1 "$HOURS"); do
  started=$(date '+%Y-%m-%d %H:%M:%S')
  echo "=== hour $i of $HOURS, $started ==="

  "$EXE" --autostart "$SECONDS_EACH"
  rc=$?

  if [ "$rc" -eq 2 ]; then
    echo "ABORT: the app refused to start - no camera. See mil-errors.log"
    exit 2
  fi

  # A summary stamped at or after this run began. Timestamps survive rotation; counts do not.
  stops=$(awk -v since="$started" '
      /grab stopped/ {
        stamp = substr($0, 1, 19)
        if (stamp >= since) n++
      }
      END { print n + 0 }' "$LOG")

  if [ "$stops" -eq 0 ]; then
    echo "ABORT: hour $i wrote no stop summary since $started (exit $rc). Something grabbed nothing."
    grep -E "cameras present" "$LOG" | tail -2
    exit 3
  fi
  echo "hour $i ok: $stops channel(s) stopped"
done
echo "campaign done, $(date)"
