# 학습 검출기는 떼어낼 수 있는 슬라이스로 넣는다

`C:\projects\cs`의 `GreenDetectorCpu`(MobileNetV2-FPN-CenterNet, ONNX, CPU 전용)를 이 앱에
가져오는 방법. 결론부터: **다섯 파일 중 하나의 절반만 가져오고, 나머지는 우리 쪽에 이미 더 나은
것이 있어 버린다.** 그리고 가져오는 부분은 `IVideoSink`와 `tools/MilVideoSink/`가 이미 세워 둔
패턴대로 **계약 뒤에** 둔다 — 모델 파일이 없으면 아무것도 로드되지 않고, 참조 한 줄을 지우면
빌드에서 사라진다.

측정 환경은 같은 머신이다(Ryzen 7 9700X 8C/16T). 그래서 cs 문서의 추론 시간이 그대로 옮겨온다.

> **상태: 설계만, 검토 완료(2026-09-11).** 이 문서에 코드는 없고 저장소에도 없다. 계약 초안을
> 한 번 써 보고 되돌렸으며, 그때 나온 것들은 아래 요구사항으로 남겼다. 네 가지를 검토해
> 확정했다 — **이름**(`Screen*` + `Inspector`/`Finding`), **붙는 자리**(사건 판독 + 순찰 판독
> 둘 다), **어휘**(`Kind`로 행동하고 `Label`로 기록, 맞는 종류가 없으면 `Unknown`),
> **제거 비용**(의존성 세 줄, 남는 것은 회색 체크박스 한 줄). 구현은 아직 시작하지 않았다.

## 무엇을 가져오고 무엇을 버리는가

| cs 파일 | 판정 | 근거 |
|---|---|---|
| `GreenDetector.cs` 세션 생성 · ONNX 메타데이터 런타임 검증 | **가져온다** | 파일명이 `192`인데 입력이 256×320인 것을 이 검증이 막아 냈다. 우리 규율과 같다 |
| `GreenDetector.cs` Decode · NMS · Sigmoid · IoU | **가져온다** | logit 공간 임계값, 안정 sigmoid, 사전 할당 버퍼 — 잘 쓴 코드다 |
| `GreenDetector.cs` `Preprocess` | **다시 쓴다** | OpenCV 93.7 MB가 여기에만 쓰인다. 아래 참조 |
| `LatestCamera.cs` | **버린다** | `CameraChannel` + `FrameExtractor` + `SharedFramePool`이 대체한다. 그쪽은 참조 계수가 있고 실측됐다 |
| `DetectionExcelWriter.cs` | **버린다** | `RecordingRecord` 사이드카가 대체한다. 아래 참조 |
| `Program.cs` | **버린다** | 설정·수명·종료는 우리 쪽 구조가 있다 |
| `.onnx` 가중치 | **저장소에 넣지 않는다** | 12 MB 바이너리. 런타임 탐색으로 다룬다 — ffmpeg와 같은 취급 |

## OpenCV는 들어오지 않는다

`Preprocess`가 OpenCV를 쓰는 곳은 정확히 셋이다 — `Cv2.Resize`, letterbox 붙여넣기
(`SetTo` + ROI `CopyTo`), `Marshal.Copy`로 `Mat.Data`에서 `byte[]` 뽑기. 결과물은
`InputW × InputH × 3` 인터리브 BGR `byte[]` 하나다.

그 하나를 위한 비용이 **93.7 MB**다(`OpenCvSharpExtern.dll` 65.4 + `opencv_videoio_ffmpeg4130_64.dll`
27.3 + `OpenCvSharp.dll` 1.0). 그중 27.3 MB는 **ffmpeg다** — `ffmpeg.exe`를 런타임에 탐색하는
앱에 두 번째 ffmpeg를 패키지로 끌고 들어오는 셈이다.

그리고 cs 문서 §8이 인터리브 반복 측정으로 확인했다: **전처리 + 디코드 전체가 추론의 2% 미만
(상한 0.13 ms)이고 실행 간 편차 이하라 측정이 안 된다.** 즉 여기를 우리 코드로 바꿔도 잃을
성능이 없다.

우리 프레임은 **플래나 gbrp** `byte[]`(1024×772, 밴드 셋)이고 모델은 **인터리브 BGR**을 받는다
(모델 그래프가 BGR→RGB를 내부에서 한다 — 여기를 다음 사람이 반드시 잘못 고친다). 그래서
**역플래나화와 축소를 한 번의 패스로 합친다.** 1024×772 → 256×320에서
`scale = min(320/1024, 256/772) = 0.3125`, `newW = 320`, `newH = 241`, `padX = 0`, `padY = 7`이다.

이 변환은 `Infrastructure/`에 들어가고 테스트된다. 우리 저장소에서 산술이 그렇게 살아 있는
자리가 `FfmpegArgs`와 `VideoRatePolicy`다 — 녹화 결함 두 건이 거기 살았다.

## 좌표는 엑셀로 쓰지 않는다

cs는 세션 끝에 xlsx 일괄 저장이고, 문서 §7.1이 실측했다: 행당 working set 약 2.3 KB이므로
8시간 세션에 **2 GB 이상**, 그리고 워크시트 상한 1,048,576행에 **약 9.7시간**에 닿아
`SaveAs`가 예외를 던진다 — **세션이 끝난 뒤에.** 하루치가 저장 직전에 사라진다.

우리에겐 이미 `RecordingRecord`가 있다. 산출물마다 옆에 손으로 쓴 JSON을 남기고, 불변 문화권을
쓰고, 이미 `test_id` / `dut_id` 자리를 비워 두었다. **판정 결과는 증거 사이드카의 필드로
들어간다.** 의존성 8.8 MB와 실패 모드 하나가 같이 사라진다.

## 어디에 붙는가 — 호출 지점 둘, 계약 하나

예산이 하나를 배제한다.

| | 값 |
|---|---|
| fp32 추론 (256×320, IntraOp=8, 유휴 머신) | **2.77 ms** |
| int8 추론 | 5.94 ms — **fp32가 2.2배 빠르다.** 파일이 3.2배 작은 것 말고 얻는 게 없다 |
| 우리 프레임 주기 (120 fps) | **8.33 ms** |
| **3채널 매 프레임** | 3 × 2.77 = **8.31 ms = 예산의 100%** ❌ **불가** |

그 아래로는 두 자리가 있고, **서로 다른 질문에 답하므로 둘 다 만든다.** 계약은 하나이고
달라지는 것은 호출 지점뿐이다.

### A. 사건 판독 — `StillRing`의 네 장 (기본)

이상이 확정되면 `StillRing`이 이미 네 프레임(onset / extreme / recovered / reference)을 MIL
버퍼에 잡아 틱에서 PNG로 쓴다. 그 네 장을 그대로 판독한다.

- **4 × 2.77 = 11 ms, 사건당 한 번.** 틱은 이미 PNG를 6~8 ms씩 쓰고 있고 그 안에 든다.
- **평시 비용 0.** 사건이 없으면 한 번도 돌지 않는다.
- 네 장을 **다 본다.** reference와 recovered는 정상 프레임이라, "extreme에서는 Flip인데
  reference에서는 아니다"가 판독 하나보다 훨씬 강한 근거다. 사이드카에 넷 다 남긴다.
- **한계가 하나 있고 이게 B가 필요한 이유다: 타일 검출기가 확정하지 못한 이상은 판독기가 영원히
  못 본다.** 타일 통계는 밝기 기반이라 밝기가 변하지 않는 이상 — 뒤집혀도 평균 밝기는 같은
  **Flip**, 밴드별로 봐야 하는 **ColorShift**, 같은 그림이 계속 오는 **Freeze** — 을 놓칠 수
  있다. A만 두면 학습 판독기를 타일 검출기의 부하로 만들고 그 한계를 물려받는다.

### B. 순찰 판독 — 낮은 주기로 상시 (`ScreenPatrol`)

A의 한계를 깨는 자리다. 채널마다 `InspectSeconds`마다 한 장을 판독한다.

- **새 데이터 경로가 필요 없다.** 이미 있는 **`IHostFrameSink`** 가 그대로 맞는다 —
  `WantsFrame(long)`이 "네 프레임마다 받는 싱크"를 위해 존재하고, 순찰은 240프레임마다 받는
  싱크다. 훅은 아무 싱크도 원하지 않는 프레임을 읽어내지 않으므로 **원하지 않는 프레임의
  비용은 0이다.**
- 2초 주기 · 3채널이면 **3 × 0.5 × 2.77 = 4.2 ms/초**, 코어 하나의 0.4%다.
- **채널마다 위상을 어긋나게 둔다.** 안 그러면 셋이 같은 틱에 몰려 8.3 ms가 한 번에 든다.
- 실시간이다 — 사건이 아직 안 났어도 지금 화면을 본다.

### 공유: 판독기 하나, ORT 세션 하나

A와 B, 그리고 세 채널이 **같은 인스턴스 하나**를 쓰고 호출은 락으로 직렬화한다. 이유 둘:

- **증거 덤프에서 배운 것을 미리 적용한다.** 세 채널이 동시에 무거운 일을 하면 양보하는 것은
  취득이다(실측: 레이트가 120 → 57~89 fps로 붕괴, `EvidenceGate` 참고). 추론은 ORT가 여러
  스레드를 쓰므로 같은 계열이다.
- 세션 하나면 **가중치도 한 번만 올라가고** 워밍업도 한 번이다. 채널마다 세션을 열 이유가 없다.

`IScreenInspector`가 "스레드 안전하지 않다"고 못 박은 것이 이 설계와 짝이다.

### 판독 결과가 가는 곳

- **A의 결과는 증거 사이드카의 필드로.** 같은 사건이니 같은 기록이다.
- **B의 결과는 두 번째 출처로 타임라인에 올린다** — 타일 검출기와 나란히, **출처를 달고.**
  곧바로 `AnomalyEvent`로 만들지 않는다. 그러면 debounce·coherence 게이트를 우회해 리포트가
  범람한다. 판독기에는 **자기 debounce**가 따로 있어야 하고, 리포트는 "타일이 봤다 / 판독기가
  봤다 / 둘 다 봤다"를 구분해서 말할 수 있어야 한다.
- **미결:** 둘이 엇갈릴 때(판독기만 Flip이라고 함) 무엇을 `ChannelHealth`에 쓸 것인가. 모델이
  생기고 오검출률을 재기 전에는 정할 수 없다.

### 설정

`InspectSeconds`(0 = 순찰 끔)와 `InspectOnAnomaly`(bool). **둘 다 기본 off** — 모델이 없으면
어차피 `NullScreenInspector`이고, 생긴 뒤에도 켜는 것은 실측 후의 판단이다.

## 이름 — cs에서 우리 쪽으로

가져오는 것마다 이름을 다시 잡았다. 하나는 **충돌**이고 나머지는 **우리 어휘로 옮긴 것**이다.

| cs | 우리 | 왜 |
|---|---|---|
| `Detection` (구조체: 박스+점수) | **`ScreenFinding`** | 🔴 **정면 충돌.** 이 글로서리에서 `Detection`은 "이상이 확정되는 **사건**" — 타임라인 위의 한 시점이다. cs의 것은 그림 위의 사각형이다. 그대로 뒀으면 한 단어에 두 뜻이 앉는다 |
| `GreenDetector` (클래스) | **`IScreenInspector`** + `OnnxScreenInspector` | 이름에 검출 대상("green")이 박혀 있어 모델을 바꾸면 거짓말이 된다. 그리고 `Detector`는 `AnomalyDetector`가 이미 쓰고 있어 "타일 검출기 / 학습 검출기"가 코드에서 구분되지 않는다 |
| — | **`NullScreenInspector`** | cs에는 없던 것. 제거 비용을 0으로 만드는 조각이다 |
| `GreenDetector.Preprocess` | **`BgrLetterbox`** | 클래스로 꺼냈다. OpenCV가 쓰이던 유일한 자리이자, 좌표를 되돌리는 매핑을 들고 있어야 할 자리다 |
| `GreenDetector.Candidate` | **`PeakCandidate`** | `Candidate`만으로는 무엇의 후보인지 말하지 않는다. 히트맵 피크의 후보다 |
| `GreenDetector.CandidateComparer` | **`PeakByScore`** | 비교자 이름이 정렬 기준을 말하게 |
| `GreenDetector.Decode` + `ApplyNms` | **`CenterNetDecoder`** | 별도 클래스로 꺼내 `Infrastructure/`에 둔다. ONNX 타입이 없는 순수 산술이라 **테스트된다** — cs에서는 350줄이 세션 옆에 붙어 테스트 불가였다 |
| `GreenDetector.Benchmark` | **하니스 명령으로** | 매 실행 13초를 쓰던 것이 앱의 일이 아니다 |
| `LatestCamera` | **삭제** | `CameraChannel` + `FrameExtractor` + `SharedFramePool` |
| `DetectionRow` | **삭제** | `RecordingRecord`의 필드로 |
| `DetectionExcelWriter` | **삭제** | 사이드카 JSON |
| `GreenCpuDetector` (네임스페이스) | **`MatroxFrameGrabber.Infrastructure`** / `.Onnx` | 계약은 기존 네임스페이스에, 구현만 분리 |

세 낱말을 `CONTEXT.md`에 등록했다 — **Screen inspection**(판독), **Screen finding**(판독 결과),
**Screen inspector**(판독기). `_피할 말_`까지 같이 적었고, 그중 첫 줄이 "Detection이라 부르지
않는다"이다.

### 검토에서 떨어진 이름들 (2026-09-11, 확정)

다시 꺼내지 않도록 사유를 남긴다. 앞의 넷은 **충돌**이라 취향의 문제가 아니었다.

| 후보 | 왜 안 되는가 |
|---|---|
| `ScreenReading` / `IScreenReader` | 글로서리에 **`Brightness reading`**이 있어 `Reading`이 선점됐다. 그리고 "screen reader"는 세상에서 접근성 소프트웨어를 뜻한다 |
| `ScreenRegion` | 글로서리의 **`Detection region`**(분석 ROI)과 충돌. 코드에도 `BrightnessSampleRegion`이 있다 |
| `IScreenModel` | `Model`은 MVVM과 충돌한다 — `CameraChannel`이 "모델이자 뷰모델"이다 |
| `*Detector` | `AnomalyDetector`가 선점. 설계 전체가 "타일 검출기 대 학습 판독기"의 구분 위에 서 있는데 코드에서 둘이 갈라지지 않는다 |
| `AnomalyFinding` / `IAnomalyInspector` | 충돌은 아니지만 **`AnomalyEvent`(시점) 옆에 `AnomalyFinding`(사각형)** 이 앉는다. `Detection`에서 피한 문제를 한 칸 옆으로 옮기는 것뿐이고, 접두어 `Anomaly*`는 이미 다섯 타입이 쓴다 |
| `AvnScreenFinding` | 글로서리와 정확히 일치하지만 호출부가 길어진다. `Screen` 식별자 사용처가 **0건**이라 모호할 자리가 없었다 |
| `ScreenHit` | 짧지만 한국어 대응어가 마땅치 않다. 이 프로젝트는 IATF 16949 도메인이고 거기서 심사가 찾아낸 한 건을 부르는 말이 **finding**이다 |

`Inspector`를 고른 것은 이 저장소의 관례이기도 하다 — `Detector` / `Meter` / `Reducer` /
`Extractor` / `Warden` / `Scheduler` / `Recorder` / `Gate` / `Pool`처럼 **행위자 명사**로 역할을
붙인다.

## 계약

`Infrastructure/Detection/`에 둔다. ONNX도 OpenCV도 MIL도 이름이 나오지 않으므로 테스트된다.

**아직 한 줄도 쓰지 않았다.** 아래는 제안이고, 검토를 통과하기 전에는 문서로만 있다.

| 무엇 | 어디 | 역할 |
|---|---|---|
| `ScreenFinding` | `Infrastructure/Detection/` | `AnomalyKind` + 점수 + 사각형 |
| `IScreenInspector` | `Infrastructure/Detection/` | 계약 |
| `NullScreenInspector` | `Infrastructure/Detection/` | 언제나 0건. 모델이 없을 때의 기본값 |
| `BgrLetterbox` | `Infrastructure/Detection/` | 플래나 gbrp → 인터리브 BGR + 축소 + 레터박스 + 역매핑 |
| `CenterNetDecoder` | `Infrastructure/Detection/` | 히트맵 → 후보 → NMS. ONNX 타입 없음 |
| `ScreenInspectorFactory` | `Infrastructure/Detection/` | 시작할 때 고르고 이유를 로그에 (`VideoSinkFactory`와 같은 규약) |
| `ScreenPatrol` | `Infrastructure/Detection/` | B의 주기 판정. `IHostFrameSink`로 붙어 N초마다 한 장만 받는다 |
| `OnnxScreenInspector` | `src/Onnx/` | ORT 세션과 버퍼만 |

`ScreenPatrol`도 `CONTEXT.md`에 올릴 이름이다 — `Sweep`은 `PwmSweep`이 쓰고 있어 못 쓴다.

```csharp
public readonly struct ScreenFinding
{
    public AnomalyKind Kind  { get; }    // 앱이 행동하는 근거 — 우리 어휘
    public string      Label { get; }    // 모델이 말한 그대로 — 기록용, 행동하지 않는다
    public float       Score { get; }    // 0..1
    public float X, Y, Width, Height;    // 취득 프레임 좌표 (분석 ROI와 같은 계)
    public float Right  => X + Width;    // x2가 아니라 가장자리 이름으로
    public float Bottom => Y + Height;
    public float Area   => Width <= 0f || Height <= 0f ? 0f : Width * Height;
}

public interface IScreenInspector : IDisposable
{
    string Describe { get; }             // 어떤 가중치로 판독했는지 — 로그와 툴팁
    int InputWidth { get; }
    int InputHeight { get; }
    AnomalyKind[] Reports { get; }       // 이 판독기가 말할 수 있는 종류
    int Inspect(byte[] bgr, Span<ScreenFinding> into);   // 인터리브 BGR, 패딩 없음
}
```

**스레드 안전하지 않다.** 구현은 버퍼를 사전 할당해 재사용한다 — 그게 hot path에서 할당을
없애는 방법이고, 그래서 호출자당 하나이거나 락 하나다. cs 문서 §9-18이 같은 것을 문서화하라고
적었다.

### 앱이 행동하는 것은 `Kind`, 기록에 남는 것은 `Label`도 (2026-09-11 확정)

**`Kind`가 앱의 어휘다.** `AnomalyKind`는 11개 파일이 소비한다 — 타임라인, 설정 창,
`ChannelHealth`, 클립 정책, 사이드카. 여기를 모델 마음대로의 문자열로 열면 그 전부가 모르는
값을 받는다. 무엇을 끄고, 무슨 색으로 그리고, 고장인지 아닌지를 답할 수 없게 된다.

**그런데 `Kind`만 두면 사이드카가 거짓말을 한다.** 모델이 "vertical_line_defect"라고 했는데
제일 가까운 것이 `ColorShift`라서 그렇게 적으면, 기록은 우리가 한 번역을 모델의 말인 것처럼
남긴다. IATF 추적성 맥락에서 가벼운 문제가 아니다.

그래서 **둘 다 든다.** 두 진실이 아니라 **결정과 그 근거**다 — `RecordingRecord`가
`SourceDeclaredFps` / `FileDeclaredFps` / `DeliveredFps`를 다 들고 있고 감사 한 줄이 그
**비교**인 것과 같은 구조다.

```json
"kind":  "ColorShift",
"label": "vertical_line_defect",
"score": 0.82
```

**`Label`은 신뢰하지 않는 입력이다.** 모델 파일에서 오고 JSON 사이드카에 들어간다. 그러므로
길이를 자르고(64자), 화이트리스트 문자만 남기고(`[A-Za-z0-9_.-]`), **파일 경로에는 절대 쓰지
않는다.** 파일명은 지금처럼 `Kind`로 짓는다.

### 종류 인덱스는 재사용하지 않는다

이게 검토에서 나온 두 번째 지점이고, 안 짚었으면 설정 창이 망가졌을 자리다.

**`AnomalyKind`는 라벨이 아니라 인덱스다** — `PerKind[AnomalyCatalog.Index(kind)]`가
`KindSettings` 한 줄을 가리키고, 그 줄에 `Deviation` · `Coherence` · `DebounceMs` ·
`MaxEventMs` · `MaxOnsetSpreadMs` · `MinOnsetTiles` · `CalibrationFloor` 등 **열 개**가 딸려
온다. 전부 타일 통계의 노브이고 **판독기에 의미 있는 것은 하나도 없다.** 판독기가 갖는 것은
점수 컷 하나다.

그래서 **판독 결과는 `DetectionSettings.PerKind`를 거치지 않는다.**

- `Kind`는 **라우팅·필터·보고용 라벨로만** 쓴다.
- 점수 임계값은 **판독기 설정에 따로** 둔다. `KindSettings`에 칸을 만들지 않는다.
- `AnomalyKindSet`(감시 종류)만 재사용한다 — 그건 켜고 끄는 목록일 뿐 임계값을 끌고 오지
  않는다. 감시하지 않는 종류는 판독기도 보고하지 않는다.
- 카메라별 설정 창의 임계값 행은 **타일 검출기가 잴 수 있는 종류만** 그린다.

### 어느 종류에도 안 맞을 때 — `Unknown`

모델이 본 것을 우리 일곱 종류 중 어디에도 넣을 수 없으면 **`AnomalyKind.Unknown = 7`** 로
보고한다. 신뢰성 시험에서 "이름 붙일 수 없는 이상"은 버릴 것이 아니라 사람이 봐야 할 것이다.
`Label`이 함께 남으므로 무엇이었는지도 남는다.

세 가지가 딸려 온다.

- **`AnomalyCatalog.Count`가 7 → 8이 된다.** 안전하다 — `Normalize()`가 배열을 버리지 않고
  **늘린다.** 이건 5→7에서 캘리브레이션을 날릴 뻔한 뒤에 그렇게 고친 것이고, 그 주석이
  "kind를 추가하는 바로 그때 일어났을 일이고, 이미 두 번 일어났다"고 적고 있다. 세 번째다.
- **`AnomalyCatalog.Implemented`가 더 이상 참/거짓이 아니다.** 지금은
  `kind == Dropout`이고, 설정 창이 나머지를 "미구현 — …"으로 그린다. `Unknown`은 미구현이
  아니라 **판독기가 있으면 구현된 것**이다. 그러므로 "누가 구현하는가"로 바꿔야 한다 —
  타일 / 판독기 / 없음. Blackout·Washout·Flip도 나중에 판독기가 맡으면 같은 길을 간다.
- **`Unknown`은 판독기 전용이다.** 타일 검출기는 이것을 만들 수 없다. 카탈로그가 그렇게
  말해야 하고, 카메라별 임계값 행도 그리지 않는다.

구현은 **별도 어셈블리** `src/Onnx/`에 두고 `ProjectReference`로만 연결한다. 본체는 ONNX 타입을
한 번도 이름으로 부르지 않는다 — 부르는 순간 제거 비용이 올라간다.

### `BgrLetterbox`에 미리 박아 둘 요구사항

초안을 한 번 써 보면서 나온 것들이다. 코드는 되돌렸고, 다시 쓸 때 이대로 쓴다.

**① 역변환은 공칭 배율이 아니라 축별 비율로 한다.** 이게 제일 중요하다.

```
공칭 배율   Scale  = min(320/1024, 256/772) = 0.3125
축소 크기   241 = round(772 × 0.3125) = round(241.25)      ← 정수라서 여기서 갈린다
실제 비율   ScaleY = 241/772 = 0.31218                      ← 채우기가 샘플링하는 비율

Scale로 역변환하면  프레임 아래 끝 = (7+241−7)/0.3125 = 771.2   ✗  (772여야 한다)
ScaleY로 역변환하면 프레임 아래 끝 = (7+241−7)/0.31218 = 772.0  ✓
```

초안은 `Scale` 하나로 나눴고 모든 좌표가 최대 **1.2 px** 어긋났다. 좌표를 산출물로 내놓는
기능에서 조용히 틀리는 종류라, `ScaleX` / `ScaleY`를 따로 두고 `Scale`에는 "역변환에 쓰지 말
것"을 적는다. cs §7.3의 off-by-one과 같은 계열이다 — 아무도 안 보면 영원히 안 보인다.

**② 패딩을 먼저 빼고 나눈다.** 순서를 바꾸면 `PadY/Scale` = 22 px 어긋난다.

**③ 패딩은 위아래가 다를 수 있다.** 256 − 241 = 15라 위 7, 아래 8이다. 그래서 **그림의 중심은
프레임의 중심이 아니다** — 반 픽셀 차이다. 검증할 때 중심이 아니라 **양 끝**을 봐야 한다.

**④ 가장자리는 `width − 1`이 아니라 `width`로 클램프한다.** 1024폭 프레임의 오른쪽 끝은
1024다. cs §6.10이 같은 것을 지적했다.

**⑤ 축소는 box average.** 1024→320은 3.2배라 bilinear은 목적 픽셀이 덮는 10픽셀 중 4개만
본다. INTER_AREA와 같은 것이고 비용은 소스 픽셀당 읽기 하나(2.37 MB) 대 추론 2.77 ms다.
다만 **모델이 bilinear로 학습됐다면 여기가 정확도를 조용히 깎는 지점**이다(cs §6.10, [추정]).

**⑥ 크기가 안 맞는 프레임은 거부한다.** 읽고 넘어가면 decimation을 바꾼 직후에 이전 프레임
조각이 섞인 그림이 만들어진다.

## 비용 — 이것이 이 문서의 요점이다

**도입:** 모델 파일 하나를 탐색 경로에 두고 설정에서 켠다. ffmpeg와 같은 방식이다.

**끄기(제거가 아니라):** 모델 파일이 없으면 그걸로 끝이다. 팩토리가 이유를 로그에 남기고
`NullScreenInspector`로 간다. `MilSeqVideoSink`가 "이 장비에서 한 번도 실행된 적 없는 골격"으로
존재하면서 아무 비용도 내지 않는 것과 같은 구조다.

### 제거 — 세 층으로 갈린다 (2026-09-11 재검토)

처음에 "세 줄"이라고 적었는데, 그건 **의존성**에 대해서만 맞다. 위의 결정 둘(호출 지점 둘,
`Unknown`)이 footprint를 넓혔으므로 정직하게 다시 센다.

**① 의존성 — 여전히 세 가지.**
1. `MatroxFrameGrabber.csproj`에서 `<ProjectReference Include="..\Onnx\..." />` 한 줄 삭제
2. `src/Onnx/` 폴더 삭제
3. `ScreenInspectorFactory`가 `NullScreenInspector`를 돌려준다 — **코드 변경 없음**

**② 남지만 공짜 — 폴더째 지워지고, 둬도 된다.**
`IScreenInspector` · `ScreenFinding` · `NullScreenInspector` · `BgrLetterbox` ·
`CenterNetDecoder` · `ScreenInspectorFactory` · `ScreenPatrol`. 전부 `Infrastructure/`,
MIL-free, 테스트됨.

**③ 깔끔하게 되돌아가지 않는 것.**

| 남는 것 | 비용 |
|---|---|
| `CameraChannel`의 호출 지점 둘 | `if` 둘. 파일 자체는 삭제 대상이 아니다 |
| `OutputSettings.InspectSeconds` / `InspectOnAnomaly` | 속성을 지우면 **설정 파일의 키는 조용히 무시된다** — `JsonSerializerOptions`에 `UnmappedMemberHandling.Disallow`가 없다. 비용 0 |
| `RecordingRecord`의 판독 필드 | 판독이 없으면 안 써진다. 비용 0 |
| `AnomalyKind.Unknown` | **설정 창에 회색 "미구현" 한 줄.** 아래 참조 |
| `AnomalyCatalog`의 출처 구분 | 판독기가 없으면 "타일 / 없음"으로 퇴화해 지금과 동작이 같다 |

### 제거된 빌드가 예전 설정 파일을 만나도 깨지지 않는다 (확인함)

이게 ③에서 제일 중요했던 부분이고, 방어가 이미 이중으로 있다.

```csharp
// AnomalyKindSet.FromNames — 판독기 빌드가 "Unknown"을 저장해 둔 경우
if (!Enum.TryParse(name.Trim(), ignoreCase: true, out AnomalyKind kind)) continue;  // 파싱 실패
if ((int)kind < 0 || (int)kind >= AnomalyCatalog.Count) continue;                   // 범위 밖
```

그리고 `PerKind` 배열이 8줄인 파일을 7종류 빌드가 읽으면 `Normalize()`가 **절단한다** — "더 긴
배열은 잘린다. 이 빌드가 이름 붙일 수 있는 것은 잃지 않는다"가 그 메서드의 주석이다.

### `Implemented`는 정적으로 두고, 연결은 밖에서

`Unknown`은 "미구현"이 아니라 "**판독기가 있으면** 구현된 것"이다. 그런데 그걸
`AnomalyCatalog`가 직접 알게 하면 순수 정적 카탈로그에 전역 런타임 상태가 들어오고
`Infrastructure/`가 순수하지 않게 되며 테스트가 실행 순서에 의존한다.

그래서 **카탈로그는 "누가 할 수 있는 종류인가"만 정적으로 말한다.**

```csharp
public enum KindSource { None, Tiles, Inspector }

AnomalyCatalog.SourceOf(Dropout) => Tiles
AnomalyCatalog.SourceOf(Unknown) => Inspector
AnomalyCatalog.SourceOf(Flip)    => None      // 아직 아무도 안 한다
```

"지금 판독기가 있는가"는 뷰모델이 합친다.

```csharp
Implemented => SourceOf(Kind) == Tiles
            || (SourceOf(Kind) == Inspector && _inspector != null);
```

호출부 넷이 이 구분을 따라간다. 둘은 의미가 **또렷해진다**:

- `DetectionSettings.EnabledCount` — 타일 검출기를 만들지 말지를 가르는 값이므로
  `SourceOf(k) == Tiles`만 센다. 지금 우연히 맞는 것(구현된 종류가 Dropout뿐이라서)이
  명시적으로 맞게 된다.
- `AnomalyKindSet.Describe`의 `(미구현)` 꼬리표도 출처를 알고 붙인다.

판독기를 뺀 빌드에서 `Unknown`은 설정 창에 **회색 "미구현 — …"** 으로 나온다
(`IsEnabled="{Binding Implemented}"`, `AppSettingsWindow.xaml:61`). 잔재가 아니라 **사실이고,
화면이 스스로 설명한다.**

## 가져오면서 고치는 것

cs 문서가 짚었고 우리가 그대로 옮기면 안 되는 것들.

- **기본 모델은 fp32.** cs는 int8이 기본인데 2.2배 느리다(§4.1, 같은 머신 실측).
- **`IntraOpNumThreads`는 다시 잰다.** cs의 최적값 8은 **유휴 머신** 값이다. 우리는 8코어를
  취득 스레드 셋과 인코더가 이미 쓰고 있으므로 낮은 값이 최적일 가능성이 높다. 실측 없이
  채택하지 않는다. 그리고 ORT 스레드풀의 기본 스핀을 끈다
  (`session.intra_op.allow_spinning = 0`) — cs §6.5가 코어 하나를 태운 것과 같은 계열이다.
- **피크 판정 동점 처리**(§5.4). logit이 격자에 갇히면 평탄부에서 한 객체가 2~4개 후보가 되고,
  어느 것이 살아남을지가 불안정 정렬에 의존해 프레임 간 좌표 지터가 된다. `(dy,dx)` 사전순으로
  동점을 깬다.
- **시작 시 벤치마크는 기본 off.** cs는 매 실행 `warmup:100, iterations:2000`으로 **13~14초**를
  쓴다. 하니스의 명령으로 옮긴다.
- **int8을 쓸 일이 생기면** 최종 `predictions` QDQ를 제거하거나 출력을 셋으로 분리한다 — 지금은
  Concat 공유 스케일 때문에 offset 서브픽셀 정밀도가 2.6배 손해다(§5.1). 지금은 fp32를 쓰므로
  해당 없음.

**하지 않을 것** (cs §8의 부정 결과 — 측정상 이득 없음): `Marshal.Copy` 제거, letterbox 부분
클리어, 디코드 SIMD화, NMS 알고리즘 개선.

## 먼저 하니스, 나중에 통합

`tools/MilVideoSink/`가 선례다 — 도메인 로직 0의 독립 하네스를 먼저 만들고 거기서 개발·계측한
뒤에 앱에 붙였다. 같은 순서로 간다: `tools/OnnxInspector/`가 모델 파일과 PNG 몇 장을 받아
판정과 시간을 찍는다. 앱이 없어도 돌고, 붙이기 전에 우리 머신에서의 `IntraOpNumThreads`와
실제 프레임에서의 정확도를 여기서 정한다.

## 아직 답이 없는 것

이 검출기는 **"green" 단일 클래스**다. 우리 대상은 AVN 화면의 이상이다. 구조는 위와 같이
정해졌지만 **"어떤 이상을 학습 검출기로 볼 것인가"는 아직 정해지지 않았고, 그게 정해지기
전에는 모델이 없다.** 이 문서는 그 모델이 생겼을 때 **어디에 어떻게 꽂고 어떻게 빼는지**를
미리 정해 두는 것이며, 그 전까지 앱에 들어가는 코드는 없다.
