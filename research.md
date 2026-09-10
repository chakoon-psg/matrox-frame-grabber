# `src/` 심층 분석 리포트 — Matrox Rapixo CXP 멀티 카메라 뷰어

> 대상: `C:\projects\matrox-frame-grabber\src` (소스 28개 파일, 8,593 LOC)
> 최초 작성: 2026-08-18, 커밋 `07e9cbc`
> 갱신: 2026-09-01. §2 에 `Infrastructure` 10개 파일과 테스트 프로젝트를 추가하고,
> View 절과 파일 크기를 현재 상태로 맞췄다.

---

## 0. 한 줄 요약

Matrox **Rapixo CXP**(CoaXPress) 프레임그래버 1장에 물린 **최대 4대**의 카메라를 **MIL 10.70** .NET
바인딩으로 라이브 그랩·표시하고, GenICam 피처로 제어하며, 스냅샷과 H.264 라이브 녹화를
산출하는 단일 프로세스
**WPF(net10.0-windows, x64)** 데스크톱 앱.

용도가 정해진 뒤로 **AVN 화면 이상 검지** 쪽 코드가 붙었다. 분석 ROI, 밝기 측정, PWM 노출 스윕,
타일 검지기의 순수 로직이 여기 속하며, 대부분 `Infrastructure`에 MIL 무의존으로 있다.
검지의 MIL 접착부와 분석 스레드는 **아직 없다.**

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
                 │                                      ├─ 그랩링(M_GRAB) × 4   │
                 │                                      ├─ GenICamFeatures     │
                 │                                      ├─ TileReducer         │
                 │                                      └─ RecordingSession    │
                 └─ CameraPaneView × 4 ── MILWPFDisplay(DisplayId) ────────────┘
```

**계층 규칙(컨벤션)**

| 레이어 | 네임스페이스 | 책임 | MIL 의존 |
|---|---|---|---|
| `Views/` | `MatroxFrameGrabber.Views` | XAML + 코드비하인드. 대화상자·파일 선택·MessageBox | `MILWPFDisplay`만 |
| `ViewModels/` | `MatroxFrameGrabber.ViewModels` | 채널 목록 노출, 전역 명령, 통계 타이머 | 없음 |
| `Mil/` | `MatroxFrameGrabber.Mil` | MIL 리소스 수명주기 · 그랩 루프 · GenICam | 전면 의존 |
| `Infrastructure/` | `MatroxFrameGrabber.Infrastructure` | ffmpeg, 파일 I/O, 설정, Win32 interop, **판정 순수 로직** | **MIL 무의존** |

> `Infrastructure`의 MIL 무의존은 컨벤션에서 **계약**으로 바뀌었다. 테스트 프로젝트가 이 파일들을
> `ProjectReference`가 아니라 소스로 포함하므로(→ §2.17), 여기에 MIL 참조가 들어가면 **보드 없는
> 머신에서 테스트가 돌지 않는다.** ROI 규칙, 밝기 표본 배치, PWM 계산, 타일 검지기가 전부
> 이 계층에 있는 이유다.

> 눈여겨볼 점: `CameraChannel`이 **모델이자 뷰모델**이다. `INotifyPropertyChanged`를 직접 구현하고
> `RelayCommand`를 노출하며, XAML이 `Channels[0..3]`을 패널의 `DataContext`로 바로 바인딩한다.
> 채널별 ViewModel 래퍼가 없는 "얇은 MVVM"이며, 이것이 2,096줄짜리 `CameraChannel`의 이유다.

---

## 2. 파일별 역할 (정독 결과)

### 2.1 진입점 / 셸

**`App.xaml(.cs)`** (12 + 217줄)
- `StartupUri="Views/MainWindow.xaml"`, `Views/Styles.xaml`을 머지 딕셔너리로 로드.
- **명령행 인자 처리**가 여기 있다. 측정과 진단을 사람 없이 돌리기 위한 것이다.

| 인자 | 동작 |
|---|---|
| `--autostart <초>` | 지정 시간 취득 후 스스로 종료 |
| `--channels <목록>` | 지정한 채널만 디지타이저를 잡는다 |
| `--decim <값>` | 시작 시 디시메이션 적용 |
| `--pwm-sweep <채널>` `--pwm-scan` `--pwm-room` | PWM 노출 스윕 |
| `--bayer-scope` | `M_BAYER_CONVERSION`의 적용 범위 진단 |

- `Unattended`가 참이면 모달 대화상자를 띄우지 않고 로그로 보낸다. 무인 실행에서 대화상자는
  호출자가 시간 초과로 포기할 때까지 프로세스를 붙든다.
- **디스패처 예외 백스톱**이 생겼다(→ §6-1 해소). UI 스레드에서 던진 MIL 예외가 녹화 중인
  프로세스를 끝내지 않게 한다. UI 스레드 밖의 예외는 그대로 치명적으로 둔다.
- `--channels`가 있으면 로그 파일에 접미사를 붙인다. 로그의 락이 프로세스 내부라
  여러 인스턴스가 한 파일에 쓰면 줄이 유실된다.

**`Views/Styles.xaml`** (370줄)
- 다크 테마 팔레트(`#1E1E1E` 배경 / `#2A2A2A` 패널 / `#0E639C` 액센트)와
  `Button` / `TextBox` / `ComboBox` / `CheckBox` / `Label` / `Expander` 암시적 스타일.
- **`RecToggle`**은 체크되면 빨강 `#C62828`이 된다. 같은 색이 패널 배너(`RecBrush`)에도 쓰여
  "지금 녹화 중"이 한 가지 색으로 읽힌다. 무손실 RAW 녹화를 주황으로 구분하던 `RawToggle` /
  `RawBrush`는 그 기능과 함께 제거했다.
- `ComboBox`는 템플릿 재정의 없이 프로퍼티 레벨만 다크 처리(주석에 명시) — 드롭다운 팝업 일부에
  시스템 기본 스타일이 남는 것을 감수한 타협.

### 2.2 `Views/MainWindow.xaml(.cs)` (217 + 912줄)

**툴바 구성** (커밋 `121d605` → `07e9cbc`에서 단순화)
- 항상 필요한 것만 노출: `Start All` / `Stop All` │ `● Rec All` │ `⚙ Settings` │ `Open`
- 설정은 `AppSettingsWindow`(앱 전역)와 `CameraSettingsWindow`(카메라별) 두 창에 모임.
  팝업이던 `⚙ Rec`을 대체한다 — 그 팝업은 "recording settings"라는 이름으로 세 줄을 들고
  있었는데 녹화인 것은 해상도 하나였고(ffmpeg 경로는 읽기 전용 진단, 표시 fps는 프리뷰),
  출력 폴더는 툴바에 있었고 녹화 백엔드는 둘 자리가 없었다. `Browse…`도 창 안으로 옮겼다:
  폴더는 한 번 정하는 값이고, 폴더가 둘 이상이 되면 툴바 버튼으로는 어느 쪽인지 말할 수 없다.
- 오른쪽 끝에 `SystemStatus`(할당된 시스템 디스크립터 + 디지타이저 수) 고정.

**생명주기 (중요한 순서 규약)**

```csharp
public MainWindow() {
    _manager = new MilApplicationManager(); _manager.Allocate();  // ① 먼저 MIL 할당
    ... RecordingFailed / CameraLost / AnomalyDetected 구독 ...
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
- 패널의 디스플레이 컨트롤을 **옮긴다**(`DetachDisplay` → `FullscreenContentGrid`,
  복귀 시 `ReattachDisplay`). 새로 만들지 않는다.
- 더블클릭 또는 `⤢` 버튼으로 진입, ESC 또는 닫기 버튼으로 복귀.
- 전체화면에도 ROI 편집 표면(`_fullscreenRoi`)이 붙는다.

> **개정 (2026-08-27).** 이전 구현은 `MILWPFDisplay`를 그때그때 `new` 해서 오버레이에 꽂았다.
> 그 결과 같은 DisplayId에 컨트롤이 둘 생겼고, **MIL의 줌 상태는 디스플레이당 하나뿐이라**
> "창에 맞춤"이 어느 컨트롤 기준인지 모호해졌다. 증상은 전체화면에서 ROI 사각형이 사라지고
> 복귀 후에도 안 보이는 것으로 나타났다(패널이 zoom 0.30이 아니라 1.011로 돌아왔다).
> 컨트롤을 옮기는 방식으로 바꿔 해결했고, 복귀 시 zoom 0.438로 확인했다.
>
> 부수 효과로 오버레이의 시각 요소가 남는 문제가 생겨 `RemoveVisuals()`를 추가했다.
> `Children.Clear()`는 디스플레이까지 지우므로 쓸 수 없다.

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


### 2.3 `Views/CameraPaneView.xaml(.cs)` (248 + 355줄)

한 카메라 패널. `DataContext`는 `CameraChannel`.

- **헤더**: `[CAM0]` 고정 포트 태그(현장에서 물리 포트를 짚기 위한 것) + 편집 가능한 `OutputName`(파랑) +
  `▶Start / ■Stop / ●Rec / Snap / Fit / 1:1 / ⤢`.
- **`Expander`(기본 접힘)** 안에 세부 설정: "이 설정을 전체 카메라에 적용" 버튼, Name, Exposure,
  Acq Rate, Trigger, White Balance, DCF/Features. 라이브 뷰 면적을 최대로 두려는 의도.
- **녹화 배너**: `RecordingActive`가 true면 뷰 상단에 상시 표시. 배경은 `RecBrush` 하나이며(
  배경이 주황으로 바뀐다. 텍스트는 `RecordingBannerText`(한국어, 모드+세그먼트+경과시간+드롭 수).

**코드비하인드가 하는 일 = "뷰 컨텍스트가 필요한 것만"**
- `Start / Fit / 1:1`은 XAML에서 `Command` 바인딩(`StartCommand` 등) — 대화상자 불필요.
- `Stop / Rec / Snap / Apply* / LoadDcf / Features`는 `Click` 핸들러 — MessageBox나
  파일 대화상자가 필요하기 때문. 이 분리가 이 프로젝트의 명확한 컨벤션이다(커밋 `d870884`).
- `_display` 생성은 `DataContextChanged`에서 **한 번만**, 그리고 `DisplayId != M_NULL`일 때만.
- `Stop_Click`은 녹화 중이면 한국어로 확인한 뒤 `StopGrab`(녹화도 함께 끝난다).

### 2.4 `ViewModels/MainViewModel.cs` (329줄)

- `DispatcherTimer` 500ms → 모든 채널 `RefreshStats()` + `AnyRecording` 알림.
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
  실패는 `"{channel.Name}: {err}"`로 합쳐 반환 → MainWindow가 MessageBox.
- `Shutdown()`은 타이머만 정지(MIL 해제는 MainWindow 담당).

### 2.5 `Mil/MilApplicationManager.cs` (155줄)

- `MappAlloc` → `MappControl(M_ERROR, M_THROW_EXCEPTION)` → `MsysAlloc`.
- 시스템 디스크립터 **폴백 체인**: `M_SYSTEM_RAPIXOCXP` → `M_SYSTEM_DEFAULT`.
  하드웨어가 없어도 앱이 뜨고 "No camera" 패널을 보여주기 위함.
- `M_DIGITIZER_NUM`을 조회해 `i < DigitizerCount`인 채널만 카메라 후보로 표시.
- `OutputSettings.Load()`를 한 번 해서 4개 채널이 **공유**한다.
- `Free()`는 **역순 해제**: 채널 → `MsysFree` → `MappFree`.

### 2.6 `Mil/GenICamFeatures.cs` (250줄)

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

### 2.7 `Mil/CameraChannel.cs` (2,096줄) — 앱의 심장

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
2. **Bayer 변환의 영속성** — `M_BAYER_CONVERSION`은 **보드에 남는 설정**이다. 한 번 꺼진 채로
   앱이 죽으면 다음 실행에서도 꺼진 채라, 컬러 파이프라인이 mono/raw 데이터를 타일/깨진 이미지로
   오해한다. 그래서 **`M_SIZE_BAND`를 조회하기 전에** 매번 다시 `M_ENABLE` (커밋 `568839f`).
   이걸 끄던 RAW 녹화는 제거했지만 그 복원은 남는다 — 옛 빌드가 꺼 놓은 보드가 있을 수 있고,
   이제 여기가 유일한 복원 지점이다.
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

**훅 본체 `OnGrabbedFrame(grabbed, display, frame, stamp)`**

```
RunDetection(grabbed, frame, stamp)      ★ 리듀서가 검출 영역만 읽어 8×8로 축약
MbufCopy(grabbed → display)              (DisplayUpdateFps로 스로틀)
_recording?.Feed(grabbed)
```

★ 리듀서는 `MbufGet2d`를 쓴다. `MbufGet`은 행 패딩(pitch 2112 > width 2064)을 포함해 복사하므로,
나중에 2064바이트 행으로 되읽으면 **영상이 사선으로 밀린다(shearing)**. `MbufGet2d`는 논리적
W×H 영역을 **packed**로 복사하고, X 오프셋을 받는 유일한 형태이기도 하다.

#### `RefreshStats()` — UI 타이머가 500ms마다 호출하는 폴링 허브

1. `M_PROCESS_FRAME_RATE` → `_frameRate`
2. `M_PROCESS_FRAME_MISSED` → `_framesMissed`(이번 실행분만. 누적값에서 시작 시점을 뺀다)
3. **카메라 분리 감지**: `M_CAMERA_PRESENT`가 **2회 연속** 실패해야 `_cameraLost`(일시 블립 방지).
   감지 시 **녹화만 중지하고 `StopGrab`은 사용자에게 맡긴다** — 죽은 포트에 `M_STOP`을 거는 것이
   위험하다는 판단(주석 명시). 다시 붙으면 `_cameraLost` 해제.
4. ffmpeg가 죽었으면(`_recording.Failed && IsActive`) 정리 후 `RecordingFailed` 이벤트
5. 확정된 이상을 큐에서 꺼내 로그·CSV·`AnomalyDetected`로 내보낸다
6. 마지막에 `FrameRate / FrameCount / StatusText / RecordingActive / RecordingBannerText` 알림

#### 녹화

`RecordingSession` 하나뿐이다. 표시 버퍼(3-band 컬러)를 메모리로 받아 ffmpeg **stdin 파이프**로
보내고, 픽셀 포맷은 `gbrp`(1-band일 때 `gray`), x264 preset은 `veryfast`. 큐가 가득 차면
**프레임을 드롭**한다 — 라이브 뷰가 우선이라는 판단이며, 출력 폴더에 직접 쓴다.

무손실 RAW-Bayer 세그먼트 녹화가 있었고 제거했다(`RawSegmentSession` / `RawFrameWriter`,
`◆ RAW` / `◆ RAW All`, 자동정지·세그먼트 길이·스크래치 폴더 설정). 그 경로만이
`M_BAYER_CONVERSION`을 껐지만, **그 설정은 보드에 남으므로 복원은 그대로 필요하다** —
`AllocateCamera`가 매번 다시 켜는 것이 이제 유일한 복원 지점이고, 옛 빌드가 꺼 놓은 보드가
남아 있을 수 있으므로 지우면 안 된다.

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

- `Start(...)`가 소스 버퍼에서 **geometry/포맷을 추론**하고, 호출자가 준 배율(현재 항상 1.0)을
  적용한 뒤 폭·높이를 `&= ~1`로 짝수화(H.264 요구). **배율과 레이트는 설정이 아니라 취득에서
  나온다** — 계약에는 남아 있지만 앱은 언제나 원본 크기·매 프레임을 넘긴다.
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

### 2.9 `Infrastructure/FfmpegRecorder.cs` (259줄)

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

### 2.10 `Infrastructure/OutputSettings.cs` (311줄)

- 저장 위치: `%LocalAppData%\MatroxFrameGrabber\settings.json`
- 항목: `OutputFolder`(기본 `내 비디오\MatroxCapture`), `FfmpegPath`, `DisplayUpdateFps`,
  `SinkPreference`, `SegmentFolder`, `KeepStills`, `AnomalyClipSeconds`, 채널별 `ChannelRois` /
  `ChannelDecimation` / `ChannelThresholds`
- **모든 setter가 값 변경 시 즉시 `Save()`** — 별도 저장 버튼이 없다.
- 방어 장치 두 개가 핵심이다:
  1. **`_loading` 플래그**로 `Load()`가 값을 적용하는 동안 재저장을 억제
  2. (de)serialization이 **관찰 가능한 setter를 절대 거치지 않도록 별도 `Dto` 클래스**를 사용
- `Load()` / `Save()` 모두 예외를 삼킨다("설정이 이번엔 저장 안 될 뿐" = 비치명적).
- 녹화 레이트와 해상도 프리셋은 **여기 없다**. 둘 다 선택이 아니라 결과였다 — 상한이 취득
  레이트이고 크기가 취득 크기다. 설정 창은 그 사실을 읽기 전용 한 줄로 말한다.

### 2.11 `Infrastructure/NativeMethods.cs` (36줄) / `RelayCommand.cs` (38줄)

- `UseImmersiveDarkTitleBar`: `DwmSetWindowAttribute` 속성 **20**(Win10 2004+), 실패하면 **19**
  (1809/1903)로 재시도, 그것도 실패하면 조용히 포기. `Window_SourceInitialized`에서 호출
  (HWND가 존재해야 하므로 `Loaded`가 아니라 `SourceInitialized`).
- `RelayCommand`: 최소 구현. `CanExecuteChanged`는 `CommandManager`가 아니라
  **수동 `RaiseCanExecuteChanged()`** 방식이다 (→ §6-2).

### 2.12 분석 ROI 관련 (2026-08 추가)

카메라 크롭이 이 장비에서 동작하지 않는다는 것이 확인된 뒤(→ §8), **어느 화소를 판정에 쓸지**를
소프트웨어로 지정하는 경로가 생겼다. 규칙은 전부 `Infrastructure`에 순수 함수로 있다.

**`Infrastructure/ChannelRoi.cs`** (177줄)
- 불변 구조체. `Width` 또는 `Height`가 0 이하이면 **전체 프레임**을 뜻한다.
- `Snap(maxW, maxH)`: 짝수 격자 정렬 + 프레임 안으로 클램프. CFA 위상 보존이 목적이다.
- `Rescale(from, to)`: 디시메이션 변경 시 재축척. **하한이 없어 토글을 반복하면 사각형이
  절반씩 줄어드는 결함이 있었다**(504×308 → 46×26). `MinEditableSize`(16)로 막았다.
- 대역폭 상수(`HostDmaCeilingBytesPerSecond`, `WarnBytesPerSecond`)도 여기 있다.

**`Infrastructure/RoiGesture.cs`** (156줄)
- 드래그 규칙 전부. `HitTest` / `Resize` / `Move`.
- 패널과 전체화면 두 표면이 같은 사각형을 편집하므로 **규칙을 한 곳에 모은 것**이다.
  핸들러마다 복사하면 갈라진다.
- 반대쪽 변을 지나쳐 끌면 **뒤집는다.** 음수 크기를 `ChannelRoi`가 "전체 프레임"으로 읽으므로,
  뒤집지 않으면 측정 대상이 조용히 전체로 바뀐다.
- `Snap`을 마지막에 적용해 미리보기와 커밋 값을 일치시킨다. 아니면 버튼을 뗄 때 사각형이 튄다.

**`Infrastructure/DisplayMapping.cs`** (69줄)
- 이미지 좌표 ↔ 컨트롤 좌표 변환. MIL의 줌·팬 상태를 반영한다.

**`Views/RoiEditSurface.cs`** (455줄)
- 패널과 전체화면이 공유하는 편집 표면. 사각형·점선·핸들 8개를 코드로 만든다.
- `TryBeginDrag` / `ContinueDrag` / `EndDrag` / `CancelDrag` / `Refresh` / `UpdateCursor`.
- **오버레이는 500ms 통계 틱에 얹혀 위치를 따라간다.** MIL의 줌·팬이 네이티브라 이벤트가
  없으므로 폴링이 유일한 방법이다.
- 사각형이 그려지지 않을 때 그 이유를 로그에 남긴다. 전이만 로깅하던 초기 구현은
  **한 번도 그려지지 않은 표면에 대해 아무 말도 하지 않았다.**

### 2.13 밝기 측정 (2026-08 추가)

**`Mil/BrightnessMeter.cs`** (209줄)
- 표시 버퍼에서 strip을 읽어 평균 luma·포화율·흑화율을 낸다.
- **분석 ROI 안에서만 측정한다.** ROI가 없으면 전체 프레임이다.
- 전체 해상도에서 점 표본을 뜬다. 축소본을 재면 이중선형 보간이 포화 화소를 이웃과 평균해
  버려서 포화율이 존재하는 이유가 사라진다.
- `MbufGet2d`를 쓴다. 행 패딩이 있는 버퍼를 `MbufGet`으로 읽으면 어긋난다.
- 컬러는 Rec.601, 1밴드는 값 그대로. Bayer의 단순 평균이 `0.25R + 0.50G + 0.25B`라
  두 모드 사이에서 그래프가 이어진다.

**`Infrastructure/BrightnessSamplePlan.cs`** (107줄)
- strip 배치 계산. 16 strip × 4행을 영역 안에 펼친다.
- 짧은 영역에서는 strip 수를 줄여 **겹치지 않게** 한다. 겹치면 같은 행을 두 번 세어
  실제로 본 면적을 과대 보고한다.
- **그려진 ROI가 표본 불가일 때는 전체 프레임으로 되돌리지 않고 아무 값도 내지 않는다.**
  되돌리면 그래프는 움직이는데 대상이 조작자가 요청한 적 없는 것이 된다.

**`Infrastructure/BrightnessHistory.cs`** (79줄)
- 240개 링 버퍼. `BrightnessSample`(luma / clip% / black%).

### 2.14 PWM 노출 스윕 (2026-08 추가)

**`Infrastructure/PwmSweep.cs`** (771줄)
- 백라이트 PWM 주파수를 노출 스윕으로 찾는 계산 전부. MIL 무의존, 단위 테스트 대상.
- 노출은 박스 적분이라 주파수 응답이 `|sinc(pi*f*T)|`이고 `f*T`가 정수면 0이 된다.
  리플이 사라지는 노출이 주기의 정수배이며 거기서 주파수가 역산된다.
- **4점 모드**는 100 Hz 계열과 120 Hz 계열만 가린다. **곡선 스캔**(4000~11000 µs, 250 µs 간격)은
  널 위치에서 주파수를 직접 구한다.
- 널 위치를 **V자 꼭짓점으로 보간한다.** 격자 그대로 쓰면 240 Hz를 250 Hz로 읽고,
  그 값으로 계산한 노출은 진짜 널을 333 µs 빗나간다.
- 널이 하나뿐이어도 대개 답이 나온다. 큰 k는 이웃 널을 함의하고 그 위치가 스캔 범위 안이면
  스캔이 찾았을 것이므로, 그런 k는 배제된다.
- 권장 fps를 노출에서 유도한다. 노출이 허용하는 최대치보다 낮게 캡을 걸면 노출 사이에
  사각지대가 생긴다(5000 µs를 184 fps로 제한하면 8%).

### 2.15 타일 검지기 (2026-08 추가, 미완)

프레임 단위 이상 검지의 순수 로직. **MIL 접착부(`TileReducer`)와 분석 스레드는 아직 없다.**

**`Infrastructure/TileGrid.cs`** (139줄)
- 8×8 타일의 합·제곱합·화소수. mean / stdev / 중앙값 / 전역 평균이 파생된다.
- 대표값은 **타일 평균의 중앙값**이다. 지나가는 밝은 물체가 평균은 끌지만 중앙값은 못 끈다.
- 표준편차는 반올림으로 분산이 음수가 되면 0으로 clamp한다. NaN이 새면 백화 판정이 꺼진다.

**`Infrastructure/FrameMetrics.cs`** (72줄)
- `Depth(median, baseline)` = `1 − median/baseline`
- `Coherence(before, after)` = `|Σd| / Σ|d|`
- **정지 화면의 coh는 0이다.** 분모가 0인데 1을 돌려주면 센서 노이즈가 만든 depth와 짝지어져
  변화 없는 화면에서 검출이 난다.

**`Infrastructure/AnomalyDetector.cs`** (277줄)
- running median 기준선, 사건형 확정, 디바운스, 프레임 번호 불연속 구간 제외.
- **진입은 `depth AND coh`, 유지는 `depth`만.** 화면이 어두워지면 타일이 더 안 움직여 coh가
  0으로 떨어지므로, 매 프레임 요구하면 여러 프레임 blank가 쪼개진다.
- 지속 시간은 **프레임 수 × 주기**다. 타임스탬프 간격은 1프레임 사건을 0으로 보고한다.
- 어두운 프레임은 기준선에 넣지 않되 완만한 정상 변화는 넣는다. 전자가 없으면 긴 블랙아웃이
  스스로를 지우고, 후자가 없으면 패널을 어둡게 한 뒤 모든 프레임이 이상이 된다.
- `Flush()`가 없으면 grab 종료 시점에 열려 있던 사건이 보고되지 않는다.

### 2.16 `Infrastructure/MilErrorLog.cs` (112줄)

MIL 오류는 실패한 스레드 위에 **모달 대화상자**로 뜬다(→ §5). 출력을 끄고 여기로 보낸다.

- **프레임마다 부르면 안 된다.** 프로세스 전역 락 아래의 동기 디스크 쓰기라, 오류 폭풍이 나면
  모든 취득 스레드가 파일 I/O 뒤에 줄을 선다.
- 트림할 때 **최신 절반을 남긴다.** 초기 구현은 경계에서 전부 버렸다.
- `FileSuffix`로 파일을 나눌 수 있다. 락이 프로세스 내부라 여러 프로세스가 한 파일에 쓰면
  줄이 유실된다. 실제로 분리 프로세스 측정을 오염시킨 적이 있다.

---

### 2.17 테스트 프로젝트 (`tests/`, 153개)

`Infrastructure`가 MIL 무의존이라 **보드 없이 검증 가능한 유일한 계층**이다.

```
ChannelRoiTests          RoiGestureTests        DisplayMappingTests
BrightnessSamplePlanTests
PwmSweepTests            PwmScanTests
TileGridTests            FrameMetricsTests      AnomalyDetectorTests
```

`csproj`가 앱을 `ProjectReference`하지 않고 **소스로 포함**한다. 참조하면 x64 전용 MIL NuGet을
끌어와 MIL 없는 머신에서 돌지 않는다.

---

## 3. 의존성

**NuGet (2개뿐)**

| 패키지 | 해석된 버전 | 출처 |
|---|---|---|
| `Matrox.MatroxImagingLibrary` | 10.70.963 | 로컬 소스 `C:\Program Files\Matrox Imaging\MIL\MIL.NET\NuGet` |
| `Matrox.MatroxImagingLibrary.WPF` | 10.70.963 | 동일 |

`nuget.config`는 nuget.org도 남겨둔다(전이 의존성용, 주석에 `System.Drawing.Common` 예시).

**NuGet에 없는 외부 의존성이 하나 더 있다: `ffmpeg.exe`** — 런타임에 탐색하며,
없으면 `CanRecord = false`로 Rec 버튼이 비활성화된다. 즉 **녹화 기능 전체가
ffmpeg에 걸려 있고, MIL 압축 라이선스는 쓰지 않는다.**

**프레임워크 / 빌드**
- `net10.0-windows`, `UseWPF=true`, **`UseWindowsForms=true`**(오직 `FolderBrowserDialog` 때문 — csproj 주석에 명시)
- `Platforms=x64`, `PlatformTarget=x64`(MIL NuGet이 x64/arm64만 지원)
- `Nullable=disable`, `ImplicitUsings=disable` → **모든 파일이 `using`을 명시**하고 `?` 어노테이션이 없다
- 산출물: `src\bin\x64\Release\net10.0-windows\MatroxFrameGrabber.exe`
  (`Platforms=x64` 때문에 `bin\x64\` 세그먼트가 낀다)
- 빌드: `dotnet build MatroxFrameGrabber.slnx -c Release` (`.slnx` 신형 솔루션 포맷)
- `.gitattributes`: `* text=auto`, `.sln/.slnx/.csproj`는 CRLF 고정

**런타임 전제**
- Windows x64 + MIL 10.70 설치 + WindowsDesktop 10.0 런타임
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
8. **UI 문자열은 영어/한국어 혼재**하되 규칙이 있다: 정보·상태·툴바 = 영어,
    **되돌릴 수 없는 동작의 확인 문구와 녹화 배너 = 한국어**(현장 오조작 방지 우선).
9. 스레드 소유권을 **주석으로 명시**한다(`// Owned by the hook thread only:`).
10. `volatile` / `Interlocked` / `Volatile.Read`를 크로스 스레드 플래그·카운터에 일관되게 사용.
13. 상태 변경은 거의 전부 `RefreshStats()`(500ms 폴링)에서 UI로 흘린다 — 이벤트 기반 push는
    실패 알림(`RecordingFailed` / `CameraLost`)과 이상 검출(`AnomalyDetected`)뿐.

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
| 8 | ffmpeg 미설치 | `CanRecord=false` → Rec 버튼 비활성화 + 시작 시 사유 메시지 |
| 9 | ffmpeg 프로세스 사망 | `Exited` → `Failed` 이벤트 → `RefreshStats`가 정리 + `RecordingFailed` MessageBox |
| 10 | 인코더가 못 따라감(라이브) | `HasRoom` false면 추출 스킵, 큐 full이면 드롭 + `DroppedFrames` 표시 |
| 11 | 설정 파일 손상 | `Load()`가 예외를 삼키고 기본값 유지 |
| 12 | 전체화면에서 ESC를 MIL이 삼킴 | `ComponentDispatcher.ThreadFilterMessage` 후크 |
| 13 | 8비트 초과 픽셀 깊이 | 디스플레이 `M_BIT_SHIFT`, 녹화는 `MimShift(-shift)`로 8비트 축소 |
| 14 | 녹화 중 Stop 클릭 | 한국어 확인 대화상자(패널 단위 · 전체 단위 모두) |
| 15 | 두 카메라가 같은 `OutputName` | `SafeName()`이 `_ch{index}` 접미사를 항상 붙임 |
| 16 | 홀수 해상도 + H.264 | `w &= ~1; h &= ~1` + ffmpeg `crop=trunc(iw/2)*2:...` 이중 방어 |
| 17 | 구형 Windows(dwmapi 없음) | `UseImmersiveDarkTitleBar`가 조용히 실패, 기본 타이틀바 유지 |

---

## 6. 관찰된 리스크 / 개선 여지

읽으면서 확인된 것들(현 시점 코드 기준, 영향도 순):

1. ~~**`StartGrab()`의 `MILException` 재던지기가 UI로 전파된다.**~~ — **해결됨**
   `CameraChannel.StartGrab`은 핸들을 정리한 뒤 `throw`했고, 이것이 `StartCommand`(`RelayCommand`)나
   `MilApplicationManager.StartAll()`을 통해 호출되면 **처리되지 않은 예외 → 앱 크래시**가 됐다.
   비throw 래퍼 `TryStartGrab()`을 추가해 UI·일괄 호출 경로가 이를 쓰도록 바꾸고, 실패는
   `GrabFailed` 이벤트로 알린다. `StartGrab` 자체는 계속 던진다 — 실패 시 되돌릴 것이 있는
   호출자가 잡을 수 있어야 한다(당시에는 RAW 녹화가 보드를 컬러로 복구했다). `App.xaml.cs`에
   `DispatcherUnhandledException` 백스톱도 추가했다.

2. ~~**`RelayCommand.RaiseCanExecuteChanged()`를 호출하는 곳이 전혀 없다.**~~ — **해결됨**
   `StartCommand` 등의 `canExecute`는 `CameraPresent`인데, `ReloadWithDcf`로 카메라 유무가 바뀌어도
   버튼 활성화 상태가 갱신되지 않았다(`CommandManager.RequerySuggested`를 쓰지 않는 구현이라
   자동 재조회도 없음). `RaiseCommandStates()`를 추가해 `AllocateCamera` / `FreeCamera`에서 호출한다.

3. **`MainViewModel.AnyCanRecord`의 주석이 낡았다** — "MIL compression licensed"라고 적혀 있지만
   실제 판정 기준은 ffmpeg 존재 여부다.

4. ~~**`CameraChannel.DumpDiagnostics()`는 죽은 코드**다.~~ — **해결됨.**
   `AllocateCamera`에서 `MilErrorLog.Note(DumpDiagnostics())`로 호출한다. 앱을 시작할 때마다
   각 카메라의 노출·레이트·링크 구성이 로그에 남는다. "한 채널만 느리다"의 원인이 노출이었던
   사례가 있어(→ §8) 그 진단을 매번 남기도록 했다.

5. **`_rawFinishing` 세션이 종료 경로에서 `Dispose`되지 않을 수 있다.**
   `FreeCamera()`는 `WaitConversions(15000)`만 하고 `Dispose()`는 하지 않는다.
   정상 동작 중에는 `RefreshStats`가 처리하지만, 종료 시엔 `Shutdown()`으로 타이머가 이미 멈춘 뒤다.

6. **`RecordingSession.Feed()`가 `_lock`을 잡은 채 MIL 추출(3회 `MbufGet` + 복사)을 수행**한다.
   같은 락을 UI 스레드의 `Stop()`이 기다리므로, 대형 프레임에서 정지 클릭이 순간적으로 블록될 수 있다.

7. **`OutputSettings.Save()`가 setter마다 동기 파일 쓰기**를 한다. 관련 TextBox가
   `UpdateSourceTrigger=PropertyChanged`라서 **타이핑 한 글자마다 JSON을 다시 쓴다.**

8. ~~**자동 테스트가 전혀 없다.**~~ — **해결됨.** `tests/` 에 153개가 있다(→ §2.17).
    다만 대상은 `Infrastructure` 뿐이다(→ §2.17). 여기 적었던 `OutputSettings`, `SafeName()`,
    `FfmpegRecorder.ResolveFfmpegPath` 는 **아직 테스트가 없다.** 새로 추가된 순수 로직
    (ROI, 밝기 표본, PWM, 타일 검지기) 쪽으로 먼저 갔다.

9. ~~`docs/`에는 스크린샷 1장만 있고 설계 문서가 없다.~~ — **해결됨.**
    `CONTEXT.md`(용어), `docs/adr/`, `docs/superpowers/specs/`(이상 검지 설계),
    `docs/GUI 가이드.md`, `docs/measurements/` 가 생겼다.

10. **`research.md` 자신이 뒤처지기 쉽다.** 2026-08 갱신 시점에 View 절이 파일 크기 기준으로
    3배 이상 차이가 났고, `Infrastructure` 의 10개 파일이 문서에 없었다. 파일을 추가할 때
    §2 에 항목을 함께 넣는 습관이 필요하다.

> **해결된 1·2·7번에 대한 검증 범위**: 컴파일(Release x64, 오류 0)까지만 확인했고
> **카메라가 붙은 보드에서 실행 검증은 하지 못했다.** 특히 7번의 MIL→ffmpeg 패턴 매핑
> (`M_BAYER_GR` → `bayer_grbg8` 등)은 "MIL은 첫 줄 첫 두 픽셀로 패턴을 명명한다"는 전제에
>기대고 있으므로, 실제 컬러 카메라로 RAW 녹화를 돌려 색이 맞는지 확인이 필요하다.
> 현재 장비가 RGGB라면 폴백 값과 같아 회귀는 발생하지 않는다.

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

## 8. 측정된 하드웨어 한계 (2026-08-20, 실기)

Rapixo CXP + 카메라 3대에서 계측해 얻은 값이다. 코드만 읽어서는 알 수 없고, 성능 설계의 모든
판단이 여기 걸린다. 설계 문서:
[docs/superpowers/specs/2026-08-20-avn-anomaly-detection-design.md](docs/superpowers/specs/2026-08-20-avn-anomaly-detection-design.md).

**장비 구성**

| 항목 | 값 |
|---|---|
| 센서 / 픽셀 포맷 | 2064×1544, `BayerRG8` |
| CXP 링크 | `CxpLinkConfiguration = CXP6_X1` ≈ **625 MB/s / 카메라** |
| 원본 184 fps의 링크 사용량 | 559 MB/s = 링크의 **89%** |
| 카메라 최대 프레임레이트 | 184.06 fps (= `AcquisitionFrameRate` 최대) |
| 사용 가능한 감축 수단 | `Width`/`Height`/`OffsetX`/`OffsetY`, `DecimationHorizontal`/`Vertical`. `Binning*`은 없음 |

**184 fps는 이미 카메라 링크의 한계다.** 그 이상은 CXP 레인 증설이 필요하다.

**호스트 DMA 천장 ≈ 1.7 GB/s (채널 공유) — x4 시절의 값. 아래 2026-08-27 갱신을 볼 것**

채널 수와 무관하게 합계가 여기서 고정된다. 1채널 단독이면 184 fps · 유실 0인데, 채널을 추가하면
합계는 그대로이고 배분만 불공정해진다(보드 arbitration이 ch0에 몰아준다).

| 구성 | ch0 | ch1 | ch2 | 합계 | GB/s | 유실 |
|---|---|---|---|---|---|---|
| 원본 컬러, ch0 단독 | 184.1 | – | – | 184 | 1.68 | **0** |
| 원본 컬러 3채널 | 146 | 22 | 19 | 187 | 1.70 | 약 62% |
| 원본 1밴드 3채널 | 184.1 | 99.7 | 184.1 | 468 | 1.39 | **0** |
| decimation 2 (1024×772) 컬러 | **184.1** | 10.0 | **184.1** | – | – | **0** |
| decimation 2 (1024×772) 컬러, 노출 일치 | **184.1** | **184.0** | **184.1** | 552 | 1.31 | **0** |
| 컬러 3채널, 카메라 60 fps 제한 | 60.0 | 60.0 | 60.0 | 180 | 1.64 | **0** (20초) |
| 컬러 3채널, 카메라 30 fps 제한 | 30.0 | 30.0 | 30.0 | 90 | 0.82 | **0** |

- **decimation 2 첫 행의 ch1 = 10.0은 대역폭이 아니다.** 그 채널의 노출이 100 ms여서 상한이
  10 fps였을 뿐이다. 노출을 5388 µs로 맞춘 다음 행이 이 구성의 실제 능력이다 — 3채널 합계
  552 fps, 유실 0, 천장의 77%. 표를 읽을 때 한 채널만 낮은 행은 먼저 노출을 의심할 것.
- `M_BAYER_CONVERSION`이 프레임당 페이로드를 **3배**(3.04 → 9.12 MB)로 만든다. 원본 컬러로
  3채널 184 fps는 5.0 GB/s가 필요해 **성립하지 않는다.**
- 컬러를 유지하려면 프레임당 페이로드를 **원본의 1/4 이하**로 줄여야 한다(≈2.3 MB → 1.25 GB/s,
  마진 27%).
- 카메라 60 fps 제한은 천장의 96%라 마진이 없다 — 60초 런에서 한 채널이 387프레임을 놓쳤다.
  45~50 fps가 안전선이다.

**그 천장의 정체는 PCIe 링크 폭이다 (2026-08-24 확인)**

보드가 **Gen2 x4**로 붙어 있고, **보드 자체는 x8을 지원한다.**

```powershell
$id = (Get-PnpDevice | Where-Object { $_.FriendlyName -match 'Rapixo' }).InstanceId
'CurrentLinkSpeed','CurrentLinkWidth','MaxLinkSpeed','MaxLinkWidth' | ForEach-Object {
  "$_ = " + (Get-PnpDeviceProperty -InstanceId $id -KeyName "DEVPKEY_PciDevice_$_").Data }
# CurrentLinkSpeed = 2   CurrentLinkWidth = 4
# MaxLinkSpeed     = 2   MaxLinkWidth     = 8
```

`LinkSpeed = 2`는 5.0 GT/s(Gen2)다. Gen2는 8b/10b 인코딩이라 레인당 실효 500 MB/s이므로:

| 구성 | 이론 | 실효(오버헤드 감안) | 실측 |
|---|---|---|---|
| **현재 — Gen2 x4** | 2.0 GB/s | 1.6~1.8 GB/s | **1.7~1.79 GB/s** |
| Gen2 x8 (보드 최대) | 4.0 GB/s | 3.2~3.6 GB/s | 미측정 |

**실측 천장이 x4의 실효 대역폭과 일치한다.** 즉 병목은 보드의 DMA 엔진이나 호스트 메모리가
아니라 **링크 폭**이다. 그리고 `MaxLinkSpeed = 2`이므로 세대는 올릴 수 없고, **폭만 올릴 수 있다.**

**x8 슬롯으로 옮기면 천장이 대략 두 배가 되고, 지금까지 설계를 지배한 제약이 사라진다.**
원본 해상도 컬러 3채널 100 fps는 2.87 GB/s여서 x4에서는 불가능하지만 x8의 실효 안에 들어온다.
그러면 디시메이션도 1밴드 취득도 필요 없어진다.

확인해야 할 것:

- 지금 꽂힌 슬롯이 **물리적으로 x8 이상이면서 전기적으로도 8레인**인지. x16 슬롯이 x4로만
  배선된 경우가 흔하다(칩셋 레인 배분·바이퍼케이션).
- BIOS의 레인 배분 설정. 슬롯은 x8인데 BIOS가 x4로 묶어 둔 경우도 있다.
- 옮긴 뒤 **위 명령으로 `CurrentLinkWidth = 8`을 확인**하고, 그다음 실제 취득량을 다시 잰다.
  링크가 넓어져도 보드의 DMA 엔진이 그만큼 못 낼 가능성은 남아 있다 — 링크가 유일한 병목이었다는
  것은 x4에서의 일치로부터 추론한 것이고, x8에서 재보기 전까지는 추론이다.

**그리고 지금 꽂힌 슬롯이 x4다 — x16 슬롯은 비어 있다 (2026-08-24 확인)**

| 항목 | 값 |
|---|---|
| 메인보드 | Gigabyte B650M AORUS ELITE (Micro-ATX, AM5) |
| CPU | AMD Ryzen 7 9700X — 외장 GPU 없음, 내장 그래픽 사용 |
| Rapixo의 PCI 위치 | 버스 **12**, 장치 0 |
| 그 상위 | `VEN_1022&DEV_43F5` = **AMD 칩셋 PCIe 다운스트림 스위치 포트** (버스 3, 장치 8) |
| BIOS가 보고하는 슬롯 | `PCIE1` = x16 **Available**, `PCIE3` = x4 Available, `J3502` = x4 In Use (M.2) |

**보드는 CPU 직결 루트 포트가 아니라 칩셋 뒤에 있다.** 그래서 x4로 링크된 것이고 카드 문제가
아니다 — 칩셋 슬롯이 전기적으로 x4다.

**할 일: 카드를 `PCIE1`(CPU 직결 x16)로 옮긴다.** 그 슬롯은 비어 있다(외장 GPU가 없다).
카드는 Gen2가 최대이므로 Gen4 슬롯에서도 Gen2로 협상하지만, **폭은 자기 최대인 x8까지 올라간다.**

부수 효과: 칩셋 업링크(2.5GbE · USB · SATA가 함께 쓴다)에서 빠져나온다. 업링크 자체는 Gen4 x4로
넉넉하지만, 취득이 1.7 GB/s를 끌면서 같은 경로로 RAW 세그먼트를 쓰는 구성이라면 경합이 사라지는
쪽이 낫다.

옮긴 뒤 확인 순서:

1. 위의 PowerShell로 **`CurrentLinkWidth = 8`** 을 먼저 확인한다. 4로 남아 있으면 슬롯·BIOS
   문제이므로 거기서 멈춘다.
2. 그다음 `--autostart`로 실제 취득량을 다시 잰다. **링크가 넓어져도 보드의 DMA 엔진이 그만큼
   못 낼 가능성이 남아 있다** — "링크가 유일한 병목"은 x4에서 숫자가 일치한다는 추론이고,
   x8에서 재보기 전까지는 확인된 것이 아니다.
3. 천장이 실제로 오르면 decimation·1밴드 결정을 되돌린다. 원본 해상도 컬러 3채널 100 fps는
   2.87 GB/s이므로 x8의 실효(3.2~3.6) 안에 들어온다.

**슬롯을 옮겼다 — 천장이 3.68 GB/s로 올랐다 (2026-08-27 실측)**

카드를 칩셋 뒤의 x4 슬롯에서 **CPU 직결 x16 슬롯**으로 옮겼다. 링크가 x8로 협상됐고,
상위가 칩셋 스위치 포트가 아니라 루트 포트로 바뀌었다.

```
CurrentLinkWidth = 8   (이전 4)      MaxLinkWidth = 8   ← 보드 최대치에 도달
CurrentLinkSpeed = 2   (Gen2, 불변)  MaxLinkSpeed = 2
위치 : PCI 버스 1, 장치 0            상위 : PCI Express 루트 포트 (버스 0, 장치 1)
```

**과포화로 밀어 천장을 다시 쟀다** — 원본 컬러 3채널, 노출 5388 µs, 캡 없음:

| 채널 | 전달 fps | 유실 | 전달량 |
|---|---|---|---|
| ch0 | 183.9 | 0 | 1.76 GB/s |
| ch1 | 172.8 | 110 | 1.65 GB/s |
| ch2 | 27.7 | 2455 | 0.27 GB/s |
| | | **합계** | **3.68 GB/s** |

요구량은 5.28 GB/s였고 보드가 내준 것이 3.68이다. **이전 천장 1.70의 2.16배**이고, Gen2 x8
이론치 4.0의 92%다 — 링크가 병목이었다는 추론이 실측으로 확인됐다. 배분이 불공정한 것
(ch2가 굶는 것)은 과포화 상태의 기존 현상 그대로다.

**이제 이 값은 더 올릴 수 없다.** `MaxLinkSpeed`도 `MaxLinkWidth`도 현재값과 같다 — 보드가
자기 최대치에 있다. 그 이상은 다른 보드가 필요하다.

**열리는 구성:**

| 구성 | 요구량 | x4에서 | x8에서 |
|---|---|---|---|
| 원본 컬러 3채널 100 fps | 2.87 GB/s | 불가 | **실측 확인** — 99.6 fps × 3, 유실 0, 2.86 GB/s (78%) |
| 원본 컬러 3채널 184 fps | 5.28 GB/s | 불가 | 불가 |
| decimation 2 컬러 3채널 100 fps | 0.71 GB/s | 가능 | 가능 (19%) |

즉 **100 fps에서는 디시메이션도 1밴드도 필요 없다.** 설계를 지배하던 페이로드 제약이
그 지점에서 사라진다. 2026-08-27 12:23에 실측으로 확인했다 — 원본 해상도 컬러 3채널이
99.6 fps에서 채널당 1493프레임, **유실 0**.

다만 2.86은 경고선(2.94)과 3%밖에 차이가 없다. 노출이 조금만 짧아져 fps가 오르면 경고가 뜬다.
같은 실행에서 분석 ROI가 decimation 2 → 1 변경을 따라 정확히 두 배가 된 것도 확인됐다
(CAM1 252x154 @ 456,368 → 504x308 @ 912,736). 그 재축척은 GUI에서 Decim을 바꾼 시점에 일어난다.

**부수 관찰: 전원을 내리면 카메라 설정이 초기화된다.** 카드를 교체하며 PC 전원을 내린 뒤
노출이 8000/8000/10000 → 5388 µs로, `AcquisitionFrameRateEnable`이 On → Off로 돌아왔다.
앱을 닫는 것으로는 남지만 전원 재인가로는 사라진다 — 두 가지가 다르다.
(누가 손으로 바꿨을 가능성을 완전히 배제하지는 못했다.)

**표시 경로 — UI 스레드 1개 공유**

`MILWPFDisplay`는 `HwndHost`도 `D3DImage`도 아니다. 어셈블리에 `_writeableBitmap`,
`_displaySurface`, `CopySurfaceToImageSource`, `DispatcherOperation`, `BeginInvoke`가 있다. 즉
**4개 pane 전부의 픽셀 업로드와 합성이 단일 WPF UI 스레드에서** 일어난다. 취득 훅은 채널별 MIL
스레드에서 돈다(측정된 관리 스레드 ID가 채널마다 다르고 전 구간 고정).

기본 상태에서 **스로틀이 없다**: `M_UPDATE_RATE`가 `M_PROCESS_FRAME_RATE`와 정확히 같다 —
grab 1프레임 = 표시 1갱신.

| 표시 설정 (원본 컬러 3채널) | 합계 fps | CPU(코어) |
|---|---|---|
| 기본 (스로틀 없음) | 187 | 1.29 |
| `M_UPDATE_RATE_MAX = 30` | 188 | 0.78 |
| 훅의 `MbufCopy`를 30 fps로 감축 | 190 | 0.71 |
| 둘 다 | 180 | 0.58 |
| 표시 완전 차단 (`M_UPDATE, M_DISABLE`) | 186 | 0.45 |

- **표시 스로틀은 취득 fps를 늘려주지 않는다.** 완전히 껐을 때조차 합계가 변하지 않는다. 즉
  천장은 호스트 메모리 대역폭이 아니라 **PCIe/보드 DMA**다. 스로틀의 목적은 CPU 확보다.
- **`M_UPDATE_RATE_MAX`는 디스플레이당 스레드를 추가로 띄운다**(45 → 65). 과포화 상태에서는 한
  채널이 2.3 fps로 붕괴하는 것을 관측했다. **대역폭 여유를 먼저 만든 뒤** 캡을 얹어야 한다.
- 모든 실행에서 `DispatcherTimer`의 실제 간격은 500 ms를 지켰다. UI 스레드 공유 자체는 30 fps ×
  3채널에서 병목이 아니다.

**`MbufBayer`는 이 장비에서 블록한다 — 예외도, 오류도 없이 반환하지 않는다**

세 경우 모두 재현했다.

| 호출 위치 / 대상 | 결과 |
|---|---|
| 훅 → `MdispSelect`된 표시 버퍼 | 훅이 첫 프레임에서 멈춤. `M_PROCESS_FRAME_COUNT`가 1에 고정 |
| 훅 → 중간 `M_PROC` 버퍼 경유 | 동일하게 멈춤 (대상 버퍼 문제가 아니다) |
| 훅 밖 (스탯 틱, UI 스레드) | **앱 전체 정지.** 스탯 타이머 사망, `0.0 fps (0 frames)` |

`MbufGetColor` 함정과 같은 계열이다(§7 참고). **호스트 디베이어를 MIL로 할 수 없다** — "1밴드로
받고 표시할 것만 디베이어" 경로는 막혀 있다. 컬러가 필요하면 보드의 `M_BAYER_CONVERSION`을
쓰면서 ROI/decimation으로 페이로드를 줄이는 것이 유일한 길이다.

**측정된 프레임 예산과 비용**

| 항목 | 값 |
|---|---|
| 184 fps의 프레임 예산 | **5.4 ms** |
| `MbufCopy` (원본 컬러 9.12 MB) | 0.9~1.5 ms |
| `MbufCopy` (1밴드 3.04 MB) | 0.27~0.5 ms |
| 밝기 측정 1회 (4% 커버리지, 전역 1값) | **0.20~0.53 ms** |

밝기 측정 비용은 184 Hz로 돌려도 채널당 코어의 7.4%다. **프레임별 측정은 예산 안에 들어온다** —
현재 500 ms 틱에 있는 것은 성능 때문이 아니라 당시 요구사항이 그랬기 때문이다.

**카메라별 설정이 성능을 좌우한다 (하드웨어 고장으로 오진하기 쉬움)**

`AcquisitionFrameRate`의 **최대값은 `ExposureTime`에 종속**된다. 측정 당시 Camera 1의
`ExposureTime`이 100000 µs(100 ms)여서 최대 10 fps였다 — Camera 0·2는 5388 µs. 한 채널만 느릴
때 케이블·링크·arbitration을 의심하기 전에 **노출을 먼저 확인할 것.**

주의할 영속성:

- `AcquisitionFrameRate` / `AcquisitionFrameRateEnable`은 **카메라에 남는다.** 앱을 닫아도
  유지된다. 복구는 `AcquisitionFrameRate`를 `M_FEATURE_MAX`로 되돌리고 `Enable`을 끄는 것.
- `DecimationHorizontal`/`Vertical`도 **카메라에 남는다.** `M_BAYER_CONVERSION`과 같은 성격이다.
- 이 피처들은 **정수형이다.** `M_TYPE_DOUBLE`로 쓰면 조용히 무시된다 —
  `M_TYPE_MIL_INT`를 쓸 것.

---

*본 리포트는 `src/` 전체를 라인 단위로 읽고 작성했다. 커밋 이력의 수정 사유
(`f2fde52` shearing, `4db0b71` 밴드 추출, `568839f` Bayer 강제 ON, `e115bfc` 분리 감지)도
대조해 근거로 삼았다.*
