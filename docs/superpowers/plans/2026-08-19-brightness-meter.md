# 밝기 미터 구현 계획

> **에이전트 작업자에게:** 필수 하위 스킬 — 이 계획은 `superpowers:subagent-driven-development`(권장) 또는 `superpowers:executing-plans`로 작업 단위별로 실행한다. 단계는 체크박스(`- [ ]`) 문법으로 추적한다.

**목표:** 카메라별 밝기를 Rec.601 luma로 수치화해 창 하단의 공통 시간축 그래프에 실시간 표시하되, 취득 경로에는 어떤 영향도 주지 않는다.

**설계 개요:** 측정은 기존 500ms `DispatcherTimer` 위에서만 돈다. 디스플레이 버퍼에서 세로로 흩뿌린 스트립을 점 추출해 luma·포화율·흑화율을 한 번의 순회로 계산하고, 채널당 링 버퍼에 쌓아 하단 스트립에 그린다.

**기술 스택:** C# / WPF / net6.0-windows / x64, MIL 10.70 (`MbufChildColor`, `MbufGet2d`)

**스펙:** `docs/superpowers/specs/2026-08-19-brightness-meter-design.md`

## 전역 제약

- **`CameraChannel.OnGrabbedFrame`에 코드를 추가하지 않는다.** 최종 diff에서 이 메서드가 한 줄도 바뀌지 않아야 한다. 이 계획에서 가장 중요한 제약이다.
- 대상 프레임워크는 `net6.0-windows`, 플랫폼은 `x64` 고정. 변경하지 않는다.
- **차트 라이브러리를 포함해 NuGet 패키지를 추가하지 않는다.**
- 밴드 바이트 추출은 `MbufGetColor`를 쓰지 않는다 — 이 버퍼들에서 패킹 경로는 멈추고 플래나 경로는 0을 돌려준다. `MbufChildColor` + `MbufGet2d`를 쓴다.
- `MbufGet`이 아니라 `MbufGet2d`다. grab/display 버퍼는 행 패딩이 있어(pitch 2112 > width 2064) `MbufGet`은 어긋난 데이터를 준다.
- 밴드 자식은 부모 버퍼보다 **먼저** 해제한다.
- 소스 주석과 XAML UI 라벨은 **영어**(주변과 일치). 문서는 **한국어**.
- 용어는 `CONTEXT.md`를 따른다. 특히 `Channel`(슬롯, 항상 4개)과 `Camera`(장치, 없을 수 있음)를 구분한다.
- 이 리포에는 테스트 프로젝트가 없다. 스펙이 자동 테스트를 범위 밖으로 두었으므로, 각 작업은 **실행 가능한 확인 명령과 기대 출력**으로 검증한다. 테스트 코드를 지어내지 않는다.
- 빌드 기대치: `오류 0개`, 경고는 기존 `NETSDK1138` 2건뿐. 세 번째 경고가 나오면 그것은 결함이다.

---

### 작업 1: `BrightnessHistory` — MIL 없는 링 버퍼

가장 먼저 만든다. MIL을 전혀 쓰지 않으므로 하드웨어 없이 검증할 수 있고, 뒤 작업들이 이 타입에 의존한다.

**파일:**
- 생성: `src/Infrastructure/BrightnessHistory.cs`

**인터페이스:**
- 사용: 없음
- 제공: `BrightnessSample` (readonly struct: `float Luma`, `float ClippedPct`, `float BlackPct`), `BrightnessHistory` (`Capacity`=240, `Count`, `HasData`, `Latest`, `Add(BrightnessSample)`, `Clear()`, `CopyTo(BrightnessSample[])`)

- [ ] **1단계: 파일 작성**

`src/Infrastructure/BrightnessHistory.cs`를 만든다. 이 폴더의 다른 파일들과 마찬가지로 **MIL 호출이 0회**여야 한다.

```csharp
using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>One brightness reading: Rec.601 luma plus the two clipping proportions.</summary>
    public readonly struct BrightnessSample
    {
        /// <summary>Mean Rec.601 luma, 0-255.</summary>
        public readonly float Luma;
        /// <summary>Percentage of sampled pixels with any band at 255.</summary>
        public readonly float ClippedPct;
        /// <summary>Percentage of sampled pixels with every band at 0.</summary>
        public readonly float BlackPct;

        public BrightnessSample(float luma, float clippedPct, float blackPct)
        {
            Luma = luma;
            ClippedPct = clippedPct;
            BlackPct = blackPct;
        }
    }

    /// <summary>
    /// A fixed-size ring of brightness readings for one channel. Sized for two minutes at the
    /// 500 ms stats tick; the oldest reading is dropped once it is full.
    /// </summary>
    public sealed class BrightnessHistory
    {
        /// <summary>240 readings = 120 s at the 500 ms stats tick.</summary>
        public const int Capacity = 240;

        private readonly BrightnessSample[] _items = new BrightnessSample[Capacity];
        private int _start;   // index of the oldest item
        private int _count;

        /// <summary>How many readings are currently held, up to <see cref="Capacity"/>.</summary>
        public int Count => _count;

        public bool HasData => _count > 0;

        /// <summary>The most recent reading. Meaningless when <see cref="HasData"/> is false.</summary>
        public BrightnessSample Latest =>
            _count == 0 ? default : _items[(_start + _count - 1) % Capacity];

        public void Add(BrightnessSample sample)
        {
            if (_count < Capacity)
            {
                _items[(_start + _count) % Capacity] = sample;
                _count++;
            }
            else
            {
                _items[_start] = sample;
                _start = (_start + 1) % Capacity;
            }
        }

        public void Clear()
        {
            _start = 0;
            _count = 0;
        }

        /// <summary>
        /// Copies the readings oldest-first into <paramref name="destination"/>, which must hold at
        /// least <see cref="Count"/> entries. Returns how many were written.
        /// </summary>
        public int CopyTo(BrightnessSample[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.Length < _count) throw new ArgumentException("destination too small", nameof(destination));

            for (int i = 0; i < _count; i++)
                destination[i] = _items[(_start + i) % Capacity];
            return _count;
        }
    }
}
```

- [ ] **2단계: 빌드**

실행: `dotnet build MatroxFrameGrabber.slnx -c Release --nologo`
기대: `오류 0개`, 경고 2개(기존 `NETSDK1138`).

- [ ] **3단계: MIL 비의존 확인**

실행: `grep -cE "^using Matrox|MIL_ID|MIL\.M[a-zA-Z]|Matrox\.MatroxImagingLibrary" src/Infrastructure/BrightnessHistory.cs`
기대: `0`. 이 폴더의 다른 여섯 파일과 같은 성질이다.

- [ ] **4단계: 커밋**

```bash
git add src/Infrastructure/BrightnessHistory.cs
git commit -m "Add a ring of brightness readings

Two minutes of history per channel at the stats tick, with no MIL
dependency so it can be exercised without the board."
```

---

### 작업 2: `BrightnessMeter` — MIL 버퍼에서 표본을 읽어 계산

**파일:**
- 생성: `src/Mil/BrightnessMeter.cs`

**인터페이스:**
- 사용: `BrightnessSample`, `BrightnessHistory` (작업 1)
- 제공: `BrightnessMeter` (`History` 프로퍼티, `Sample(MIL_ID displayBuffer)`, `LastSampleMs` 프로퍼티)

- [ ] **1단계: `MimStat` 가용성을 먼저 확인한다**

스펙 4절의 미해결 항목이다. 이 리포는 `MimResize`/`MimShift`를 프로덕션에서 쓰고 있지만 라이선스 조회는 `M_LICENSE_LITE`만 보고했으므로, `MimStat`이 되는지 단정할 수 없다.

`BrightnessMeter`를 작성하는 동안 임시로 `MimStat` 호출을 하나 넣고 `MILException`이 나는지 관찰한 뒤 **그 임시 코드는 지운다.** 결과를 보고서에 적는다.

- 예외가 나지 않고 값이 나오면: 보고서에 "MimStat 사용 가능"이라 적고 **그래도 이번에는 스트립 샘플링으로 진행한다.** 이유는 아래 2단계 주석에 적는다. 사용 가능하다는 사실만 기록해 두면 나중에 필요할 때 판단할 수 있다.
- 예외가 나면: 보고서에 예외 메시지를 그대로 적는다.

어느 쪽이든 이 작업의 산출물은 동일하다. 이 단계는 **답을 기록으로 남기는 것**이 목적이다.

- [ ] **2단계: 파일 작성**

`src/Mil/BrightnessMeter.cs`를 만든다.

```csharp
using System;
using System.Diagnostics;
using Matrox.MatroxImagingLibrary;
using MatroxFrameGrabber.Infrastructure;

namespace MatroxFrameGrabber.Mil
{
    /// <summary>
    /// Measures one channel's brightness off the acquisition path: the stats tick calls
    /// <see cref="Sample"/>, which reads a scatter of strips out of the display buffer.
    ///
    /// It deliberately point-samples at full resolution rather than measuring a downscaled copy.
    /// Bilinear downscaling averages a clipped pixel together with its neighbours — 255 beside 200
    /// becomes 227 — which erases exactly the failure the clipping figures exist to catch.
    ///
    /// The strips are spread down the frame rather than taken as one contiguous band, so a light
    /// source in the top of the frame cannot hide from the sample.
    /// </summary>
    public sealed class BrightnessMeter
    {
        /// <summary>Strips spread evenly down the frame.</summary>
        private const int Strips = 16;
        /// <summary>Rows read per strip. 16 x 4 = 64 rows of a 1544-row frame, about 4%.</summary>
        private const int RowsPerStrip = 4;

        private byte[] _r, _g, _b;
        private int _stripBytes;

        public BrightnessHistory History { get; } = new BrightnessHistory();

        /// <summary>Wall time the last <see cref="Sample"/> took, for the 500 ms budget check.</summary>
        public double LastSampleMs { get; private set; }

        /// <summary>
        /// Reads one brightness reading from <paramref name="displayBuffer"/> and appends it.
        /// Does nothing when the buffer is unbound. Never throws: a MIL failure here must not cost
        /// the caller its stats tick.
        /// </summary>
        public void Sample(MIL_ID displayBuffer)
        {
            if (displayBuffer == MIL.M_NULL)
                return;

            var watch = Stopwatch.StartNew();
            try
            {
                MIL_INT width = MIL.MbufInquire(displayBuffer, MIL.M_SIZE_X, MIL.M_NULL);
                MIL_INT height = MIL.MbufInquire(displayBuffer, MIL.M_SIZE_Y, MIL.M_NULL);
                MIL_INT bands = MIL.MbufInquire(displayBuffer, MIL.M_SIZE_BAND, MIL.M_NULL);
                if (width <= 0 || height < RowsPerStrip)
                    return;

                EnsureBuffers((int)width);

                if (bands >= 3)
                    SampleColor(displayBuffer, (int)width, (int)height);
                else
                    SampleMono(displayBuffer, (int)width, (int)height);
            }
            catch (MILException)
            {
                // A buffer can be freed and reallocated underneath us when a camera is reloaded.
                // Losing one reading is not worth disturbing the caller's tick.
            }
            finally
            {
                LastSampleMs = watch.Elapsed.TotalMilliseconds;
            }
        }

        public void Reset() => History.Clear();

        private void EnsureBuffers(int width)
        {
            int needed = width * RowsPerStrip;
            if (_stripBytes == needed && _r != null)
                return;
            _stripBytes = needed;
            _r = new byte[needed];
            _g = new byte[needed];
            _b = new byte[needed];
        }

        private void SampleColor(MIL_ID buffer, int width, int height)
        {
            // Band children are created and freed inside this call rather than cached. Caching them
            // would tie their lifetime to the display buffer's, and a child outliving its parent is
            // the failure this codebase already documents.
            MIL_ID red = MIL.M_NULL, green = MIL.M_NULL, blue = MIL.M_NULL;
            try
            {
                MIL.MbufChildColor(buffer, MIL.M_RED, ref red);
                MIL.MbufChildColor(buffer, MIL.M_GREEN, ref green);
                MIL.MbufChildColor(buffer, MIL.M_BLUE, ref blue);

                double lumaSum = 0;
                long clipped = 0, black = 0, counted = 0;
                int step = height / Strips;

                for (int s = 0; s < Strips; s++)
                {
                    int y = s * step;
                    if (y + RowsPerStrip > height)
                        break;

                    MIL.MbufGet2d(red, 0, y, width, RowsPerStrip, _r);
                    MIL.MbufGet2d(green, 0, y, width, RowsPerStrip, _g);
                    MIL.MbufGet2d(blue, 0, y, width, RowsPerStrip, _b);

                    for (int i = 0; i < _stripBytes; i++)
                    {
                        byte r = _r[i], g = _g[i], b = _b[i];
                        lumaSum += 0.299 * r + 0.587 * g + 0.114 * b;
                        if (r == 255 || g == 255 || b == 255) clipped++;
                        if (r == 0 && g == 0 && b == 0) black++;
                        counted++;
                    }
                }

                Append(lumaSum, clipped, black, counted);
            }
            finally
            {
                if (blue != MIL.M_NULL) MIL.MbufFree(blue);
                if (green != MIL.M_NULL) MIL.MbufFree(green);
                if (red != MIL.M_NULL) MIL.MbufFree(red);
            }
        }

        private void SampleMono(MIL_ID buffer, int width, int height)
        {
            // RAW recording turns M_BAYER_CONVERSION off, so the display buffer becomes the
            // single-band Bayer mosaic. Its plain mean is 0.25R + 0.50G + 0.25B by the CFA's own
            // 1R:2G:1B ratio, which is close enough to Rec.601 that the graph stays continuous
            // when a RAW recording starts.
            double lumaSum = 0;
            long clipped = 0, black = 0, counted = 0;
            int step = height / Strips;

            for (int s = 0; s < Strips; s++)
            {
                int y = s * step;
                if (y + RowsPerStrip > height)
                    break;

                MIL.MbufGet2d(buffer, 0, y, width, RowsPerStrip, _r);

                for (int i = 0; i < _stripBytes; i++)
                {
                    byte v = _r[i];
                    lumaSum += v;
                    if (v == 255) clipped++;
                    if (v == 0) black++;
                    counted++;
                }
            }

            Append(lumaSum, clipped, black, counted);
        }

        private void Append(double lumaSum, long clipped, long black, long counted)
        {
            if (counted == 0)
                return;
            History.Add(new BrightnessSample(
                (float)(lumaSum / counted),
                (float)(100.0 * clipped / counted),
                (float)(100.0 * black / counted)));
        }
    }
}
```

- [ ] **3단계: 빌드**

실행: `dotnet build MatroxFrameGrabber.slnx -c Release --nologo`
기대: `오류 0개`, 경고 2개.

- [ ] **4단계: 임시 `MimStat` 코드가 남아 있지 않은지 확인**

실행: `grep -n "MimStat" src/Mil/BrightnessMeter.cs || echo "없음"`
기대: `없음`.

- [ ] **5단계: 커밋**

```bash
git add src/Mil/BrightnessMeter.cs
git commit -m "Measure brightness by point-sampling the display buffer

Downscaling would have been cheaper but averages clipped pixels away,
erasing the very failure the clipping figures exist to catch. The strips
are spread down the frame so a light source at the top cannot hide."
```

---

### 작업 3: 채널에 연결 — 훅은 건드리지 않는다

**파일:**
- 수정: `src/Mil/CameraChannel.cs` (필드 선언부, `RefreshStats()`, `FreeBuffers()`)

**인터페이스:**
- 사용: `BrightnessMeter` (작업 2)
- 제공: `CameraChannel.Brightness` (`BrightnessHistory`), `CameraChannel.LastBrightnessSampleMs` (`double`)

- [ ] **1단계: 필드와 프로퍼티 추가**

`src/Mil/CameraChannel.cs`의 `_dispBufId` 선언(71행) 근처, 다른 private 필드들 사이에 넣는다.

```csharp
        private readonly BrightnessMeter _brightness = new BrightnessMeter();
```

그리고 공개 프로퍼티를 다른 프로퍼티들 사이에 넣는다.

```csharp
        /// <summary>This channel's brightness readings, appended on each stats tick.</summary>
        public BrightnessHistory Brightness => _brightness.History;

        /// <summary>Wall time the last brightness reading took, for the tick-budget check.</summary>
        public double LastBrightnessSampleMs => _brightness.LastSampleMs;
```

`using MatroxFrameGrabber.Infrastructure;`가 이 파일에 없으면 추가한다.

- [ ] **2단계: `RefreshStats()`에서 표본을 뜬다**

`RefreshStats()`(693행부터)의 `if (_isGrabbing && _digId != MIL.M_NULL)` 블록 **안**, 프레임레이트를 읽은 직후에 넣는다. grab 중이 아닌 채널은 표본을 뜨지 않는다 — 디스플레이 버퍼에 유효한 화면이 없기 때문이다.

```csharp
                // Brightness is measured here, on the stats tick, and never in the grab hook:
                // anything added to MdigProcess runs inside the acquisition budget.
                _brightness.Sample(_dispBufId);
```

- [ ] **3단계: 버퍼 해제 시 이력을 비운다**

`FreeBuffers()`(541행부터) 안에서 `_dispBufId`를 해제하기 전에 넣는다. 카메라를 다시 로드하면 이전 이력은 다른 화면의 것이므로 이어 붙이면 거짓말이 된다.

```csharp
            _brightness.Reset();
```

- [ ] **4단계: 훅이 손대지지 않았음을 확인한다**

이 계획에서 가장 중요한 검증이다.

실행: `git diff -U0 src/Mil/CameraChannel.cs | grep -c "OnGrabbedFrame"`
기대: `0`.

실행: `git diff src/Mil/CameraChannel.cs`
기대: 변경 hunk가 필드 선언, 프로퍼티, `RefreshStats`, `FreeBuffers` 네 곳뿐이고 `OnGrabbedFrame` 본문은 어디에도 나타나지 않는다. 눈으로 직접 확인한다.

- [ ] **5단계: 빌드**

실행: `dotnet build MatroxFrameGrabber.slnx -c Release --nologo`
기대: `오류 0개`, 경고 2개.

- [ ] **6단계: 커밋**

```bash
git add src/Mil/CameraChannel.cs
git commit -m "Sample brightness on the stats tick

The grab hook is untouched by design: anything added there runs inside
the acquisition budget, where heavy work stalls the grab rather than
merely lagging the preview."
```

---

### 작업 4: 하단 스트립 UI

**파일:**
- 수정: `src/Views/Styles.xaml` (색 정의)
- 수정: `src/Views/MainWindow.xaml` (툴바 토글 + 하단 스트립)
- 수정: `src/Views/MainWindow.xaml.cs` (그리기)
- 수정: `src/ViewModels/MainViewModel.cs` (틱 이벤트, 토글 상태)

**인터페이스:**
- 사용: `CameraChannel.Brightness`, `CameraChannel.LastBrightnessSampleMs` (작업 3)
- 제공: `MainViewModel.StatsRefreshed` (`event Action`), `MainViewModel.ShowBrightness` (`bool`)

- [ ] **1단계: 채널 색 정의**

`src/Views/Styles.xaml`의 `RecHoverColor`(16행) 다음에 넣는다. 다크 배경에서 서로 구분되고, 녹화 상태의 붉은 계열과 혼동되지 않는 4색이다.

```xml
    <Color x:Key="Ch0Color">#FF4FC3F7</Color>
    <Color x:Key="Ch1Color">#FF81C784</Color>
    <Color x:Key="Ch2Color">#FFFFB74D</Color>
    <Color x:Key="Ch3Color">#FFBA68C8</Color>
```

같은 파일에서 기존 브러시들이 정의된 자리에 대응 브러시를 추가한다(기존 `SolidColorBrush` 정의들과 같은 형식을 따른다).

```xml
    <SolidColorBrush x:Key="Ch0Brush" Color="{StaticResource Ch0Color}" />
    <SolidColorBrush x:Key="Ch1Brush" Color="{StaticResource Ch1Color}" />
    <SolidColorBrush x:Key="Ch2Brush" Color="{StaticResource Ch2Color}" />
    <SolidColorBrush x:Key="Ch3Brush" Color="{StaticResource Ch3Color}" />
```

- [ ] **2단계: 뷰모델에 틱 이벤트와 토글 추가**

`src/ViewModels/MainViewModel.cs`의 타이머 Tick 핸들러(33-40행) 끝, `RaiseChanged(nameof(AnyRawRecording));` 다음 줄에 넣는다.

```csharp
                StatsRefreshed?.Invoke();
```

그리고 다른 공개 멤버들 사이에 넣는다.

```csharp
        /// <summary>Raised on the UI thread after every stats tick, so the view can redraw.</summary>
        public event Action StatsRefreshed;

        private bool _showBrightness;

        /// <summary>Whether the brightness strip along the bottom of the window is expanded.</summary>
        public bool ShowBrightness
        {
            get => _showBrightness;
            set { _showBrightness = value; RaiseChanged(nameof(ShowBrightness)); }
        }
```

- [ ] **3단계: 툴바에 토글 버튼**

`src/Views/MainWindow.xaml`의 `Browse…` 버튼(110-111행) 다음, `</WrapPanel>` 앞에 넣는다.

```xml
                    <Border Width="1" Height="22" Background="{StaticResource BorderBrushColor}" Margin="8,0" VerticalAlignment="Center" />
                    <ToggleButton Content="Brightness" Padding="8,4"
                                  IsChecked="{Binding ShowBrightness}"
                                  ToolTip="Show a brightness graph for every running channel" />
```

- [ ] **4단계: 컨버터 등록**

`MainWindow.xaml`에는 `Window.Resources` 블록이 없다. 11행 `Background="{StaticResource BgBrush}">` 다음, 13행 `<Grid>` 앞에 넣는다. `CameraPaneView.xaml:7`이 쓰는 것과 같은 형식이며, 새 컨버터 클래스를 작성하지 않는다.

```xml
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
    </Window.Resources>
```

- [ ] **5단계: 하단 스트립**

`MainContent`는 `LastChildFill="True"`인 `DockPanel`이고(15행), 카메라 격자 `<Grid Margin="2">`가 그 마지막 자식이라 남는 공간을 채운다. `DockPanel`은 **선언 순서대로** 도킹하므로, 스트립은 툴바를 닫는 `</DockPanel>`(113행) 다음이자 `<Grid Margin="2">`(115행) **앞**에 와야 한다. 격자 뒤에 두면 격자가 더 이상 마지막 자식이 아니게 되어 레이아웃이 뒤집힌다.

```xml
            <Border DockPanel.Dock="Bottom" Height="110" Margin="2,0,2,2"
                    Background="{StaticResource PanelBrush}"
                    BorderBrush="{StaticResource BorderBrushColor}" BorderThickness="1"
                    Visibility="{Binding ShowBrightness, Converter={StaticResource BoolToVis}}">
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto" />
                        <RowDefinition Height="*" />
                    </Grid.RowDefinitions>
                    <StackPanel x:Name="BrightnessLegend" Grid.Row="0" Orientation="Horizontal" Margin="6,4,6,2" />
                    <Canvas x:Name="BrightnessCanvas" Grid.Row="1" Margin="6,0,6,4" ClipToBounds="True" />
                </Grid>
            </Border>
```

`Visibility`가 `Collapsed`이면 `DockPanel`에서 공간을 차지하지 않으므로, 접었을 때 격자가 원래 크기로 돌아온다.

- [ ] **6단계: 그리기 코드**

`src/Views/MainWindow.xaml.cs`에 넣는다. 생성자에서 `((MainViewModel)DataContext).StatsRefreshed += RedrawBrightness;`로 구독하고, 창이 닫힐 때 해제한다(기존 `Closed` 처리와 같은 자리).

```csharp
        private static readonly string[] ChannelBrushKeys = { "Ch0Brush", "Ch1Brush", "Ch2Brush", "Ch3Brush" };
        private readonly Polyline[] _brightnessLines = new Polyline[4];
        private readonly BrightnessSample[] _sampleScratch = new BrightnessSample[BrightnessHistory.Capacity];

        /// <summary>
        /// Redraws the brightness strip. The vertical axis is pinned to 0-255 rather than scaled to
        /// the data: an auto-scaled axis hides the slow drift the graph exists to reveal.
        /// </summary>
        private void RedrawBrightness()
        {
            var vm = DataContext as MainViewModel;
            if (vm == null || !vm.ShowBrightness || BrightnessCanvas.ActualWidth <= 0)
                return;

            double w = BrightnessCanvas.ActualWidth;
            double h = BrightnessCanvas.ActualHeight;
            BrightnessLegend.Children.Clear();

            for (int i = 0; i < _brightnessLines.Length; i++)
            {
                if (_brightnessLines[i] == null)
                {
                    _brightnessLines[i] = new Polyline
                    {
                        Stroke = (Brush)FindResource(ChannelBrushKeys[i]),
                        StrokeThickness = 1.5
                    };
                    BrightnessCanvas.Children.Add(_brightnessLines[i]);
                }
                _brightnessLines[i].Points.Clear();
            }

            for (int i = 0; i < vm.Channels.Count && i < _brightnessLines.Length; i++)
            {
                var channel = vm.Channels[i];
                // A channel with no camera, or one that is stopped, has no valid display buffer —
                // drawing a flat zero for it would read as "this camera is completely dark".
                if (!channel.Brightness.HasData)
                    continue;

                int n = channel.Brightness.CopyTo(_sampleScratch);
                var points = _brightnessLines[i].Points;
                for (int p = 0; p < n; p++)
                {
                    double x = w * p / (BrightnessHistory.Capacity - 1);
                    double y = h - (h * _sampleScratch[p].Luma / 255.0);
                    points.Add(new Point(x, y));
                }

                BrightnessSample latest = channel.Brightness.Latest;
                var label = new TextBlock
                {
                    Text = $"{channel.Name}  {latest.Luma:F0}   clip {latest.ClippedPct:F1}%",
                    Margin = new Thickness(0, 0, 14, 0),
                    Foreground = latest.ClippedPct >= ClipWarnPercent
                        ? (Brush)FindResource("RecBrush")
                        : (Brush)FindResource(ChannelBrushKeys[i])
                };
                BrightnessLegend.Children.Add(label);
            }
        }
```

임계값 상수는 같은 클래스에 둔다. 스펙이 정한 출발점이며 현장에서 조정할 수 있도록 상수 하나로 남긴다.

```csharp
        /// <summary>Clipping above this share of sampled pixels is called out in the legend.</summary>
        private const double ClipWarnPercent = 1.0;
```

`RecBrush`는 `Styles.xaml:29`에 이미 정의돼 있다(녹화 상태의 붉은 계열). 새로 만들지 않는다.

- [ ] **7단계: 빌드**

실행: `dotnet build MatroxFrameGrabber.slnx -c Release --nologo`
기대: `오류 0개`, 경고 2개.

- [ ] **8단계: 커밋**

```bash
git add src/Views/Styles.xaml src/Views/MainWindow.xaml src/Views/MainWindow.xaml.cs src/ViewModels/MainViewModel.cs
git commit -m "Draw every channel's brightness on one shared axis

Splitting the graphs per channel would have lost the comparison that
matters most here — whether three cameras see the same illumination the
same way. The axis is pinned to 0-255 because an auto-scaled one hides
the drift the graph exists to show."
```

---

### 작업 5: 문서

**파일:**
- 수정: `CLAUDE.md` (구조 표, UI 상태 갱신 문단)
- 수정: `README.md` (기능 목록)

- [ ] **1단계: `CLAUDE.md` 구조 표에 새 파일 추가**

`src/Mil/`에 `BrightnessMeter`를, `src/Infrastructure/`에 `BrightnessHistory`를 한 줄씩 넣는다.

- [ ] **2단계: `CLAUDE.md`의 UI 상태 문단 보강**

"UI 상태는 500ms `DispatcherTimer` 하나가…" 문단에 한 문장을 덧붙인다. 이 문단은 새 값을 어디에 노출해야 하는지 알려주는 자리이고, 밝기 측정이 그 규칙을 따른 실례다.

```markdown
밝기 측정도 이 틱 위에서 돈다 — 취득 훅이 아니라 여기다. `MdigProcess` 훅에 넣은 작업은
취득 예산 안에서 돌기 때문이다.
```

- [ ] **3단계: `README.md` 기능 목록에 한 줄 추가**

`## 기능` 절에 넣는다.

```markdown
- **밝기 미터**: 채널별 Rec.601 luma와 포화 화소 비율을 창 하단 그래프에 실시간 표시
  (취득 경로와 분리된 500ms 틱에서 측정)
```

- [ ] **4단계: 커밋**

```bash
git add CLAUDE.md README.md
git commit -m "Document the brightness meter

The stats-tick paragraph is where a future reader learns to surface a new
value; the meter is the worked example of following it."
```

---

## 최종 인수 확인

작업 1-5를 마친 뒤 실행한다. 스펙의 "검증 방법" 절에 대응한다.

- [ ] `git diff`에서 `OnGrabbedFrame`이 **한 줄도 바뀌지 않았음**을 확인한다
- [ ] **3채널 grab + RAW 녹화 중, 미터를 켠 상태와 끈 상태에서 `M_PROCESS_FRAME_MISSED`가 같은지** 비교한다
- [ ] `LastBrightnessSampleMs`가 500ms 대비 충분히 작은지 확인한다
- [ ] 렌즈를 가려 luma가 0 근처로 가는지, 밝은 조명으로 포화율이 오르는지 확인한다
- [ ] 카메라 없는 4번 채널이 그래프에도 범례에도 나타나지 않는지 확인한다
- [ ] RAW 녹화를 시작·종료할 때 그래프가 크게 튀지 않는지 확인한다

두 번째 항목이 이 작업 전체의 목적이다. 나머지가 모두 통과해도 이것이 실패하면 목표를 달성하지 못한 것이다. **카메라가 연결된 상태에서만 가능하므로 사람이 직접 확인해야 한다.**

## 알려진 한계 — 사람에게 알려야 할 것

표본은 프레임의 약 4%(64/1544행)를 본다. 넓은 과다노출은 확실히 잡지만, **작은 정반사 하이라이트(예: 10×10 화소)는 표본에 걸리지 않아 놓칠 수 있다.** 스펙의 목적이 노출 건강 감시이므로 이 한계는 수용 가능하지만, 사용자가 "포화 0%"를 "어디에도 포화가 없음"으로 읽지 않도록 알려야 한다.
