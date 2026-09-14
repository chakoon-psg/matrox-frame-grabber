# 데이터 흐름 — 보드에서 파일까지

한 프레임이 보드에 도착해서 무엇이 되는지, **어떤 클래스를 거쳐 어느 스레드에서** 처리되는지.

읽는 법: **스레드 경계가 이 문서의 요점이다.** 같은 일을 어느 스레드에서 하느냐가 이 시스템에서
유실 프레임을 만드는 거의 유일한 차이다.

---

## 한 장으로

```
                        ┌─────────────────────────────────────────────────┐
                        │  Rapixo CXP 보드 · 채널 4개 (카메라는 3대)        │
                        └────────────────────┬────────────────────────────┘
                                             │ DMA  (호스트 천장 3.68 GB/s)
                                             ▼
                              grab 링 버퍼 4장  ─ 비페이지드/DMA 메모리
                                             │
                                             │ MdigProcess 훅
   ══════════════════════════════════════════▼══════════════════════════════════════
   취득 스레드 — 프레임 주기 8,333 µs 가 예산이다. 넘으면 M_PROCESS_FRAME_MISSED.
   ══════════════════════════════════════════════════════════════════════════════════

        CameraChannel.ProcessFrame  (static, MIL 콜백)
                 │  MdigGetHookInfo → grabbedBuffer(MIL_ID), timeStampSec
                 ▼
        ┌────────────────────────────────────────────────────────────────┐
        │ ① 호스트로 한 번만 꺼낸다                                        │
        │    FrameExtractor.Extract(grabbedBuffer)                       │
        │        → SharedFramePool 에서 byte[] 한 장 (플래나 gbrp)         │
        │        → hold 계수로 여러 소비자가 같이 본다                      │
        └───────┬──────────────┬──────────────┬──────────────────────────┘
                │              │              │
        ┌───────▼──────┐ ┌─────▼────────┐ ┌──▼──────────────────────────┐
        │ IHostFrame   │ │ EvidenceRing │ │ QueueInspection             │
        │ Sink.Feed    │ │ .Add         │ │   AddHold → Task.Run        │
        │ Shared       │ │  (RAM 링)     │ │   (여기서 끝, 추론 안 함)     │
        └───────┬──────┘ └──────────────┘ └──┬──────────────────────────┘
                │                             │
                ▼                             ╎  ← 스레드 경계
        FfmpegVideoSink                       ╎
        (세션 · 사건 tier)                     ╎
                                              ╎
        ┌─────────────────────────────────────╎──────────────────────────┐
        │ ② 타일로 줄인다  (①과 별개, 원본 MIL 버퍼에서)                    │
        │    TileReducer.Reduce(grabbedBuffer, ChannelRoi, TileGrid)     │
        │        MbufChildColor 자식 3개 → MbufGet2d 로 행 블록씩         │
        │        → TileGrid : 타일 64칸의 합·제곱합·개수 (+밴드 합계)       │
        └───────┬────────────────────────────────────────────────────────┘
                ▼
        ┌────────────────────────────────────────────────────────────────┐
        │ ③ 일곱 판정자가 같은 TileGrid 를 읽는다                           │
        │                                                                │
        │   AnomalyDetector      Dropout    ← 사건형, 한 프레임으로 확정    │
        │   BlackoutDetector     Blackout   ┐                            │
        │   WashoutDetector      Washout    │                            │
        │   FreezeDetector       Freeze     ├ 지속형 — 여섯이               │
        │   ColorShiftDetector   ColorShift │  SustainedFaultTracker 공유  │
        │   FlickerDetector      Flicker    │  (dwell·히스테리시스·1회 보고) │
        │   FlipDetector         Flip       ┘                            │
        │                                                                │
        │        → AnomalyEvent?  →  RecordAnomaly → _anomalies 큐        │
        └───────┬────────────────────────────────────────────────────────┘
                ▼
        ┌────────────────────────────────────────────────────────────────┐
        │ ④ StillRing.Keep — 판정이 선 프레임을 MIL 버퍼에 붙잡아 둔다       │
        │    Onset / Extreme / Recovered / Reference                     │
        └────────────────────────────────────────────────────────────────┘

   ══════════════════════════════════════════════════════════════════════════════════
   워커 스레드 (Task.Run) — 취득 예산 밖. 여기서는 21 ms 를 써도 프레임을 잃지 않는다.
   ══════════════════════════════════════════════════════════════════════════════════

        CameraChannel.Inspect
                 │  공유 byte[] 에서 분석 ROI 를 잘라
                 ▼
        BgrLetterbox.Fill      플래나 gbrp → 인터리브 BGR, 축소·레터박스
                 │             (역변환은 ScaleX/ScaleY 로 — 공칭 배율 아님)
                 ▼
        IScreenInspector.Inspect
          └ OnnxScreenInspector   ORT 세션, 격자 64×80×5
                 │
                 ▼
        CenterNetDecoder.Suppress   logit 임계 → PeakCandidate → IoU NMS
                 │                  (동점은 (row,col) 사전순 — 좌표 지터 방지)
                 ▼
        ScreenFinding[]   Kind(우리 어휘) + Label(모델이 말한 것) + 사각형
                 │
                 ▼           frames.Release(frame)  ← hold 반납, 풀로 복귀
        mil-errors.log

        ─────────────────────────────────────────────────────────────────
        EvidenceGate.Shared  ← 증거 덤프는 채널을 가로질러 직렬화된다
        FfmpegClipExtractor  ← 세그먼트에서 -c copy 로 잘라낸다
        ─────────────────────────────────────────────────────────────────

   ══════════════════════════════════════════════════════════════════════════════════
   UI 틱 (DispatcherTimer 500 ms) — 사건이 산출물이 되는 곳
   ══════════════════════════════════════════════════════════════════════════════════

        CameraChannel.RefreshStats
                 │  _anomalies 큐를 비운다  (훅에서 직접 하지 않는 이유:
                 │  핸들러가 취득 스레드를 붙잡으면 그게 곧 유실이다)
                 ├──► ClipScheduler.Offer      → 구간·due 계산 → 클립 .mp4
                 ├──► EvidenceClips.Offer      → 무압축 .mkv (Ut Video)
                 ├──► ExportStills             → 무손실 .png 4장
                 ├──► WriteEventWindow         → 타일 기록 .csv
                 └──► AnomalyDetected 이벤트   → 타임라인 UI
```

---

## 왜 이 순서인가

**① 바이트 추출이 ② 타일 축약보다 먼저다.** 훅 안의 실제 순서가 그렇다. 싱크와 증거 링 버퍼가
프레임을 원하는지 먼저 묻고, 원하면 그때 한 번 꺼낸다.

**①과 ②는 같은 프레임을 두 번 읽는 것이 아니다.** ①은 호스트 RAM으로 전체 프레임을 꺼내고,
②는 MIL 버퍼에서 **타일 행 블록씩** 읽어 64칸 통계만 만든다 — 목적도 양도 다르다.
`TileReducer`는 행 블록 하나짜리 스트립 버퍼를 재사용하므로 전체 프레임을 들고 있지 않다.

**판독기가 ①에 합류하는 이유.** 판독기도 전체 프레임 바이트가 필요한데, 따로 읽으면
**두 읽기가 각각 4배 느려진다**(2026-09-10 실측 — 단순 중복이 아니라 취득 DMA와 같은 경로를
쓰는 메모리 경합이다). 그래서 `AddHold` 로 같은 배열에 올라탄다.

**추론이 워커인 이유.** 판독 1회가 5~21 ms인데 프레임 주기는 8.3 ms다. 훅에서 돌리면
240프레임에 한 번이라 grab 링이 흡수해 주지만 그건 운이다. 증거 덤프가 같은 이유로 워커다.

**사건이 훅이 아니라 틱에서 산출물이 되는 이유.** `ClipScheduler`·`StillRing.Export`·ffmpeg 호출은
전부 파일을 만진다. 취득 스레드가 디스크를 기다리면 그 시간이 곧 유실이다.

---

## 클래스 색인

### 취득 · MIL (`src/Mil/`) — MIL 타입을 아는 계층

| 클래스 | 하는 일 |
|---|---|
| `MilApplicationManager` | 앱·시스템 소유, 채널 생성, `cameras present: N of M` |
| `CameraChannel` | 디지타이저·디스플레이·grab 링 버퍼·훅. **모델이자 뷰모델** (약 4,400줄) |
| `GenICamFeatures` | 카메라 피처 읽기·쓰기. **쓰기는 read-back 전까지 검증 안 됨** |
| `TileReducer` | 원본 → `TileGrid`. `MbufChildColor` 자식을 링 버퍼에 한 번만 바인딩 |
| `BrightnessMeter` | 틱에서 도는 밝기 측정 (훅 아님) |
| `StillRing` | 판정 프레임을 MIL 버퍼에 보관 → 무손실 PNG |
| `FrameExtractor` | **호스트로 꺼내는 유일한 자리.** 플래나 gbrp `byte[]` |
| `FfmpegVideoSink` / `MilSeqVideoSink` | `IVideoSink` 구현. 후자는 미실행 골격 |

### 판정 (`src/Infrastructure/Detection/`) — MIL-free, 테스트되는 계층

| 클래스 | 하는 일 |
|---|---|
| `TileGrid` | 타일 64칸의 합·제곱합·개수 + 밴드 합계 |
| `AnomalyDetector` | Dropout. 실행 기준선 대비 하강, coherence·onset spread 게이트 |
| `SustainedFaultTracker` | 지속형 여섯이 공유하는 상태머신 |
| `BlackoutDetector` · `WashoutDetector` · `FreezeDetector` | 레벨·구조·변화량 |
| `ColorShiftDetector` · `FlickerDetector` · `FlipDetector` | 밴드 비중 · 진동 · 배치 |
| `AutoCalibrationPolicy` | 조용한 실행이 임계값을 **조이기만** 하도록 |
| `DetectionSettings` / `KindSettings` | 종류별 설정. 필드 추가 시 `CopyFrom`·`Clamp`·`Resolve` 셋 다 |

### 판독 (`src/Infrastructure/Detection/` + `src/Onnx/`) — 떼어낼 수 있는 조각

| 클래스 | 하는 일 |
|---|---|
| `IScreenInspector` | 계약. **ONNX 타입이 나오지 않는다** |
| `NullScreenInspector` | 언제나 0건. 제거 비용을 0으로 만드는 조각 |
| `ScreenInspectorFactory` | 시작 시 고르고 **이유를 로그에** (`VideoSinkFactory` 규약) |
| `ScreenPatrol` | N초마다 한 장. 프레임 번호·타임스탬프 산술 |
| `BgrLetterbox` | 플래나 → 인터리브 BGR, 축소·레터박스, **역변환** |
| `CenterNetDecoder` / `PeakCandidate` / `PeakByScore` | 임계·정렬·NMS |
| `ScreenFinding` | `Kind` + `Label` + 점수 + 사각형 |
| `OnnxScreenInspector` | **ORT 를 아는 유일한 파일** (`src/Onnx/`) |

### 산출물 (`src/Infrastructure/Video/`)

| 클래스 | 하는 일 |
|---|---|
| `SharedFramePool` | hold 계수. `Take`→1, 싱크가 큐 전에 `AddHold`, 각자 한 번 `Release` |
| `EvidenceRing` | 무압축 RAM 링 버퍼. 슬롯 예약 → **락 밖에서 복사** → 인덱스 발행 |
| `EvidenceGate` | 덤프를 채널 가로질러 직렬화 (하나가 660 MB) |
| `SegmentRing` / `ClipExtractor` | 2초 세그먼트 링 버퍼 → `-c copy` |
| `ClipScheduler` | 구간·due 계산, 사건 병합 |
| `StoragePolicy` / `StorageWarden` | 예비 용량, 오래된 문맥부터 삭제 |
| `RecordingRecord` | 사이드카 JSON. `first_frame`/`last_frame` 이 핵심 |

---

## 스레드별 예산

| 스레드 | 무엇이 도나 | 예산 | 실측 |
|---|---|---|---|
| **취득 훅** | 추출 1회 · 타일 축약 · 판정 7 · 정지화면 보관 | **8,333 µs** | reduce 210~273 µs, 읽기 438~619 µs |
| **워커** (`Task.Run`) | 판독 · 증거 덤프 · 클립 추출 | 없음 | 판독 4.9~5.7 ms (최대 21 ms) |
| **UI 틱** (500 ms) | 사건 산출물 · 밝기 · 상태 표시 | 부드러움 | UI 왕복 중앙값 0.10 ms |

**넘으면 조용히 사라진다.** 취득 예산 초과는 예외가 아니라 `M_PROCESS_FRAME_MISSED` 카운터
증가로만 나타난다. 그래서 이 저장소의 모든 성능 판단이 "유실 프레임 몇 개"로 표현된다.
