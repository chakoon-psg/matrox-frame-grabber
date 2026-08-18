# `src/` 심층 분석 리포트 — Matrox Rapixo CXP 멀티 카메라 뷰어

> 대상: `C:\projects\matrox-frame-grabber\src` (소스 20개 파일, 4,048 LOC)
> 기준 커밋: `07e9cbc` (branch `fix/color-rec-and-toolbar-ux`)
> 작성일: 2026-08-18

---

## 0. 한 줄 요약

Matrox **Rapixo CXP**(CoaXPress) 프레임그래버 1장에 물린 **최대 4대**의 카메라를 **MIL 10.70** .NET
바인딩으로 라이브 그랩·표시하고, GenICam 피처로 제어하며, 스냅샷 / H.264 라이브 녹화 /
**무손실 RAW-Bayer 세그먼트 녹화** 3가지 산출물을 만드는 단일 프로세스
**WPF(net6.0-windows, x64)** 데스크톱 앱.

---

## 1. 전체 아키텍처

```
App.xaml ──> Views/MainWindow.xaml
                 │  (생성자: MIL 할당 → DataContext 세팅 → InitializeComponent)
                 ├─ MainViewModel ────────> MilApplicationManager
                 │   (DispatcherTimer 500ms)      │  MappAlloc + MsysAlloc (시스템 1개 공유)
                 │                                └─ CameraChannel × 4
                 │                                      ├─ MdigAlloc(M_DEV0+i)
                 │                                      ├─ MdispAlloc(M_WPF)  ─┐
                 │                                      ├─ 표시버퍼(M_DISP)     │
                 │                                      ├─ 그랩링(M_GRAB) 4/24 │
                 │                                      ├─ GenICamFeatures     │
                 │                                      ├─ RecordingSession    │
                 │                                      └─ RawSegmentSession   │
                 └─ CameraPaneView × 4 ── MILWPFDisplay(DisplayId) ────────────┘
```

**계층 규칙(컨벤션)**

| 레이어 | 네임스페이스 | 책임 | MIL 의존 |
|---|---|---|---|
| `Views/` | `MatroxFrameGrabber.Views` | XAML + 코드비하인드. 대화상자·파일 선택·MessageBox | `MILWPFDisplay`만 |
| `ViewModels/` | `MatroxFrameGrabber.ViewModels` | 채널 목록 노출, 전역 명령, 통계 타이머 | 없음 |
| `Mil/` | `MatroxFrameGrabber.Mil` | MIL 리소스 수명주기 · 그랩 루프 · GenICam | 전면 의존 |
| `Infrastructure/` | `MatroxFrameGrabber.Infrastructure` | ffmpeg, 파일 I/O, 설정, Win32 interop | **MIL 무의존** |

> 눈여겨볼 점: `CameraChannel`이 **모델이자 뷰모델**이다. `INotifyPropertyChanged`를 직접 구현하고
> `RelayCommand`를 노출하며, XAML이 `Channels[0..3]`을 패널의 `DataContext`로 바로 바인딩한다.
> 채널별 ViewModel 래퍼가 없는 "얇은 MVVM"이며, 이것이 1,394줄짜리 `CameraChannel`의 이유다.

---

## 2. 파일별 역할 (정독 결과)

### 2.1 진입점 / 셸

**`App.xaml(.cs)`** (12 + 11줄)
- `StartupUri="Views/MainWindow.xaml"`, `Views/Styles.xaml`을 머지 딕셔너리로 로드.
- 코드비하인드는 사실상 비어 있음 — 전역 예외 핸들러 없음(→ §6-1 참조).

**`Views/Styles.xaml`** (191줄)
- 다크 테마 팔레트(`#1E1E1E` 배경 / `#2A2A2A` 패널 / `#0E639C` 액센트)와
  `Button` / `TextBox` / `ComboBox` / `CheckBox` / `Label` / `Expander` 암시적 스타일.
- **`RecToggle`**(체크 시 빨강 `#C62828`) vs **`RawToggle`**(체크 시 주황 `#E65100`) —
  "라이브 H.264 녹화"와 "무손실 RAW 녹화"를 **색으로 구분**하는 것이 이 앱의 핵심 UX 규약이다.
  같은 규약이 패널 배너(`RecBrush` / `RawBrush`)에서도 반복된다.
- `ComboBox`는 템플릿 재정의 없이 프로퍼티 레벨만 다크 처리(주석에 명시) — 드롭다운 팝업 일부에
  시스템 기본 스타일이 남는 것을 감수한 타협.

### 2.2 `Views/MainWindow.xaml(.cs)` (155 + 266줄)

**툴바 구성** (커밋 `121d605` → `07e9cbc`에서 단순화)
- 항상 필요한 것만 노출: `Start All` / `Stop All` │ `● Rec All` / `◆ RAW All` / `⚙ Rec` 팝업 │ `Open` / `Browse…`
- 가끔 쓰는 설정(RAW 자동정지 초, 세그먼트 길이, 출력 해상도)은 `RecSettingsPopup` 뒤로 숨김.
- 오른쪽 끝에 `SystemStatus`(할당된 시스템 디스크립터 + 디지타이저 수) 고정.

**생명주기 (중요한 순서 규약)**

```csharp
public MainWindow() {
    _manager = new MilApplicationManager(); _manager.Allocate();  // ① 먼저 MIL 할당
    ... RecordingFailed / CameraLost / RawRecordingFinished 구독 ...
    DataContext = _viewModel;                                     // ② 그 다음 DataContext
    InitializeComponent();                                        // ③ 마지막에 비주얼 트리
}
```

이유가 주석에 명시돼 있다: **`MILWPFDisplay`가 DisplayId=0으로 생성되면 MIL이 네이티브 모달
에러 대화상자를 띄운다.** 그래서 트리가 만들어지기 전에 유효한 DisplayId가 준비돼야 한다.
생성자에서 예외가 나면 `_initError`에 담아뒀다가 `Window_Loaded`에서 MessageBox로 보여준다
(창이 아직 없는 생성자 시점에 MessageBox를 띄우지 않기 위해).

종료는 `Window_Closing`: 전체화면 해제 → ESC 후크 제거 → `_viewModel.Shutdown()`(타이머 정지)
→ `DataContext = null` → `_manager.Free()`.

**전체화면 오버레이**
- `MainContent`를 `Collapsed`, `FullscreenOverlay`를 `Visible`로 토글.
- `MILWPFDisplay`를 **그때그때 `new`** 해서 `FullscreenBorder.Child`에 꽂고, 나올 때 `null`로 버린다
  (같은 DisplayId를 두 컨트롤이 동시에 물지 않게 하려는 의도).
- `SizeChanged` 때마다 `FitToWindow()` 재호출, 더블클릭으로 진입/이탈.

**ESC 처리 — 이 파일에서 가장 비직관적인 부분**

```
M_KEYBOARD_USE가 켜진 MILWPFDisplay는 MIL이 최상위 HWND를 서브클래싱해서
키 메시지를 WPF 라우티드 이벤트로 바꾸기 전에 삼켜버린다 → Window.PreviewKeyDown이 오지 않는다.
해결: ComponentDispatcher.ThreadFilterMessage 로 디스패처 펌프 단계(모든 WndProc보다 앞)에서
      WM_KEYDOWN / WM_SYSKEYDOWN 의 VK_ESCAPE 를 가로챈다.
```

후크는 전체화면 진입 시에만 설치(`InstallEscHook`)하고 나갈 때 제거하며, `handled = true`로
MIL이 ESC를 다시 처리하지 못하게 막는다.

**안전장치**: `StopAll_Click`은 녹화 중일 때만 한국어 확인 대화상자를 띄운다(되돌릴 수 없는 동작이므로).
`OnRawRecordingFinished`는 **실패만** 알린다(성공은 그냥 출력 폴더에 mp4가 생기는 것으로 충분).

### 2.3 `Views/CameraPaneView.xaml(.cs)` (191 + 218줄)

한 카메라 패널. `DataContext`는 `CameraChannel`.

- **헤더**: `[CAM0]` 고정 포트 태그(현장에서 물리 포트를 짚기 위한 것) + 편집 가능한 `OutputName`(파랑) +
  `▶Start / ■Stop / ●Rec / ◆RAW / Snap / Fit / 1:1 / ⤢`.
- **`Expander`(기본 접힘)** 안에 세부 설정: "이 설정을 전체 카메라에 적용" 버튼, Name, Exposure,
  Acq Rate, Trigger, White Balance, DCF/Features. 라이브 뷰 면적을 최대로 두려는 의도.
- **녹화 배너**: `RecordingActive`가 true면 뷰 상단에 상시 표시. `IsRawRecording` `DataTrigger`로
  배경이 주황으로 바뀐다. 텍스트는 `RecordingBannerText`(한국어, 모드+세그먼트+경과시간+드롭 수).

**코드비하인드가 하는 일 = "뷰 컨텍스트가 필요한 것만"**
- `Start / Fit / 1:1`은 XAML에서 `Command` 바인딩(`StartCommand` 등) — 대화상자 불필요.
- `Stop / Rec / RAW / Snap / Apply* / LoadDcf / Features`는 `Click` 핸들러 — MessageBox나
  파일 대화상자가 필요하기 때문. 이 분리가 이 프로젝트의 명확한 컨벤션이다(커밋 `d870884`).
- `_display` 생성은 `DataContextChanged`에서 **한 번만**, 그리고 `DisplayId != M_NULL`일 때만.
- `_rawTimer`: 패널별 RAW 자동정지 타이머(`Output.RawDurationSeconds > 0`일 때만 arm).
- `Stop_Click`은 녹화 중이면 한국어 확인 → `StopRawTimer` → `StopRawRecording` → `StopGrab`.

### 2.4 `ViewModels/MainViewModel.cs` (274줄)

- `DispatcherTimer` 500ms → 모든 채널 `RefreshStats()` + `AnyRecording` / `AnyRawRecording` 알림.
  **세션 내내 계속 돈다**(패널별 Start도 fps를 갱신해야 하므로 — 주석에 명시).
- `StartAllCommand` / `StopAllCommand`는 **항상 Enabled** — 패널별 시작/정지와 상태가 어긋나는 것을
  막기 위해 의도적으로 게이팅하지 않음(주석에 명시).
- `ApplyToAll(source, kind)` / `ApplyAllSettings(source)`:
  `CameraSettingKind { Exposure, AcqRate, Trigger, WhiteBalance }`를 돌며
  **대상 카메라가 그 기능을 지원할 때만** 복사. 세부 규칙은 `ApplySettingFrom` 한 곳에 모여 있다:
  - Exposure: `SupportsExposureAuto`면 Auto 먼저 맞추고, **소스가 Auto가 아닐 때만** 수동값 복사
  - AcqRate: Enable 토글 먼저 → `CanSetAcqRate`일 때만 값 복사
  - Trigger: On/Off + 소스(비어있지 않을 때만)
  - WhiteBalance: Auto 먼저 → 소스가 수동일 때만 R/B 비율 복사
- `ToggleRawAll()`: 전 채널 RAW 시작 → **하나라도 성공했을 때만** 자동정지 타이머를 arm.
  실패는 `"{channel.Name}: {err}"`로 합쳐 반환 → MainWindow가 MessageBox.
- `RawSeconds` / `RawSegSeconds`는 문자열 프로퍼티(TextBox 바인딩) → 파싱 실패 시 조용히 무시.
- `Shutdown()`은 타이머만 정지(MIL 해제는 MainWindow 담당).

### 2.5 `Mil/MilApplicationManager.cs` (123줄)

- `MappAlloc` → `MappControl(M_ERROR, M_THROW_EXCEPTION)` → `MsysAlloc`.
- 시스템 디스크립터 **폴백 체인**: `M_SYSTEM_RAPIXOCXP` → `M_SYSTEM_DEFAULT`.
  하드웨어가 없어도 앱이 뜨고 "No camera" 패널을 보여주기 위함.
- `M_DIGITIZER_NUM`을 조회해 `i < DigitizerCount`인 채널만 카메라 후보로 표시.
- `OutputSettings.Load()`를 한 번 해서 4개 채널이 **공유**한다.
- `Free()`는 **역순 해제**: 채널 → `MsysFree` → `MappFree`.

### 2.6 `Mil/GenICamFeatures.cs` (150줄)

디지타이저 1개에 대한 GenICam SFNC 접근 래퍼. **설계 원칙 하나로 관통된다.**

> 모든 접근은 `Available(name)` 존재 확인 + `try/catch(MILException)` → 실패하면 `false` 반환.
> 즉 **기능이 없는 카메라는 그 컨트롤이 조용히 비활성화될 뿐, 예외가 UI로 새어나가지 않는다.**

제공 API: `Available` / `SetString` / `SetDouble` / `SetBool` / `ExecuteCommand`(command 피처) /
`TryGetString` / `TryGetDouble(inquireType)` / `TryGetBool` / `EnumEntries`.

- `TryGetDouble`은 `inquireType`을 인자로 받아 `M_FEATURE_VALUE` / `_MIN` / `_MAX`를 모두 커버한다
  (Exposure 범위 힌트, AcqRate 최대치에 사용).
- `EnumEntries`는 `M_FEATURE_ENUM_ENTRY_COUNT` → `M_FEATURE_ENUM_ENTRY_NAME + i`로 순회.
- `Digitizer` 프로퍼티는 **재할당 때마다 갱신**해야 한다(`AllocateCamera`가 세팅, `FreeCamera`가 `M_NULL`).
- 문자열 읽기는 `StringBuilder(256)` 고정 — 255자 초과 피처 값은 잘린다.

### 2.7 `Mil/CameraChannel.cs` (1,394줄) — 앱의 심장

#### 소유 리소스
`_digId`(디지타이저), `_dispId`(WPF 디스플레이), `_dispBufId`(표시 버퍼), `_graId`(그래픽 컨텍스트),
`_grabBuffers`(그랩 링), `_hookData` + `_hookHandle`(GCHandle) + `_hookDelegate`.

#### 할당 흐름

```
Allocate(sys, cameraAvailable, dcf)
  ├─ MdispAlloc(M_WPF) + MdispControl(M_TITLE) + MgraAlloc + new RecordingSession
  └─ AllocateCamera()
       ├─ [에러 출력 억제] MdigAlloc(M_DEV0+idx) → 실패 시 _digId = M_NULL
       ├─ M_CAMERA_PRESENT == M_NO 면 MdigFree 후 M_NULL
       ├─ M_BAYER_CONVERSION = M_ENABLE          ★ (아래 2번)
       ├─ CanRecord = (ffmpeg 발견 여부)
       ├─ AllocateBuffers(4)
       └─ _features.Digitizer = _digId; RefreshFeatureState()
```

**예외 케이스 4가지가 여기 응축돼 있다.**

1. **빈 포트 프로브** — Rapixo CXP는 카메라가 없어도 디지타이저 4개를 보고한다. 빈 포트에
   `MdigAlloc`하면 `M_THROW_EXCEPTION` 상태에서도 **네이티브 모달 대화상자**가 뜬다.
   → `MappControl(M_DEFAULT, M_ERROR, M_PRINT_DISABLE)`로 감싸고 **`finally`에서 반드시 복구**.
2. **Bayer 변환의 영속성** — `M_BAYER_CONVERSION`은 **보드에 남는 설정**이다. RAW 녹화를 위해 껐다가
   앱이 죽으면 다음 실행에서도 꺼진 채라, 컬러 파이프라인이 mono/raw 데이터를 타일/깨진 이미지로
   오해한다. 그래서 **`M_SIZE_BAND`를 조회하기 전에** 매번 다시 `M_ENABLE` (커밋 `568839f`).
3. **비페이지드(DMA) 메모리 고갈** — 그랩 버퍼는 희소한 non-paged 풀을 쓴다. 요청은
   `REQUESTED_GRAB_BUFFERS = 4`지만 **실패해도 중단하지 않고 얻은 만큼만** 쓴다(루프 안 개별
   try/catch, 실패 시 `break`). 2개 미만이면 상태 텍스트가 `Low memory: only N grab buffer(s)`로 바뀐다.
4. **표시 버퍼는 `M_GRAB`을 일부러 뺀다** — `M_IMAGE + M_DISP + M_PROC`만. 훅이 복사해 넣는
   대상이지 그랩 타깃이 아니므로, 페이지드 메모리에 두어 DMA 풀을 아낀다.

추가로 `M_SIZE_BIT > 8`이면 디스플레이를 `M_BIT_SHIFT` 뷰 모드로 놓고 `M_VIEW_BIT_SHIFT = bit − 8`.
카메라가 없으면 표시 버퍼에 `MgraText`로 "no camera"를 그려 넣는다.
표시 설정은 `M_MOUSE_USE` / `M_KEYBOARD_USE`(네이티브 줌·팬) + `M_SCALE_DISPLAY, M_ONCE`
(**`M_ENABLE`이 아니라 `M_ONCE`** — 한 번만 맞추고 이후 수동 줌/팬을 살려두기 위해).

#### 그랩 루프

```
StartGrab: ChannelHookData 생성 → GCHandle.Alloc(고정) → MIL_DIG_HOOK_FUNCTION_PTR 델리게이트
           → MdigProcess(M_START, hook, GCHandle.ToIntPtr)
           실패 시 GCHandle / 델리게이트 / 데이터를 **정리한 뒤 다시 throw** (핀 누수 방지)
StopGrab:  StopRecording() → MdigProcess(M_STOP) → GCHandle.Free()
```

`_hookDelegate`를 필드로 잡아두는 것이 필수다(GC가 콜백을 수거하면 네이티브 호출이 크래시).

**훅 본체 `OnGrabbedFrame(grabbed, display)` — 두 갈래**

```
RAW 세션 활성?  ── YES ──> buf = seg.Rent()                 (필요하면 세그먼트 롤오버)
                          MbufGet2d(grabbed, 0,0, W,H, buf)  ★ MbufGet 아님
                          seg.Feed(buf)
                          6프레임마다 MbufCopy → 흑백 프리뷰 (RAW_DISPLAY_EVERY)
                          return
                ── NO ───> MbufCopy(grabbed → display)
                          _recording?.Feed(grabbed)
```

★ **`MbufGet2d`를 쓰는 이유(커밋 `f2fde52`)**: `MbufGet`은 행 패딩(pitch 2112 > width 2064)을 포함해
복사하므로, 나중에 2064바이트 행으로 되읽으면 **영상이 사선으로 밀린다(shearing)**.
`MbufGet2d`는 논리적 W×H 영역을 **packed**로 복사한다.

#### `RefreshStats()` — UI 타이머가 500ms마다 호출하는 폴링 허브

1. `M_PROCESS_FRAME_RATE` → `_frameRate`
2. RAW 중이면 `M_PROCESS_FRAME_MISSED` → `_rawMissed`
   ("무손실"이라 해놓고 실제로 유실된 프레임을 숨기지 않고 배너에 `⚠ dropped N`으로 노출)
3. **카메라 분리 감지**: `M_CAMERA_PRESENT`가 **2회 연속** 실패해야 `_cameraLost`(일시 블립 방지).
   감지 시 **녹화만 중지하고 `StopGrab`은 사용자에게 맡긴다** — 죽은 포트에 `M_STOP`을 거는 것이
   위험하다는 판단(주석 명시). 다시 붙으면 `_cameraLost` 해제.
4. ffmpeg가 죽었으면(`_recording.Failed && IsActive`) 정리 후 `RecordingFailed` 이벤트
5. RAW 세그먼트 세션이 실패했으면 `StopRawRecording()`
6. `_rawFinishing.WaitConversions(0)`으로 백그라운드 변환 완료를 폴링 → `RawRecordingFinished` 이벤트
7. 마지막에 `FrameRate / FrameCount / StatusText / RecordingActive / RecordingBannerText` 알림

#### 두 가지 녹화 모드 (반드시 구분)

| | **Rec (컬러 라이브)** | **RAW (무손실)** |
|---|---|---|
| 클래스 | `RecordingSession` | `RawSegmentSession` |
| 소스 | 표시 버퍼(3-band 컬러) | 그랩 버퍼(1-band Bayer) |
| 경로 | MIL → 메모리 → ffmpeg **stdin 파이프** | MIL → 로컬 `.raw` 세그먼트 → ffmpeg **배치 변환** |
| 픽셀 포맷 | `gbrp`(컬러) / `gray`(mono) | `bayer_rggb8` |
| x264 preset | `veryfast` | `ultrafast` |
| 프레임 유실 | **있음**(큐 가득 차면 드롭) | 없음이 목표(백프레셔), 보드 드롭은 계측 |
| 프리뷰 | 정상 컬러 | **흑백**, 6프레임마다 |
| 보드 설정 변경 | 없음 | `M_BAYER_CONVERSION = M_DISABLE` |
| 그랩 링 | 4 | 24 (`RAW_GRAB_BUFFERS`) |
| 저장 위치 | 출력 폴더 직접 | 스크래치(로컬 NVMe) → 출력 폴더 |
| 상호배타 | RAW 중엔 시작 불가 | Rec 중엔 시작 불가 |

**`StartRawRecording`의 안전 시퀀스** (이 파일에서 가장 조심스럽게 쓰인 부분):

```
사전 검증(디지타이저 / 중복 / IsRecording / CanRecord / 출력·스크래치 폴더 / ffmpeg)
_rawResumeGrab = _isGrabbing;  if (_isGrabbing) StopGrab();
try {
    SetBayerConversion(false); FreeBuffers(); AllocateBuffers(24);
    band != 1 이면 → "raw Bayer 미지원" + RestoreColorAfterRaw() 후 실패 반환
    new RawSegmentSession(...); _rawRecording = true; StartGrab();
} catch {
    세션 Finish / WaitConversions(2000) / Dispose → RestoreColorAfterRaw()   ★ 컬러 반드시 복구
}
```

`RestoreColorAfterRaw()`는 **`SetBayerConversion(true)`를 가장 먼저** 호출한다 —
그 뒤(FreeBuffers/AllocateBuffers/StartGrab)가 실패해도 보드는 컬러로 돌아가도록.

`FreeCamera()`도 같은 이유로 **RAW 중지 → 남은 변환 최대 15초 대기 → StopGrab → 녹화 finalize 15초 대기**
순서로 종료한다.

#### GenICam 제어 표면

- SFNC 이름을 상수로 고정: `ExposureTime` / `ExposureAuto` / `TriggerSelector` / `TriggerMode` /
  `TriggerSource` / `TriggerSoftware` / `AcquisitionFrameRate`(+`Enable`) / `BalanceWhiteAuto` /
  `BalanceRatioSelector` / `BalanceRatio`.
- **바인딩 패턴**: `Supports*`(기능 존재) → `CanSet*`(존재 && Auto 꺼짐) → `*Input`(텍스트) → `Apply*()`.
  Auto 토글은 **카메라가 수락했을 때만** 백킹 필드를 커밋한다
  (`if (SetExposureAuto(value)) _exposureAuto = value;`) — UI가 하드웨어와 거짓말하지 않게.
- 화이트밸런스는 `BalanceRatioSelector`를 "Red"/"Blue"로 바꾼 뒤 `BalanceRatio`를 읽고 쓰는
  **selector 패턴**(Green이 1.0 기준). `Once`는 1회 자동 WB.
- `SetTriggerMode`는 항상 `TriggerSelector = "FrameStart"`를 먼저 시도한 뒤 `TriggerMode`.
- `ExposureAuto` 판정은 `!= "Off"`, `BalanceWhiteAuto` 판정은 `== "Continuous"`(둘의 기준이 다름).
- 모든 파싱/포매팅은 `CultureInfo.InvariantCulture`(한국 로캘 소수점 문제 회피).
- **`DumpDiagnostics()`**: 존재하지 않을 수도 있는 피처 이름들(`ResultingFrameRate`,
  `DeviceLinkSpeedBps`, `CxpLinkConfiguration`, `M_GC_PAYLOAD_SIZE` 등)을 투기적으로 조회하므로
  전체를 `M_PRINT_DISABLE`로 감싼다. "왜 스펙보다 fps가 낮은가" 진단용. **현재 호출부가 없다**(§6-4).
- `OpenFeatureBrowser()` = `MdigControl(M_GC_FEATURE_BROWSER, M_OPEN + M_ASYNCHRONOUS)`.
- `ReloadWithDcf(path)`: `FreeCamera` → `_cameraAvailable = true` 강제 → `AllocateCamera` → 필요 시 재시작.

#### 파일명 규약

`SafeName()` = `OutputName`의 금지문자·공백을 `_`로 치환 + **`_ch{index}` 접미사 강제**
(두 카메라가 같은 이름을 써도 충돌하지 않게). 최종 산출물: `{SafeName}_{yyyyMMdd_HHmmss}.png / .mp4`.

### 2.8 `Mil/RecordingSession.cs` (234줄) — 컬러 라이브 녹화

- `Start(sourceBuf, settings, baseName, fps)`가 소스 버퍼에서 **geometry/포맷을 추론**하고,
  해상도 프리셋(`ScaleFactorFor`)을 적용한 뒤 폭·높이를 `&= ~1`로 짝수화(H.264 요구).
- fps는 `CameraChannel`이 넘긴다: 실측 `_frameRate > 1.0`이면 그것, 아니면
  `M_SELECTED_FRAME_RATE`, 그것도 없으면 30.0.
- **컬러 프레임 추출 방식이 이 파일의 핵심 함정(커밋 `4db0b71`)**:

  ```
  MbufGetColor 의 패킹 경로는 이 버퍼에서 hang 하거나 0을 반환하고,
  평범한 MbufGet 은 band 0 만 준다.
  → planar 3-band 캡처 버퍼를 만들고 MbufChildColor 로 밴드별 자식(_b0/_b1/_b2)을 만든 뒤
    밴드마다 MbufGet 해서 ffmpeg 에 planar `gbrp` 로 먹인다.   순서는 [G][B][R].
  ```

  (같은 지식이 메모리 `mil-color-byte-extraction`에도 기록돼 있음)
- `Feed()`는 **`_lock` 안에서** 실행되고, 맨 앞에서 `_recorder.HasRoom`을 확인해
  **인코더가 밀리면 추출 자체를 건너뛴다**(취득 스레드와 라이브 뷰를 지키기 위해).
  `catch`는 콜백 도중 세션을 무너뜨리지 않고 그 프레임만 버린다.
- 8비트 초과 소스는 `MimShift(src, capture, -shift)`로 축소, 아니면 `MbufCopy`.
- `Stop()`은 락 안에서 필드를 스냅샷/널링만 하고, 실제 `recorder.Stop()` + `MbufFree`는
  `Task.Run`으로 **비동기 finalize**(UI 프리즈 방지). `WaitFinalize(ms)`로 MIL 해제 전 동기화.
- `FreeBuffers`는 **자식(밴드) 버퍼를 부모보다 먼저** 해제.
- 프레임 버퍼는 `ConcurrentQueue<byte[]>` 풀(최대 12개)로 재사용, `FrameReturned` 콜백으로 회수.
- `StatusSuffix()`가 `  ● REC 01:23 (dropped 5)` 형태의 상태 문자열을 만든다.

### 2.9 `Infrastructure/FfmpegRecorder.cs` (220줄)

- ffmpeg를 자식 프로세스로 띄우고 **stdin 파이프**로 rawvideo를 밀어 넣는다.
- 인자: `-f rawvideo -pixel_format {pixFmt} -video_size WxH -framerate F -i pipe:0 -an
  -vf crop=trunc(iw/2)*2:trunc(ih/2)*2 -c:v libx264 -preset veryfast -pix_fmt yuv420p -movflags +faststart`
- `ResolveFfmpegPath(configured)` 탐색 순서: 설정값 → `PATH` 스캔 → **WinGet `Links`** →
  `WinGet\Packages` 재귀 검색 → `C:\ffmpeg\bin`, `C:\Program Files\ffmpeg\bin`.
  결과는 **static 캐시**(WinGet 트리 재귀 스캔이 비싸서).
- 큐 용량 **8**, `TryAdd` 실패 시 **드롭 + `DroppedFrames++`**(블로킹하지 않음 = 라이브 우선).
- stderr는 `BeginErrorReadLine`으로 **반드시 배수**(안 하면 파이프가 막혀 ffmpeg가 멈춘다),
  마지막 5줄만 롤링 보관해 `LastError`로.
- `Exited` 이벤트 → `_running = false` + `Failed` 이벤트 → `RefreshStats`가 UI에 전달.
- `Stop()`: 큐 완료 → 라이터 3초 조인 → stdin 닫기(EOF로 ffmpeg가 mp4 finalize) →
  8초 대기 후 미종료면 `Kill()` → `Cleanup()`에서 이벤트 해제·Dispose.

### 2.10 `Infrastructure/RawFrameWriter.cs` (87줄)

- 고정 크기 프레임을 **전용 스레드**로 파일에 쓴다. `BlockingCollection`(기본 용량 64) +
  `ConcurrentQueue<byte[]>` 버퍼 풀(`Rent()` / `Enqueue()`).
- **정책이 `FfmpegRecorder`와 정반대**: 큐가 가득 차면 `Add`가 **블로킹**한다
  = 프레임을 버리지 않고 취득 스레드에 백프레셔를 건다(무손실이 목적이므로).
  대신 보드 쪽에서 프레임이 밀릴 수 있고, 그것을 `M_PROCESS_FRAME_MISSED`로 계측해 노출한다.
- 라이터가 예외로 죽으면 `Failed`(volatile) 세팅 후 **`CompleteAdding()`으로 생산자를 깨워**
  훅이 영원히 블록되는 것을 막는다. 이후 `Enqueue`는 `InvalidOperationException`을 삼킨다.
  (`Failed`를 volatile로 쓰는 것이 `LastError` 문자열을 다른 스레드에 publish 하는 장치)
- `FileStream`: 1MB 버퍼, `FileOptions.SequentialScan`. `CompleteAndWait`에서 `Flush(true)`.

### 2.11 `Infrastructure/RawSegmentSession.cs` (221줄)

연속 무손실 녹화의 오케스트레이터.

**스레드 모델(주석에 명시된 계약)**

```
훅 스레드(단일 생산자)  : "현재" writer 를 배타 소유 — Rent / Feed / Roll (락 불필요)
변환 스레드(단일 소비자): 큐에서 꺼낸 "완료된" writer 를 배타 소유
→ 두 스레드가 같은 writer 를 만지지 않으므로 핸드오프가 race-free
```

**세그먼트 롤오버**
- `Rent()` 호출 시 `CurrentSegmentSeconds >= _segmentSeconds`면 `Roll()`.
- `Roll()`: 새 세그먼트를 **먼저 열고**(실패 시 `Failed` + `_cur = null`), 이전 writer를 변환 큐에 넣는다.
- 파일명: 스크래치 `{base}_{yyyyMMdd_HHmmss}_p{NNN}.raw` → 결과 `{base}_{세그먼트시작시각}.mp4`.
- 변환: `ffmpeg -f rawvideo -pixel_format bayer_rggb8 -video_size WxH -framerate {실측fps}
  -c:v libx264 -preset ultrafast -pix_fmt yuv420p -movflags +faststart`
  → **fps는 `frames / 실제경과초`로 역산**(공칭 fps가 아니라 실측이라 재생 속도가 맞는다).
- 성공하면 `.raw` 삭제, **실패하면 `.raw`를 남긴다**(수동 복구용). 이 비대칭이 의도적이다.
- `Finish()`는 **그랩이 멈춘 뒤에만** 호출해야 한다(마지막 세그먼트를 큐에 넣고 `CompleteAdding`).
- 스크래치 폴더가 출력 폴더와 분리된 이유(주석): RAW는 너무 빨라 네트워크 스토리지에 못 쓴다 →
  로컬 NVMe에 쓰고, 압축된 MP4만 (NAS일 수 있는) 출력 폴더로 보낸다.

### 2.12 `Infrastructure/OutputSettings.cs` (193줄)

- 저장 위치: `%LocalAppData%\MatroxFrameGrabber\settings.json`
- 항목: `OutputFolder`(기본 `내 비디오\MatroxCapture`), `Resolution`(Original / P1080 / P720),
  `FfmpegPath`, `RawDurationSeconds`(0 = 수동), `RawSegmentSeconds`(최소 5),
  `RawScratchFolder`(기본 `%LocalAppData%\MatroxFrameGrabber\rawscratch`)
- **모든 setter가 값 변경 시 즉시 `Save()`** — 별도 저장 버튼이 없다.
- 방어 장치 두 개가 핵심이다:
  1. **`_loading` 플래그**로 `Load()`가 값을 적용하는 동안 재저장을 억제
  2. (de)serialization이 **관찰 가능한 setter를 절대 거치지 않도록 별도 `Dto` 클래스**를 사용
- `Load()` / `Save()` 모두 예외를 삼킨다("설정이 이번엔 저장 안 될 뿐" = 비치명적).
- `ScaleFactorFor(h)`: 업스케일 금지, 원본이 목표보다 작으면 1.0. `TargetHeight`는 `[JsonIgnore]`.

### 2.13 `Infrastructure/NativeMethods.cs` (36줄) / `RelayCommand.cs` (38줄)

- `UseImmersiveDarkTitleBar`: `DwmSetWindowAttribute` 속성 **20**(Win10 2004+), 실패하면 **19**
  (1809/1903)로 재시도, 그것도 실패하면 조용히 포기. `Window_SourceInitialized`에서 호출
  (HWND가 존재해야 하므로 `Loaded`가 아니라 `SourceInitialized`).
- `RelayCommand`: 최소 구현. `CanExecuteChanged`는 `CommandManager`가 아니라
  **수동 `RaiseCanExecuteChanged()`** 방식이다 (→ §6-2).

---

## 3. 의존성

**NuGet (2개뿐)**

| 패키지 | 해석된 버전 | 출처 |
|---|---|---|
| `Matrox.MatroxImagingLibrary` | 10.70.963 | 로컬 소스 `C:\Program Files\Matrox Imaging\MIL\MIL.NET\NuGet` |
| `Matrox.MatroxImagingLibrary.WPF` | 10.70.963 | 동일 |

`nuget.config`는 nuget.org도 남겨둔다(전이 의존성용, 주석에 `System.Drawing.Common` 예시).

**NuGet에 없는 외부 의존성이 하나 더 있다: `ffmpeg.exe`** — 런타임에 탐색하며,
없으면 `CanRecord = false`로 Rec / RAW 버튼이 모두 비활성화된다. 즉 **녹화 기능 전체가
ffmpeg에 걸려 있고, MIL 압축 라이선스는 쓰지 않는다.**

**프레임워크 / 빌드**
- `net6.0-windows`, `UseWPF=true`, **`UseWindowsForms=true`**(오직 `FolderBrowserDialog` 때문 — csproj 주석에 명시)
- `Platforms=x64`, `PlatformTarget=x64`(MIL NuGet이 x64/arm64만 지원)
- `Nullable=disable`, `ImplicitUsings=disable` → **모든 파일이 `using`을 명시**하고 `?` 어노테이션이 없다
- 산출물: `src\bin\x64\Release\net6.0-windows\MatroxFrameGrabber.exe`
  (`Platforms=x64` 때문에 `bin\x64\` 세그먼트가 낀다)
- 빌드: `dotnet build MatroxFrameGrabber.slnx -c Release` (`.slnx` 신형 솔루션 포맷)
- `.gitattributes`: `* text=auto`, `.sln/.slnx/.csproj`는 CRLF 고정

**런타임 전제**
- Windows x64 + MIL 10.70 설치 + WindowsDesktop 6.0 런타임
- Rapixo CXP 보드(없으면 `M_SYSTEM_DEFAULT` 폴백 → "No camera" 패널 4개)
- 비페이지드 메모리 풀이 부족하면 MILConfig에서 증설 필요

---

## 4. 코딩 컨벤션 (코드에서 귀납한 규칙)

1. **XML 문서 주석(`/// <summary>`)을 public 멤버에 거의 빠짐없이** 단다. 한 줄 요약 스타일.
2. **"왜"를 설명하는 주석**이 많다. 특히 MIL의 비직관적 동작(모달 대화상자, 영속 설정, pitch 패딩,
   `MbufGetColor` 이상 동작, MIL의 키 메시지 가로채기)은 **반드시 근거 주석과 함께** 코드에 남긴다.
   이 저장소의 가장 강한 관습이며, 커밋 메시지도 같은 스타일이다.
3. `#region`으로 큰 클래스를 구획(`Constants` / `Private members` / `Allocation` / `Acquisition` /
   `Recording` / `Output helpers` / `Feature helpers` / `INotifyPropertyChanged`).
4. **MIL 호출은 반드시 `try/catch(MILException)`** 또는 존재 확인 뒤에. 실패는 `false` 반환 또는 무시.
5. **정리 코드는 `try { } catch { }`로 감싸고 절대 던지지 않는다**(`FreeBuffers`, `Dispose`, `Stop`, `TryDelete`).
6. `MappControl(M_ERROR, M_PRINT_DISABLE)`는 **항상 `finally`로 `M_PRINT_ENABLE` 복구**.
7. 숫자 파싱·포매팅은 `CultureInfo.InvariantCulture`.
8. 필드는 `_camelCase`, 로컬 상수는 `UPPER_SNAKE`, GenICam 이름 상수는 `F_FEATURE_NAME`.
9. **명령 바인딩 vs Click 핸들러**: 대화상자가 필요 없으면 `RelayCommand`, 필요하면 코드비하인드.
10. **UI 문자열은 영어/한국어 혼재**하되 규칙이 있다: 정보·상태·툴바 = 영어,
    **되돌릴 수 없는 동작의 확인 문구와 녹화 배너 = 한국어**(현장 오조작 방지 우선).
11. 스레드 소유권을 **주석으로 명시**한다(`// Owned by the hook thread only:`).
12. `volatile` / `Interlocked` / `Volatile.Read`를 크로스 스레드 플래그·카운터에 일관되게 사용.
13. 상태 변경은 거의 전부 `RefreshStats()`(500ms 폴링)에서 UI로 흘린다 — 이벤트 기반 push는
    실패 알림(`RecordingFailed` / `CameraLost` / `RawRecordingFinished`) 세 가지뿐.

---

## 5. 예외 케이스 총정리

| # | 상황 | 처리 |
|---|---|---|
| 1 | MIL 시스템 할당 실패 | `M_SYSTEM_RAPIXOCXP` → `M_SYSTEM_DEFAULT` 폴백, 둘 다 실패 시 `InvalidOperationException` → `Window_Loaded`에서 MessageBox |
| 2 | 빈 CXP 포트에 `MdigAlloc` | `M_PRINT_DISABLE` + try/catch, `M_CAMERA_PRESENT` 재확인 후 "No camera" 패널 |
| 3 | DisplayId=0으로 `MILWPFDisplay` 생성 | 생성자 순서 강제 + `DataContextChanged`에서 지연 생성 |
| 4 | 그랩 버퍼(DMA) 부족 | 개별 try/catch, 얻은 만큼 사용, 2개 미만이면 상태 텍스트 경고 |
| 5 | 카메라가 특정 GenICam 피처 미지원 | `Available()` 확인 → `Supports*` 바인딩으로 컨트롤 비활성화 |
| 6 | 피처 값 범위 초과 쓰기 | `false` 반환 → "value out of range or feature unavailable" MessageBox |
| 7 | 카메라 케이블 분리 | `M_CAMERA_PRESENT` 2연속 실패 → **녹화만** 중지 + 경고, StopGrab은 사용자 몫 |
| 8 | ffmpeg 미설치 | `CanRecord=false` → Rec/RAW 버튼 비활성화 + 시작 시 사유 메시지 |
| 9 | ffmpeg 프로세스 사망 | `Exited` → `Failed` 이벤트 → `RefreshStats`가 정리 + `RecordingFailed` MessageBox |
| 10 | 인코더가 못 따라감(라이브) | `HasRoom` false면 추출 스킵, 큐 full이면 드롭 + `DroppedFrames` 표시 |
| 11 | 디스크가 못 따라감(RAW) | `BlockingCollection.Add` 블로킹(백프레셔) → 보드 드롭은 `M_PROCESS_FRAME_MISSED` → 배너 `⚠ dropped N` |
| 12 | RAW 쓰기 스레드 사망 | `Failed` + `CompleteAdding()`으로 생산자 해제 → `RefreshStats`가 `StopRawRecording` |
| 13 | 세그먼트 변환 실패 | `.raw`를 **삭제하지 않고 보존**, `Failed`/`LastError` → 완료 폴링 시 MessageBox |
| 14 | RAW 시작 도중 예외 | 세션 정리 + `RestoreColorAfterRaw()`로 **보드 Bayer 변환 강제 복구** |
| 15 | mono 카메라에서 RAW 시도 | `band != 1` 검사 → "raw Bayer 미지원" + 컬러 복구 |
| 16 | Rec ↔ RAW 동시 시도 | 서로 상대 모드를 검사해 거부 |
| 17 | 앱 종료 중 변환/finalize 미완 | `FreeCamera`에서 각각 최대 15초 대기, 미완 `.raw`는 스크래치에 남김 |
| 18 | 설정 파일 손상 | `Load()`가 예외를 삼키고 기본값 유지 |
| 19 | 전체화면에서 ESC를 MIL이 삼킴 | `ComponentDispatcher.ThreadFilterMessage` 후크 |
| 20 | 8비트 초과 픽셀 깊이 | 디스플레이 `M_BIT_SHIFT`, 녹화는 `MimShift(-shift)`로 8비트 축소 |
| 21 | 녹화 중 Stop 클릭 | 한국어 확인 대화상자(패널 단위 · 전체 단위 모두) |
| 22 | 두 카메라가 같은 `OutputName` | `SafeName()`이 `_ch{index}` 접미사를 항상 붙임 |
| 23 | 홀수 해상도 + H.264 | `w &= ~1; h &= ~1` + ffmpeg `crop=trunc(iw/2)*2:...` 이중 방어 |
| 24 | 구형 Windows(dwmapi 없음) | `UseImmersiveDarkTitleBar`가 조용히 실패, 기본 타이틀바 유지 |

---

## 6. 관찰된 리스크 / 개선 여지

읽으면서 확인된 것들(현 시점 코드 기준, 영향도 순):

1. **`StartGrab()`의 `MILException` 재던지기가 UI로 전파된다.**
   `CameraChannel.StartGrab`은 핸들을 정리한 뒤 `throw`한다. 이것이 `StartCommand`(`RelayCommand`)나
   `MilApplicationManager.StartAll()`을 통해 호출되면 **처리되지 않은 예외 → 앱 크래시**가 된다.
   `App.xaml.cs`에 `DispatcherUnhandledException` 핸들러가 없어 완충 장치도 없다.
   (`RestoreColorAfterRaw`는 유일하게 `try { StartGrab(); } catch (MILException) { }`로 감싼다.)

2. **`RelayCommand.RaiseCanExecuteChanged()`를 호출하는 곳이 전혀 없다.**
   `StartCommand` 등의 `canExecute`는 `CameraPresent`인데, `ReloadWithDcf`로 카메라 유무가 바뀌어도
   버튼 활성화 상태가 갱신되지 않는다. `CommandManager.RequerySuggested`를 쓰지 않는 구현이라
   자동 재조회도 없다.

3. **`MainViewModel.AnyCanRecord`의 주석이 낡았다** — "MIL compression licensed"라고 적혀 있지만
   실제 판정 기준은 ffmpeg 존재 여부다.

4. **`CameraChannel.DumpDiagnostics()`는 죽은 코드**다. 어디서도 호출하지 않는다.
   fps 문제 진단에 유용한 내용이므로 UI(예: 패널 툴팁/로그)에 노출하든지 제거하든지 결정이 필요하다.

5. **`_rawFinishing` 세션이 종료 경로에서 `Dispose`되지 않을 수 있다.**
   `FreeCamera()`는 `WaitConversions(15000)`만 하고 `Dispose()`는 하지 않는다.
   정상 동작 중에는 `RefreshStats`가 처리하지만, 종료 시엔 `Shutdown()`으로 타이머가 이미 멈춘 뒤다.

6. **`RecordingSession.Feed()`가 `_lock`을 잡은 채 MIL 추출(3회 `MbufGet` + 복사)을 수행**한다.
   같은 락을 UI 스레드의 `Stop()`이 기다리므로, 대형 프레임에서 정지 클릭이 순간적으로 블록될 수 있다.

7. **`bayer_rggb8` 하드코딩** — 카메라의 실제 Bayer 패턴(GRBG/BGGR/GBRG)을 조회하지 않는다.
   패턴이 다른 카메라에서는 RAW 산출물의 색이 뒤바뀐다. (컬러 Rec 경로는 보드가 변환하므로 무관)

8. **`RawSegmentSession.Roll()`의 `_convertQueue.Add`가 이론상 던질 수 있다.**
   `Finish()`가 `CompleteAdding()`을 부른 뒤 훅이 한 번 더 도는 경우인데, 현재 호출 순서
   (`StopGrab` → `Finish`)에서는 발생하지 않는다. 다만 방어 코드가 없다.

9. **`OutputSettings.Save()`가 setter마다 동기 파일 쓰기**를 한다. 관련 TextBox가
   `UpdateSourceTrigger=PropertyChanged`라서 **타이핑 한 글자마다 JSON을 다시 쓴다.**

10. **자동 테스트가 전혀 없다.** 하드웨어 의존이 크지만 `OutputSettings`(로드/저장/`ScaleFactorFor`),
    `SafeName()`, `FfmpegRecorder.ResolveFfmpegPath` 정도는 순수 로직이라 단위 테스트가 가능하다.

11. `docs/`에는 스크린샷 1장만 있고 설계 문서가 없다. 실질적 문서는 `CLAUDE.md` + 코드 주석 + 커밋 메시지.

---

## 7. 손대기 전에 알아야 할 것 (실무 체크리스트)

- **`M_BAYER_CONVERSION`을 끄는 코드를 추가한다면 반드시 복구 경로를 함께 만들어라.** 보드에 남는다.
- **`MappControl(M_ERROR, M_PRINT_DISABLE)`은 항상 `finally`로 되돌려라.** 안 그러면 이후 모든
  MIL 오류가 조용히 사라진다.
- **`MbufGet`은 pitch 패딩을 포함한다.** packed 데이터가 필요하면 `MbufGet2d`.
- **컬러 바이트를 뽑을 땐 `MbufGetColor`를 쓰지 마라.** planar 버퍼 + `MbufChildColor` 밴드별 `MbufGet`.
  자식 버퍼는 부모보다 먼저 해제.
- **`MILWPFDisplay`는 유효한 DisplayId 없이 생성하지 마라.** 네이티브 모달이 뜬다.
- 그랩 버퍼를 늘리려면 MILConfig의 **non-paged 풀** 크기를 함께 확인하라.
- 훅 델리게이트와 `GCHandle`은 그랩이 도는 동안 반드시 살아 있어야 한다.
- 새 상태를 UI에 띄우려면 `RefreshStats()`에 `RaisePropertyChanged`를 추가하는 것이 이 코드베이스의 방식이다
  (500ms 폴링 하나가 유일한 갱신 축).
- 취득 훅(`OnGrabbedFrame`)에서 하는 일은 **전부 실시간 예산 안에 들어와야 한다.** 무거운 처리를 넣으면
  라이브 뷰가 아니라 **그랩 자체가 밀린다**(RAW 경로는 특히 블로킹 백프레셔라 즉시 드롭으로 나타난다).

---

*본 리포트는 `src/` 전체를 라인 단위로 읽고 작성했다. 커밋 이력의 수정 사유
(`f2fde52` shearing, `4db0b71` 밴드 추출, `568839f` Bayer 강제 ON, `e115bfc` 분리 감지)도
대조해 근거로 삼았다.*
