# 학습 검출기는 떼어낼 수 있는 슬라이스로 넣는다

`C:\projects\cs`의 `GreenDetectorCpu`(MobileNetV2-FPN-CenterNet, ONNX, CPU 전용)를 이 앱에
가져오는 방법. 결론부터: **다섯 파일 중 하나의 절반만 가져오고, 나머지는 우리 쪽에 이미 더 나은
것이 있어 버린다.** 그리고 가져오는 부분은 `IVideoSink`와 `tools/MilVideoSink/`가 이미 세워 둔
패턴대로 **계약 뒤에** 둔다 — 모델 파일이 없으면 아무것도 로드되지 않고, 참조 한 줄을 지우면
빌드에서 사라진다.

측정 환경은 같은 머신이다(Ryzen 7 9700X 8C/16T). 그래서 cs 문서의 추론 시간이 그대로 옮겨온다.

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

## 어디에 붙는가 — 매 프레임이 아니라 사건당 4장

예산이 답을 정한다.

| | 값 |
|---|---|
| fp32 추론 (256×320, IntraOp=8, 유휴 머신) | **2.77 ms** |
| int8 추론 | 5.94 ms — **fp32가 2.2배 빠르다.** 파일이 3.2배 작은 것 말고 얻는 게 없다 |
| 우리 프레임 주기 (120 fps) | **8.33 ms** |
| 3채널 매 프레임 | 3 × 2.77 = **8.31 ms = 예산의 100%** ❌ |
| 사건당 4장 (`StillRing`) | 4 × 2.77 = **11 ms, 사건당 1회** ✓ |

`StillRing`은 이미 사건마다 네 프레임(onset / extreme / recovered / reference)을 MIL 버퍼에
잡아 틱에서 PNG로 쓴다. **거기에 붙인다.** 취득 훅이 아니다 — 훅에 넣은 것은 취득 예산 안에서
돌고 즉시 `M_PROCESS_FRAME_MISSED`로 드러난다. 틱은 이미 PNG를 6~8 ms씩 쓰고 있고 11 ms는 그
안에 든다.

역할도 이렇게 갈라야 맞다. **타일 검출기는 *언제* 이상한지를 120 Hz로 말하고, 학습 검출기는
*무엇이* 화면에 있는지를 사건당 한 번 말한다.** 지금 미구현으로 비활성인 Blackout / Washout /
Flip이 바로 "무엇인지"를 요구하는 종류다.

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

## 계약

`Infrastructure/Detection/`에 둔다. ONNX도 OpenCV도 MIL도 이름이 나오지 않으므로 테스트된다.

| 있음 | 무엇 |
|---|---|
| ✅ `ScreenFinding` | `AnomalyKind` + 점수 + 사각형. `Right`/`Bottom`/`Area` |
| ✅ `IScreenInspector` | `Describe` / `InputWidth` / `InputHeight` / `Reports` / `Inspect(byte[] bgr, Span<ScreenFinding>)` |
| ✅ `NullScreenInspector` | 언제나 0건. 모델이 없을 때의 기본값 |
| ✅ `BgrLetterbox` | 플래나 gbrp → 인터리브 BGR + 축소 + 레터박스 + 역매핑. 순수 산술, 테스트 15개 |
| ⬜ `CenterNetDecoder` | 히트맵 → 후보 → NMS. 다음 포팅 대상 |
| ⬜ `ScreenInspectorFactory` | 시작할 때 고르고 이유를 로그에 (VideoSinkFactory와 같은 규약) |
| ⬜ `OnnxScreenInspector` | `src/Onnx/`. ORT 세션과 버퍼만 |

**계약이 `AnomalyKind`만 말한다.** 모델이 자기 클래스를 갖고 있으면 인스펙터 안에서 우리 종류로
옮기거나 보고하지 않는다. 앱에는 "화면이 어떻게 잘못될 수 있는가"에 대한 어휘가 하나뿐이고,
설정 창이 그 목록이며, 다른 이름으로 올라온 판독 결과는 **끌 수도 보고할 수도 DUT에 걸 수도
없다.**

구현은 **별도 어셈블리** `src/Onnx/`에 두고 `ProjectReference`로만 연결한다. 본체는 ONNX 타입을
한 번도 이름으로 부르지 않는다 — 부르는 순간 제거 비용이 올라간다.

### 구현하면서 실제로 나온 것

`BgrLetterbox`를 쓰면서 테스트가 버그 하나를 잡았다. 역변환이 **공칭 배율**(0.3125)로 나누고
있었는데, 실제 샘플링은 **축별 비율**(241/772 = 0.31218)로 한다 — 축소 크기가 정수라서 갈린다.
프레임 아래쪽 끝이 772가 아니라 771.2로 돌아왔고, 모든 좌표가 최대 1.2 px 어긋났다. 좌표를
산출물로 내놓는 기능에서 조용히 틀리는 종류의 오차다. `ScaleX` / `ScaleY`로 갈라 고쳤다.

## 비용 — 이것이 이 문서의 요점이다

**도입:** 모델 파일 하나를 탐색 경로에 두고 설정에서 켠다. ffmpeg와 같은 방식이다.

**제거:** 세 가지뿐이다.
1. `MatroxFrameGrabber.csproj`에서 `<ProjectReference Include="..\Onnx\..." />` 한 줄 삭제
2. `src/Onnx/` 폴더 삭제
3. `ScreenInspectorFactory`가 `NullScreenInspector`를 돌려준다 — **코드 변경 없음.** 이미
   그것이 모델을 못 찾았을 때의 동작이다

`Infrastructure/`의 계약과 `BgrLetterbox`는 남아도 MIL-free 순수 코드 몇 백 줄이고 테스트가
붙어 있다. 남겨도 비용이 없고 지워도 된다.

**끄기(제거가 아니라):** 모델 파일이 없으면 그걸로 끝이다. 팩토리가 이유를 로그에 남기고
`NullScreenInspector`로 간다. `MilSeqVideoSink`가 "이 장비에서 한 번도 실행된 적 없는 골격"으로
존재하면서 아무 비용도 내지 않는 것과 같은 구조다.

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
