#!/usr/bin/env bash
# A campaign of one-hour runs with all seven kinds on.
#
# The previous version of this script ran sixteen hours against a board another instance
# was holding, exited zero every time, and produced nothing. Two things stop that now:
# the app refuses an autostart with no camera (exit 2), and this loop stops on the first
# run that does not leave a stop summary rather than carrying on for another fifteen hours.
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
  echo "=== hour $i of $HOURS, $(date) ==="
  before=$(grep -c "grab stopped" "$LOG")

  "$EXE" --autostart "$SECONDS_EACH"
  rc=$?

  after=$(grep -c "grab stopped" "$LOG")
  if [ "$rc" -eq 2 ]; then
    echo "ABORT: the app refused to start - no camera. See mil-errors.log"
    exit 2
  fi
  if [ "$after" -le "$before" ]; then
    echo "ABORT: hour $i wrote no stop summary (exit $rc). Something grabbed nothing."
    grep -E "cameras present" "$LOG" | tail -2
    exit 3
  fi
  echo "hour $i ok: $((after - before)) channels stopped"
done
echo "campaign done, $(date)"
