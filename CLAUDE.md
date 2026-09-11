# CLAUDE.md

이 저장소에서 작업할 때의 지침.

용어는 [CONTEXT.md](CONTEXT.md)의 정의를 따른다. 특히 **채널**(보드의 취득 슬롯, 항상 4개)과
**카메라**(채널에 실제로 연결된 장치, 없을 수 있음)를 구분한다.

## 무엇인가

Matrox **Rapixo CXP**(CoaXPress) 프레임그래버 보드 한 장의 **채널 4개**를 **MIL**(Matrox
Imaging Library) API로 다루며 화면에 표시하고 처리하는 C# **WPF** 데스크톱 앱.
라이브 grab과 프레임 단위 처리를 하고, 채널별로 노출 / 취득 프레임레이트 / 트리거 /
화이트밸런스 / DCF를 제어한다. 창 맞춤과 마우스 줌·팬, 더블클릭 전체화면, 스냅샷,
그리고 **녹화 모드 두 가지**(아래 참고)를 지원한다.

`src/`의 줄 단위 해설 — 파일별 역할, 처리하는 모든 예외 경우, MIL 우회책마다의 근거 —
은 [research.md](research.md)를 볼 것.

## 전제조건

- **MIL 10.70** 설치(`C:\Program Files\Matrox Imaging\MIL`). MIL .NET NuGet 패키지는
  `src/nuget.config`에 등록된 로컬 소스에서 가져온다
  (`C:\Program Files\Matrox Imaging\MIL\MIL.NET\NuGet`).
- 라이브 grab에는 카메라가 연결된 **Rapixo CXP** 보드가 필요하다(하드웨어가 없으면 기본 MIL
  시스템으로 폴백하고 "No camera" pane을 보여준다).
- **x64** 전용(MIL NuGet이 x64/arm64만 지원). 대상 프레임워크는 **net10.0-windows**
  (net6.0이 지원 종료되어 올렸다. MIL NuGet은 `net6.0` / `net6.0-windows7.0` 자산을 담고 있어
  상위 TFM에서 그대로 참조된다 — MIL이 배포한 WPF 예제가 net6.0인 것과는 무관하다.
  대신 현장 PC에는 **WindowsDesktop 10.0** 런타임이 있어야 한다).
- 녹화에는 **ffmpeg.exe**가 필요하다. NuGet 의존성이 아니라 런타임에 탐색한다(설정된 경로 →
  앱 폴더 → `PATH` → WinGet → `C:\ffmpeg\bin`). 찾지 못하면 `CanRecord`가 false가 되어 녹화 버튼
  **두 개 모두** 비활성화된다. 모든 인코딩은 ffmpeg를 거치며 MIL 압축 라이선스는 쓰지 않는다
  (`docs/adr/0001-ffmpeg-for-all-encoding.md` 참고).

## 빌드와 실행

```bash
dotnet build MatroxFrameGrabber.slnx -c Release
```

출력 exe (`x64` 경로 조각에 주의 — `Platforms=x64`라 출력이 `bin\x64\` 아래로 들어간다):

```
src\bin\x64\Release\net10.0-windows\MatroxFrameGrabber.exe
```

## 구조

```
MatroxFrameGrabber.slnx        솔루션 (루트)
CONTEXT.md                     용어 글로서리
src/
  MatroxFrameGrabber.csproj    SDK 형식, UseWPF + UseWindowsForms (폴더 선택기 전용), x64
  nuget.config                 MIL.NET 로컬 패키지 소스
  App.xaml(.cs)                MatroxFrameGrabber
  Views/                       MatroxFrameGrabber.Views
                                 MainWindow, CameraPaneView, Styles.xaml (다크 테마)
  ViewModels/                  MatroxFrameGrabber.ViewModels (MainViewModel)
  Mil/                         MatroxFrameGrabber.Mil
                                 MilApplicationManager, CameraChannel,
                                 GenICamFeatures, RecordingSession, BrightnessMeter
  Infrastructure/              MatroxFrameGrabber.Infrastructure  ← MIL-free. 테스트되는 유일한 계층
                                 OutputSettings, FfmpegRecorder, RawFrameWriter,
                                 RawSegmentSession, RelayCommand, NativeMethods,
                                 BrightnessHistory, ChannelRoi, RoiGesture,
                                 DisplayMapping, BrightnessSamplePlan, PwmSweep,
                                 TileGrid, FrameMetrics, AnomalyDetector,
                                 BrightnessLog
tests/                         MatroxFrameGrabber.Tests (165개). csproj가 위 파일들을
                               ProjectReference가 아니라 **소스로 포함**한다 — 앱을 참조하면
                               MIL NuGet(x64 전용)을 끌어와 MIL 없는 머신에서 못 돈다.
docs/
LICENSES/                       동봉 서드파티 라이선스 고지
tools/                          빌드 보조 스크립트 (ffmpeg 스테이징)
research.md                    src/ 심층 분석
```

핵심은 `CameraChannel`이다. 채널마다 `MdigAlloc(M_DEV0+i)` + `MdispAlloc(M_WPF)` +
디스플레이 버퍼 + grab 링 + `MdigProcess` 훅을 잡고, GenICam 피처 제어까지 한다.
이 클래스는 **모델이자 뷰모델**이다(`INotifyPropertyChanged`를 구현하고 `RelayCommand`를
노출하며 pane의 `DataContext`로 직접 바인딩된다) — 그래서 약 1400줄이다.
`MilApplicationManager`는 공유 앱·시스템을 소유하고 채널마다 `CameraChannel`을 하나씩 만든다.

## 녹화 모드 두 가지

채널별로 **상호 배타**이며, 부하가 걸렸을 때의 정책이 **의도적으로 정반대**다.

| | `● Rec` (Live recording) | `◆ RAW` (RAW recording) |
|---|---|---|
| 클래스 | `RecordingSession` | `RawSegmentSession` + `RawFrameWriter` |
| 소스 | grab 버퍼 (3밴드 컬러) | grab 버퍼 (1밴드 Bayer) |
| 경로 | MIL → 메모리 → ffmpeg **stdin 파이프** | MIL → 로컬 `.raw` segment → ffmpeg **배치** |
| 픽셀 포맷 | `gbrp` / `gray` | 센서의 `bayer_*8` (`M_BAYER_PATTERN`에서 조회) |
| 부하 시 | **프레임을 버린다** (라이브 뷰 우선) | **훅을 막는다** (프레임을 잃지 않는다) |
| 프리뷰 | 정상 컬러 | **흑백**, 6프레임마다 한 장 |
| 보드 상태 | 건드리지 않음 | `M_BAYER_CONVERSION` **비활성** |
| 해상도 프리셋 | 적용됨 | 항상 원본 해상도 |

RAW는 segment를 로컬 scratch 폴더(빠른 NVMe)에 쓰고, 변환된 MP4만 출력 폴더로 보낸다.
출력 폴더는 NAS일 수 있는데, RAW는 네트워크 저장소가 감당하기에 너무 빠르다.

## 함정 (겪고 나서 알게 된 것들)

- 보드는 **연결된 카메라가 더 적어도 디지타이저 4개를 보고한다.** 비어 있는 포트에
  `MdigAlloc`을 하면 `M_THROW_EXCEPTION` 아래에서도 모달 MIL 오류 대화상자가 뜬다. 그래서 탐지
  구간을 `MappControl(M_ERROR, M_PRINT_DISABLE/ENABLE)`로 감쌌다 — 이걸 유지하고, 반드시
  `finally`에서 복원할 것(안 그러면 이후의 모든 MIL 오류가 조용히 사라진다).
- **`M_BAYER_CONVERSION`은 보드에 남는 영속 설정이다.** RAW 녹화를 위해 꺼 두면 grab이 끝나도,
  앱을 종료해도, 재부팅해도 그대로 남는다. 그 상태에서 컬러 파이프라인이 raw/모노 데이터를
  잘못 읽어 타일처럼 깨진 이미지가 나온다. 매 `AllocateCamera`에서(`M_SIZE_BAND`를 조회하기
  전에) 다시 켜도록 해 두었고, `RestoreColorAfterRaw()`는 실패할 수 있는 다른 어떤 작업보다
  **먼저** 이것을 되돌린다. 이 설정을 끄는 코드를 새로 추가한다면 같은 복원을 반드시 보장할 것.
- 바인딩되지 않은(0인) `DisplayId`로 `MILWPFDisplay`를 만들면 네이티브 MIL 오류 대화상자가
  뜬다. 그래서 디스플레이 컨트롤은 유효한 DisplayId를 가진 채널이 생긴 뒤에 **코드에서 지연
  생성**한다(`CameraPaneView`와 `MainWindow`의 전체화면 오버레이 참고). 같은 이유로
  `MainWindow` 생성자는 `InitializeComponent()` *이전에* MIL을 할당하고 `DataContext`를 설정한다.
- grab 버퍼는 희소한 **비페이지드/DMA 메모리**를 쓴다. 디스플레이 버퍼는 `M_GRAB`을 빼고
  (페이지드), grab 링은 작게 유지한다(4개. RAW는 24개인데 band-1 프레임이 약 3배 작다).
  할당마다 개별적으로 방어해 두어서, 부족하면 시작을 중단하는 대신 성능을 낮춘다. 필요하면
  **MILConfig**에서 MIL의 비페이지드 풀을 늘릴 것.
- **`MbufGet`은 행 패딩이 들어간 버퍼를 그대로 복사한다**(pitch 2112 > width 2064). 이를 빈틈
  없는 행으로 읽으면 이미지가 어긋난다. 논리적 W×H 영역을 촘촘하게 받아야 할 때는
  `MbufGet2d`를 쓸 것.
- **컬러 바이트를 뽑을 때 `MbufGetColor`를 쓰지 말 것.** 이 버퍼들에서는 패킹 경로가 멈추고,
  플래나 경로는 조용히 0만 돌려준다. 플래나 3밴드 버퍼를 할당하고 `MbufChildColor`로 밴드별
  자식을 만든 뒤 각 밴드를 `MbufGet`해서 ffmpeg에 플래나 `gbrp`로 넘길 것. 그리고 자식을
  부모보다 **먼저** 해제할 것.
- `M_KEYBOARD_USE`가 걸린 `MILWPFDisplay`는 MIL이 최상위 HWND를 서브클래싱하게 만들어, WPF가
  라우팅 이벤트로 바꾸기 전에 키 메시지를 삼킨다. 그래서 `PreviewKeyDown`이 아예 발생하지
  않는다. 전체화면 ESC는 대신 `ComponentDispatcher.ThreadFilterMessage`로 잡는다.
- 대화형 줌·팬은 네이티브 MIL이 처리한다(`M_MOUSE_USE`/`M_KEYBOARD_USE`). 창 맞춤은
  `M_SCALE_DISPLAY, M_ONCE`를 쓴다 — `M_ENABLE`을 쓰면 수동 줌·팬이 잠긴다.
- `MdigProcess` 훅에 무엇을 추가하든 그것은 취득 예산 안에서 실행된다. 무거운 작업은 프리뷰만
  느려지게 하는 게 아니라 grab 자체를 정체시키고, RAW 경로에서는 즉시
  `M_PROCESS_FRAME_MISSED`로 드러난다.
- UI 상태는 **500ms짜리 `DispatcherTimer` 하나**가 모든 채널의 `RefreshStats()`를 호출해
  갱신한다. 새 값을 노출하려면 거기서 올릴 것. 이벤트로 밀어내는 것은 세 가지뿐이다
  (`RecordingFailed`, `CameraLost`, `RawRecordingFinished`). 밝기 측정도 이 틱 위에서 돈다 —
  취득 훅이 아니라 여기다. `MdigProcess` 훅에 넣은 작업은 취득 예산 안에서 돌기 때문이다.
- **정지 상태의 스냅샷은 이전 실행의 마지막 프레임이다.** `SaveSnapshotToOutput`은 디스플레이
  버퍼를 내보내는데, grab이 멈춰 있으면 그 버퍼는 직전 실행의 마지막 프레임에 얼어 있다. 노출을
  바꿔 가며 스냅샷을 찍어도 **바이트 단위로 같은 파일**이 나온다(실측: 3개 노출 × 스냅샷 4장 →
  해시 2개, 전부 이미 끝난 실행의 프레임). `Snap` 버튼을 `IsGrabbing`에 묶고 메서드에서도
  거부하도록 해 두었다. 무언가를 버퍼에서 꺼내 저장하는 코드를 새로 추가한다면 같은 확인을 할 것.
- **노출 하나만 바꿔 비교하면 가산 잡음과 곱셈성 플리커를 구별할 수 없다.** 두 점은 어느 쪽
  이야기로도 이어진다 — 실측에서 이 때문에 한 번 반대로 읽었다. 최소 3점을 재서 **곡선의 모양**을
  볼 것. 플리커는 단조가 아니라 `|sinc(πfT)|`의 V 자로 나타난다. `--expo-scan`이 이 스캔을
  자동으로 돌린다.
- **`Mim*` 함수는 하나의 라이선싱 그룹이 아니다.** `MimResize`와 `MimShift`는 MIL-Lite에
  포함되지만 `MimStat`은 Image Processing(IM) 모듈이 필요하고 이 장비에는 없다 — 호출하면
  `Licensing error. A module was used without a valid license`가 난다. 통계·히스토그램류를
  MIL로 처리하려다 이 벽에 부딪히므로, 호스트에서 직접 계산할 것을 전제로 설계한다.
- **`MbufBayer`는 이 장비에서 예외도 오류도 없이 블록한다.** 취득 훅에서 호출하면 훅이 첫
  프레임에서 멈추고(`M_PROCESS_FRAME_COUNT`가 1에 고정), UI 스레드에서 호출하면 앱 전체가
  정지한다. 대상 버퍼를 바꿔도(중간 `M_PROC` 버퍼 경유) 마찬가지다. `MbufGetColor` 함정과 같은
  계열이다. **호스트 디베이어를 MIL로 할 수 없다** — 컬러가 필요하면 보드의
  `M_BAYER_CONVERSION`을 쓰면서 ROI/decimation으로 페이로드를 줄이는 것이 유일한 길이다.
- **취득 대역폭에는 공유 천장이 있다(호스트 DMA **3.68 GB/s**, 2026-08-27 실측).** 채널 수와
  무관하게 합계가 여기서 고정되고, 초과분은 `M_PROCESS_FRAME_MISSED`로 조용히 사라진다.
  성능 작업을 하기 전에 [research.md](research.md) 8절의 실측표를 볼 것 — **표시 경로를
  최적화해도 취득 fps는 늘지 않는다**(측정으로 확인). 프레임당 페이로드만이 레버다.
  **그 천장은 PCIe 링크 폭이었다.** 보드가 칩셋 뒤의 x4 슬롯에 있을 때 1.70 GB/s였고, CPU 직결
  x16 슬롯으로 옮겨 x8로 붙자 **3.68 GB/s**가 됐다(Gen2 x8 이론치 4.0의 92%).
  **이제 더 올릴 수 없다** — `MaxLinkSpeed`도 `MaxLinkWidth`도 현재값과 같아 보드가 자기 최대에
  있다. 카드를 다시 옮기거나 BIOS를 만지기 전에 `CurrentLinkWidth`부터 읽을 것(명령은 8절에 있다).
  **100 fps에서는 원본 해상도 컬러 3채널이 들어온다**(2.87 GB/s, 천장의 78%) — 그 지점에서는
  디시메이션도 1밴드도 필요 없다.
- **전원을 내리면 카메라 설정이 초기화된다 — 앱 종료와 다르다.** 카드 교체로 PC 전원을 내린 뒤
  노출이 5388 µs로, `AcquisitionFrameRateEnable`이 Off로 돌아와 있었다. 아래 항목의 "앱을 닫아도
  남는다"는 앱 재시작에 대한 것이고, 전원 재인가는 그것과 다르다. 하드웨어를 만진 뒤에는 노출과
  레이트 캡을 다시 확인할 것.
- **카메라 GenICam 설정 일부는 보드가 아니라 카메라에 영속된다.** `AcquisitionFrameRate` /
  `AcquisitionFrameRateEnable`, `DecimationHorizontal` / `Vertical`이 그렇다. 앱을 닫아도 남으니
  `M_BAYER_CONVERSION`과 같은 복구 규율을 적용할 것. 그리고 이들은 **정수형 피처라
  `M_TYPE_MIL_INT`로 써야 한다** — `M_TYPE_DOUBLE`로 쓰면 조용히 무시된다.
- **한 채널만 느리면 케이블·링크보다 노출을 먼저 보라.** `AcquisitionFrameRate`의 최대값은
  `ExposureTime`에 종속된다. 노출 100 ms면 그 채널의 상한은 10 fps다.
- **GenICam 피처 쓰기는 read-back 하기 전까지 검증되지 않았다.** `MdigControlFeature`가 예외를
  던지지 않고, `M_PRINT_DISABLE` 상태에서는 출력도 없어서 이를 감싼 헬퍼가 **true를 반환한다** —
  그런데 값은 그대로일 수 있다. 반환값도, `M_FEATURE_ACCESS_MODE`도 믿을 수 없다
  (RW라고 답하는 상태에서 무시된 쓰기를 실측했다). **쓴 뒤에는 반드시 다시 읽어 확인할 것.**
  현재 값은 앱 시작 때마다 `mil-errors.log`에 찍힌다.
- **페이로드를 줄이는 수단은 `DecimationHorizontal` / `Vertical` 뿐이다.** 이 카메라는 카메라 쪽
  ROI(`Width` / `Height` / `OffsetX` / `OffsetY`)를 지원하지 않는다 — 쓰기를 받아들이고 무시한다.
  판정 범위 지정은 소프트웨어 값인 **분석 ROI**로 한다.
  (실측: decimation 2 → 1024×772 컬러 184.1 fps, 유실 0)
- **앱을 강제 종료하면 카메라의 지오메트리 노드가 잠긴다.** 작업 관리자 종료, 디버거 중단,
  크래시 — `Window_Closing`을 타지 않고 죽으면 `DecimationHorizontal` 같은 피처 쓰기가 그 뒤로
  **조용히 거부된다**(예외도 출력도 없고 반환값은 성공이다). 증상이 "코드가 맞는데 값이 안
  바뀐다"로 나타나서 코드를 의심하게 만든다.
  **복구는 전원 재인가가 아니라 앱을 한 번 정상 종료하는 것으로 된다** — 실측으로 확인했다.
  즉 한 번 정상 실행·종료하면 다음 실행부터 쓰기가 다시 먹는다. 검증 스크립트에서
  `Process.Kill()` 대신 `CloseMainWindow()`를 쓸 것.

## 에이전트 스킬

### 이슈 트래커

이슈는 이 리포의 GitHub 이슈(`chakoon-psg/matrox-frame-grabber`)에 두고 `gh` CLI로 다룬다.
`docs/agents/issue-tracker.md` 참고.

### 트리아지 라벨

다섯 가지 정규 역할을 쓰며, 라벨 문자열은 역할 이름과 같다. `docs/agents/triage-labels.md` 참고.

### 도메인 문서

단일 컨텍스트 — 루트의 `CONTEXT.md`와 `docs/adr/`. `docs/agents/domain.md` 참고.
