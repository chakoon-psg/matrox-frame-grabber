#!/usr/bin/env bash
#
# Builds the validation clips for AVN anomaly detection.
#
# The detector does not know it is looking at an AVN. It only sees brightness fall by some amount
# for some length of time — so anything that darkens on cue validates it, and a screen playing a
# video is the cheapest source of events whose timing we already know.
#
# Every clip is 60 fps, so one frame is 16.67 ms. That is also the shortest event this method can
# produce: the monitor quantises everything to its own refresh. Sub-millisecond events need an LED
# driven by a microcontroller, which is a separate exercise and only worth doing if the events seen
# here turn out to be shorter than a frame.
#
# Usage:  bash scripts/make-test-clips.sh [outdir]

set -euo pipefail

OUT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/docs/measurements/test-clips}"

# ---- ffmpeg: the same search order the app uses, minus the configured path ----
find_ffmpeg() {
  local c
  for c in "$(command -v ffmpeg || true)" \
           "/c/ffmpeg/bin/ffmpeg.exe" \
           "$HOME/AppData/Local/Microsoft/WinGet/Links/ffmpeg.exe"; do
    [ -n "$c" ] && [ -f "$c" ] && { printf '%s' "$c"; return 0; }
  done
  # WinGet keeps the real binary under Packages/, and the Links shim is not always there.
  c=$(ls -d "$HOME"/AppData/Local/Microsoft/WinGet/Packages/Gyan.FFmpeg*/*/bin/ffmpeg 2>/dev/null | head -1 || true)
  [ -n "$c" ] && { printf '%s' "$c"; return 0; }
  return 1
}

FFMPEG=$(find_ffmpeg) || { echo "ffmpeg를 찾을 수 없습니다." >&2; exit 1; }
echo "ffmpeg: $FFMPEG"

mkdir -p "$OUT"

# ---- Shape of every clip ----
W=1920
H=1080
FPS=60
DUR=60            # seconds
LEADIN=240        # frames (4 s) of steady grey before the first event, so exposure can settle
PERIOD=120        # frames between events (2 s) — wide enough that no two events can merge

# Mid grey, not white. A white field clips at any sensible exposure, and a clipped pixel has its
# ripple cut off at 255 — the same trap the PWM sweep warns about. 128 sits in the middle of the
# 60-180 band the brightness legend wants.
GREY=0x808080

# Lossless. A single black frame among grey ones is exactly the kind of detail a lossy encoder
# smooths, and a smoothed edge would understate the depth the detector is supposed to measure.
ENC=(-c:v libx264 -qp 0 -pix_fmt yuv420p -preset veryfast)

# blank_clip NAME FRAMES — grey field with FRAMES-long blackouts every PERIOD frames.
blank_clip() {
  local name="$1" frames="$2"
  local expr="gte(n\,${LEADIN})*lt(mod(n\,${PERIOD})\,${frames})"

  echo "  $name — ${frames}프레임 ($(awk -v f="$frames" 'BEGIN{printf "%.1f", f*1000/60}') ms) 소등"
  "$FFMPEG" -hide_banner -loglevel error -y \
    -f lavfi -i "color=c=${GREY}:s=${W}x${H}:r=${FPS}:d=${DUR}" \
    -vf "drawbox=x=0:y=0:w=iw:h=ih:color=black@1:t=fill:enable='${expr}'" \
    "${ENC[@]}" "$OUT/$name"
}

echo "생성 중 → $OUT"

# ---- 1. The control. Arguably the most useful clip here ----
# Nothing happens in it. Run the detector over this and whatever it reports is a false positive;
# the largest depth it sees is the floor the threshold has to clear. This is how the threshold gets
# set when no faulty unit exists to produce a true positive.
echo "  control-steady.mp4 — 사건 없음 (오검출 바닥 측정용)"
"$FFMPEG" -hide_banner -loglevel error -y \
  -f lavfi -i "color=c=${GREY}:s=${W}x${H}:r=${FPS}:d=${DUR}" \
  "${ENC[@]}" "$OUT/control-steady.mp4"

# ---- 2-4. Known blackouts, one duration per clip ----
# One duration per clip rather than a mixed sequence: matching a detector's output against ground
# truth is a counting exercise, and a clip where every event is the same length makes a wrong count
# obvious at a glance.
blank_clip "blank-1frame.mp4" 1
blank_clip "blank-2frame.mp4" 2
blank_clip "blank-5frame.mp4" 5

# ---- 5. The negative test ----
# A bright box crossing a grey field. Brightness inside individual tiles swings hard, but the tiles
# disagree about which way — which is exactly what the spatial coherence gate exists to reject.
# A detector that fires on this would fire on any animation an AVN plays.
echo "  motion-reject.mp4 — 밝은 물체 이동 (검출되면 안 됨)"
"$FFMPEG" -hide_banner -loglevel error -y \
  -f lavfi -i "color=c=${GREY}:s=${W}x${H}:r=${FPS}:d=${DUR}" \
  -vf "drawbox=x='mod(t*400\,${W}+400)-400':y=240:w=400:h=600:color=white@1:t=fill" \
  "${ENC[@]}" "$OUT/motion-reject.mp4"

# ---- Ground truth, written next to the clips ----
FIRST_S=$(awk -v l="$LEADIN" -v f="$FPS" 'BEGIN{printf "%.2f", l/f}')
GAP_S=$(awk -v p="$PERIOD" -v f="$FPS" 'BEGIN{printf "%.2f", p/f}')
COUNT=$(awk -v d="$DUR" -v f="$FPS" -v l="$LEADIN" -v p="$PERIOD" \
  'BEGIN{print int((d*f - l - 1)/p) + 1}')

cat > "$OUT/README.md" <<EOF
# 검지 검증용 시험 영상

\`scripts/make-test-clips.sh\`로 생성. 다시 만들려면 그 스크립트를 실행할 것.

전부 ${W}x${H}, ${FPS} fps, ${DUR}초, 무손실 H.264. 배경은 중간 회색(${GREY}) —
흰 배경은 어떤 노출에서도 포화되고, 포화된 화소는 리플이 255에서 잘려 depth를 왜곡한다.

## 정답 (ground truth)

| 파일 | 사건 길이 | 첫 사건 | 간격 | 개수 | 기대 결과 |
|---|---|---|---|---|---|
| \`control-steady.mp4\` | 없음 | — | — | 0 | **검출 0** |
| \`blank-1frame.mp4\` | 16.7 ms | ${FIRST_S}s | ${GAP_S}s | ${COUNT} | ${COUNT}회, depth ≈ 1.0 |
| \`blank-2frame.mp4\` | 33.3 ms | ${FIRST_S}s | ${GAP_S}s | ${COUNT} | ${COUNT}회, depth ≈ 1.0 |
| \`blank-5frame.mp4\` | 83.3 ms | ${FIRST_S}s | ${GAP_S}s | ${COUNT} | ${COUNT}회, depth ≈ 1.0 |
| \`motion-reject.mp4\` | 없음 | — | — | 0 | **검출 0** (coh 게이트가 걸러야 함) |

사건은 프레임 ${LEADIN}부터 ${PERIOD}프레임마다 시작한다. 앞 ${FIRST_S}초는 노출이 안정될
시간이자, 측정 전에 luma와 clip을 확인할 구간이다.

## 쓰는 법

1. 아무 모니터에나 **전체화면**으로 재생한다. 반복 재생으로 두면 표본이 늘어난다.
2. 카메라를 그 화면에 맞추고, 화면 안쪽에 ROI를 그린다.
3. 범례에서 luma 60~180, **clip 0.0%** 확인.
4. \`control-steady.mp4\`부터 돌린다 — 여기서 나온 최대 depth가 임계의 바닥이다.
5. 그다음 blank 클립들. 개수가 위 표와 맞는지 센다.
6. 마지막으로 \`motion-reject.mp4\`. 하나라도 검출되면 coh 게이트가 덜 된 것이다.

## 이 방법의 한계 — 미리 알아 둘 것

- **16.7 ms보다 짧은 사건은 만들 수 없다.** 모니터가 자기 주사율로 양자화한다.
  0.5 ms대를 검증하려면 마이크로컨트롤러로 구동하는 LED가 필요하다. 다만 실제 사건이
  전부 16.7 ms 이상으로 나오면 그건 콘텐츠 경로 고장이라는 뜻이고, LED는 필요 없어진다.
- **재생 모니터에도 자기 백라이트 PWM이 있다.** 밝기를 최대로 두면 듀티가 100%에 가까워
  리플이 줄어든다. 그래도 남으면 PWM 스윕과 같은 방법으로 노출을 맞추면 된다.
- **재생기가 프레임을 건너뛰거나 겹칠 수 있다.** 사건 개수가 표와 안 맞으면 검지기를
  의심하기 전에 재생기를 의심할 것. 모니터 주사율이 ${FPS} Hz인지도 확인할 것.
- 이 클립들은 **진짜 양성**을 만들어 주지만 진짜 AVN 고장은 아니다. 실제 고장의 depth와
  지속시간 분포는 현장에서 다시 재야 한다.
EOF

echo
echo "완료:"
ls -la "$OUT" | tail -n +2
