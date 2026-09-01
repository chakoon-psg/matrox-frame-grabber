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

# ---- 5. The depth staircase ----
# The threshold is on depth, not on duration. From the detector's side a 0.5 ms blackout and a
# frame that is 10% dimmer are the same event — both are "brightness fell by a tenth" — so the
# sensitivity floor can be probed by varying how dark the dip goes, on an ordinary 60 Hz monitor,
# with no LED and no high-refresh panel.
#
# The dip codes are not the depths. A monitor applies a gamma curve and the camera sensor is
# linear, so a code ratio of 0.9 arrives as roughly 0.9^2.2 = 0.79 of the light. The codes below
# are chosen to land either side of a 0.10 threshold once that curve is accounted for, but the
# figure that matters is the one the app measures — see the README.
DEPTH_CODES=(100 110 116 120 123 125 126 127)
DEPTH_PASSES=4

depth_clip() {
  local name="depth-staircase.mp4"
  local steps=${#DEPTH_CODES[@]}
  local events=$((steps * DEPTH_PASSES))
  local frames=$((LEADIN + events * PERIOD))
  local dur; dur=$(awk -v f="$frames" -v r="$FPS" 'BEGIN{printf "%.4f", f/r}')

  # One drawbox per step, each firing only on the event frames whose index falls on that step.
  local vf="" s code hex
  for s in $(seq 0 $((steps - 1))); do
    code=${DEPTH_CODES[$s]}
    hex=$(printf '0x%02x%02x%02x' "$code" "$code" "$code")
    [ -n "$vf" ] && vf="$vf,"
    vf="${vf}drawbox=x=0:y=0:w=iw:h=ih:color=${hex}@1:t=fill:enable='gte(n\\,${LEADIN})*eq(mod(n-${LEADIN}\\,${PERIOD})\\,0)*eq(mod(floor((n-${LEADIN})/${PERIOD})\\,${steps})\\,${s})'"
  done

  echo "  $name — ${steps}단계 x ${DEPTH_PASSES}회 = ${events}개 사건"
  # No -color_range override. Tagging this clip differently from the others made its grey decode to
  # a different level, which would have the operator re-setting the aperture between clips — and an
  # aperture change mid-session invalidates the comparison the clips exist to make.
  "$FFMPEG" -hide_banner -loglevel error -y \
    -f lavfi -i "color=c=${GREY}:s=${W}x${H}:r=${FPS}:d=${dur}" \
    -vf "$vf" "${ENC[@]}" "$OUT/$name"
}

# probe_y FILE FRAME — the luma one frame decodes to. scale=1:1 averages the (flat) frame.
probe_y() {
  "$FFMPEG" -hide_banner -loglevel error -i "$1" \
    -vf "select='eq(n\,$2)',scale=1:1" -fps_mode passthrough \
    -f rawvideo -pix_fmt gray - 2>/dev/null | od -An -tu1 | tr -d ' \n'
}

depth_clip

# ---- 6. The negative test ----
# A bright box crossing a grey field. Brightness inside individual tiles swings hard, but the tiles
# disagree about which way — which is exactly what the spatial coherence gate exists to reject.
# A detector that fires on this would fire on any animation an AVN plays.
echo "  motion-reject.mp4 — 밝은 물체 이동 (검출되면 안 됨)"
"$FFMPEG" -hide_banner -loglevel error -y \
  -f lavfi -i "color=c=${GREY}:s=${W}x${H}:r=${FPS}:d=${DUR}" \
  -vf "drawbox=x='mod(t*400\,${W}+400)-400':y=240:w=400:h=600:color=white@1:t=fill" \
  "${ENC[@]}" "$OUT/motion-reject.mp4"

# ---- 7. The coherence gate under load ----
# motion-reject.mp4 above is a weaker test than it looks. One white box on grey makes the picture
# brighter, and depth only counts falls -- so it never reaches the coherence gate, and a detector
# with the gate deleted outright would still pass it.
#
# Nor is sliding enough. Blocks crossing a grey field were measured at depth 0.0000 across twenty
# seconds: however fast they travel, half the tiles are still grey and the median of sixty-four
# tiles is that grey. The gate is never consulted.
#
# What moves a median is a change of composition -- most of the tiles going dark at once. So the
# load phase switches the field to bands, five eighths dark and three eighths bright, sized so the
# frame mean stays near grey and the running baseline does not follow it down. The median lands in
# the dark bands while five of them fall and three rise, which is the signed sum cancelling against
# the magnitudes: a deep fall the tiles do not agree on. Coherence has to reject every one.
#
# The last phase asks what a negative test cannot: does a real event still get through while
# content moves? The blocks return and the whole field is scaled by 0.70 for two frames every two
# seconds. Scaling moves every tile the same way, so a working gate passes it -- and a gate tuned
# until nothing ever fires would swallow these too.
#
#   0-4 s    still                 baseline settles
#   4-20 s   blocks sliding        expect 0 -- and depth near zero, which is the point
#   20-40 s  band bursts           expect 0 -- this is the load
#   40-60 s  blocks + dips         expect 10
BURST_FROM=1200                    # frame, 20 s
BURST_TO=2399                      # frame, 40 s
BURST_FRAMES=10                    # 167 ms of bands, every PERIOD
DIP_FROM=2400                      # frame, 40 s
DIP_FRAMES=2
DIP_ALPHA=0.30
BAND_DARK=0x282828
BAND_BRIGHT=0xf0f0f0

motion_load_clip() {
  local name="motion-load.mp4"
  local vf="" bandh=$((H / 8))
  local slide="between(n\,${LEADIN}\,$((BURST_FROM - 1)))+gte(n\,${DIP_FROM})"
  local burst="between(n\,${BURST_FROM}\,${BURST_TO})*lt(mod(n-${BURST_FROM}\,${PERIOD})\,${BURST_FRAMES})"

  add_box() {                      # colour w h y speed dir
    local colour="$1" bw="$2" bh="$3" by="$4" sp="$5" dir="$6" x
    if [ "$dir" = "r" ]; then
      x="mod(t*${sp}\,${W}+${bw})-${bw}"
    else
      x="${W}-mod(t*${sp}\,${W}+${bw})"
    fi
    [ -n "$vf" ] && vf="$vf,"
    vf="${vf}drawbox=x='${x}':y=${by}:w=${bw}:h=${bh}:color=${colour}:t=fill:enable='${slide}'"
  }

  add_box "0x303030@1" 620 420  60 380 r
  add_box "0xe0e0e0@1" 420 300 140 310 l
  add_box "0x303030@1" 500 360 400 260 r
  add_box "0xe0e0e0@1" 360 260 520 440 l
  add_box "0x303030@1" 700 300 700 500 r
  add_box "0xe0e0e0@1" 300 220 760 210 l

  # The bands: the whole field dark, then three eighths painted back bright. Five dark against
  # three bright puts the median in the dark and keeps the mean near grey.
  vf="${vf},drawbox=x=0:y=0:w=iw:h=ih:color=${BAND_DARK}@1:t=fill:enable='${burst}'"
  local b
  for b in 1 4 6; do
    vf="${vf},drawbox=x=0:y=$((b * bandh)):w=iw:h=${bandh}:color=${BAND_BRIGHT}@1:t=fill:enable='${burst}'"
  done

  # The dips, last so they scale whatever has been drawn under them.
  vf="${vf},drawbox=x=0:y=0:w=iw:h=ih:color=black@${DIP_ALPHA}:t=fill:enable='gte(n\,${DIP_FROM})*lt(mod(n-${DIP_FROM}\,${PERIOD})\,${DIP_FRAMES})'"

  echo "  $name — 움직임 부하 + 움직이는 중의 실제 사건"
  "$FFMPEG" -hide_banner -loglevel error -y \
    -f lavfi -i "color=c=${GREY}:s=${W}x${H}:r=${FPS}:d=${DUR}" \
    -vf "$vf" "${ENC[@]}" "$OUT/$name"
}

motion_load_clip

# ---- What the motion clip actually came out as -------------------------------------------
# The blackout clips need no measuring: the events are where the script put them. This one does.
# Whether the load phase reaches the coherence gate depends on how the bands land on an 8x8 grid,
# and whether the dips survive the motion depends on the same. Guessing produced a first version
# whose load phase measured depth 0.0000 for twenty seconds -- it never reached the gate at all.
MOTION_KEY=$(python "$(dirname "${BASH_SOURCE[0]}")/predict-clip.py" \
               "$OUT/motion-load.mp4" --phases 4,20,40 2>/dev/null || echo "(predict-clip.py 실행 실패)")
MOTION_EVENTS=$(printf '%s' "$MOTION_KEY" | awk '/^predicted events:/ {print $3}')
MOTION_LOAD=$(printf '%s' "$MOTION_KEY" | awk '/coherence rejected:/ {print $NF}')
MOTION_MAXDEPTH=$(printf '%s' "$MOTION_KEY" | awk '$1 ~ /^20\.0-40\.0/ {print $5}')

# ---- Ground truth, written next to the clips ----
FIRST_S=$(awk -v l="$LEADIN" -v f="$FPS" 'BEGIN{printf "%.2f", l/f}')
GAP_S=$(awk -v p="$PERIOD" -v f="$FPS" 'BEGIN{printf "%.2f", p/f}')
COUNT=$(awk -v d="$DUR" -v f="$FPS" -v l="$LEADIN" -v p="$PERIOD" \
  'BEGIN{print int((d*f - l - 1)/p) + 1}')

# ---- Measure what the staircase actually encoded, rather than repeating the nominal codes ----
# yuv420p and the encoder both move the values a little, and the shallow steps are only a level or
# two apart — close enough to the quantiser that assuming the nominal code would misreport which
# step is which.
DEPTH_STEPS=${#DEPTH_CODES[@]}
DEPTH_EVENTS=$((DEPTH_STEPS * DEPTH_PASSES))
DEPTH_BASE=$(probe_y "$OUT/depth-staircase.mp4" 100)
DEPTH_TABLE=""
for s in $(seq 0 $((DEPTH_STEPS - 1))); do
  y=$(probe_y "$OUT/depth-staircase.mp4" $((LEADIN + s * PERIOD)))
  DEPTH_TABLE="${DEPTH_TABLE}$(awk -v n=$((s+1)) -v c="${DEPTH_CODES[$s]}" -v y="$y" -v b="$DEPTH_BASE" \
    'BEGIN{ printf "| %d | %d | %d | %.3f | %.3f |", n, c, y, 1-y/b, 1-(y/b)^2.2 }')
"
done

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
| \`motion-reject.mp4\` | 없음 | — | — | 0 | **검출 0** — 다만 아래 참고 |
| \`motion-load.mp4\` | 33.3 ms | 40.00s | ${GAP_S}s | ${MOTION_EVENTS} | 구간별, 아래 표 참조 |
| \`depth-staircase.mp4\` | 16.7 ms | ${FIRST_S}s | ${GAP_S}s | ${DEPTH_EVENTS} | 아래 표 참조 |

사건은 프레임 ${LEADIN}부터 ${PERIOD}프레임마다 시작한다. 앞 ${FIRST_S}초는 노출이 안정될
시간이자, 측정 전에 luma와 clip을 확인할 구간이다.

## depth 계단

\`depth-staircase.mp4\`는 소등 대신 **한 프레임만 조금 어둡게** 한다. 검지기의 임계는 지속시간이
아니라 depth에 걸려 있고, 검지기가 보기에 "0.5 ms 완전 소등"과 "한 프레임이 10% 어두움"은
같은 사건이다 — 둘 다 "밝기가 10분의 1 떨어졌다"이다. 그래서 평범한 60 Hz 모니터로,
LED도 고주사율 패널도 없이 감도 바닥을 훑을 수 있다.

단계는 ${DEPTH_PASSES}회 반복된다. 사건 k번째(1부터)의 단계는 \`((k-1) mod ${DEPTH_STEPS}) + 1\`.

| 단계 | 코드 | 디코드 Y | 코드 depth | 빛 depth (추정) |
|---|---|---|---|---|
${DEPTH_TABLE}

- **디코드 Y**는 이 스크립트가 생성 직후 실제로 측정한 값이다(기준 회색 = ${DEPTH_BASE}).
- **빛 depth**는 모니터 감마를 2.2로 가정한 *추정치*다. 실제 감마는 모니터마다 다르다.
- **믿을 값은 앱이 재는 luma다.** 기준 구간의 luma와 딥의 luma 비가 검지기가 실제로 보는
  depth이고, 임계는 거기에 맞춰 정한다. 위 표는 어느 단계가 더 깊은지의 순서일 뿐이다.

## coherence 게이트 (\`motion-load.mp4\`)

coherence는 **내용 변화를 걸러내라고** 있는 게이트다. 그런데 그것이 실제로 걸러내는지는
지금까지 확인된 적이 없다 — 정지한 패널 앞에서 15분을 재도 게이트가 막아낸 프레임이 0~1개였다.
부하가 걸리지 않으면 임계값 0.80이 맞는지 알 수 없다.

\`motion-reject.mp4\`로는 확인되지 않는다. 흰 상자가 회색 위를 지나가면 화면이 **밝아지고**,
depth는 하락만 세므로 게이트까지 도달하지 않는다. **게이트를 아예 삭제해도 이 영상은 통과한다.**

미끄러지는 것만으로도 부족하다. 상자 6개를 서로 다른 방향·속도로 지나가게 해도
20초간 depth가 **0.0000**이었다 — 아무리 빨라도 타일의 절반은 회색으로 남고,
64개 타일의 중앙값은 그 회색이다.

중앙값을 움직이는 것은 **구성의 변화**다. 그래서 부하 구간은 화면을 띠로 바꾼다 —
8분할 중 5개를 어둡게, 3개를 밝게. 프레임 평균은 회색 근처로 유지되어 baseline이 따라오지 않고,
중앙값은 어두운 띠에 놓인다. 5개가 내려가는 동안 3개가 올라가므로 **깊은 하락인데 타일이
동의하지 않는다.** coherence가 이걸 전부 막아야 한다.

| 구간 | 내용 | 기대 |
|---|---|---|
| 0–4초 | 정지 | baseline 안정 |
| 4–20초 | 상자 6개 이동 | **검출 0**, depth ≈ 0 — 게이트에 도달하지 않는 것이 결과 |
| 20–40초 | 띠 전환 10회 (167 ms씩) | **검출 0**, depth 최대 ${MOTION_MAXDEPTH} — 이것이 부하 |
| 40–60초 | 상자 이동 + 전체 0.70배 딥 | **검출 ${MOTION_EVENTS}회**, depth ≈ 0.27 |

- **coherence가 막아낸 프레임: ${MOTION_LOAD}개.** 이 수가 0이면 그 실행은 게이트를 시험하지 않았다.
- 마지막 구간이 반대쪽 절반이다. 움직임에 안 걸리도록 게이트를 조여 놓으면 **실제 사건도 같이
  삼킨다.** 20–40초가 0이고 40–60초가 ${MOTION_EVENTS}여야 둘 다 맞는 것이다.
- 위 수치는 \`scripts/predict-clip.py\`가 이 영상을 직접 축약해 계산한 **예측**이다.
  코드 공간의 값이고, 모니터 감마와 카메라 응답이 사이에 있으므로 앱이 재는 depth와는 다르다.
  **개수를 정답으로 보고 depth는 근사로 볼 것.**

### 쓰는 법

검출된 사건이 몇 번째 단계까지 내려가는지 센다. 예를 들어 1~6단계가 검출되고 7·8단계가
안 잡히면, 감도 바닥은 6단계와 7단계 사이에 있다. 그 값을 \`control-steady.mp4\`에서 나온
오검출 바닥과 비교한다 — 둘 사이가 임계를 놓을 수 있는 구간이고, 그 구간이 좁거나 뒤집혀
있으면 지금 광학·노출로는 그 깊이를 분간할 수 없다는 뜻이다.

## 쓰는 법

1. 아무 모니터에나 **전체화면**으로 재생한다. 반복 재생으로 두면 표본이 늘어난다.
2. 카메라를 그 화면에 맞추고, 화면 안쪽에 ROI를 그린다.
3. 범례에서 luma 60~180, **clip 0.0%** 확인.
4. \`control-steady.mp4\`부터 돌린다 — 여기서 나온 최대 depth가 임계의 바닥이다.
5. 그다음 blank 클립들. 개수가 위 표와 맞는지 센다.
6. \`motion-load.mp4\`. **coherence 게이트를 판정하는 유일한 클립이다** — 20~40초 구간에서
   하나라도 검출되면 게이트가 통과시키고 있고, 40~60초에서 ${MOTION_EVENTS}회가 안 나오면
   게이트가 실제 사건까지 막고 있다. 두 조건을 같이 봐야 한다.
7. \`motion-reject.mp4\`는 남겨 두었지만 약한 시험이다. 화면이 밝아지므로 depth 게이트에서
   이미 걸러지고, coherence까지 가지 않는다.

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
