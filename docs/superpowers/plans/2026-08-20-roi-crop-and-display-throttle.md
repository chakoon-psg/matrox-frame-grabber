# ROI 크롭 + 표시 스로틀 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 카메라 on-board ROI로 프레임당 페이로드를 줄여 3채널 184 fps 컬러가 호스트 DMA 천장 안에 들어오게 하고, 표시 갱신을 30 fps로 제한해 CPU를 확보한다.

**Architecture:** ROI 좌표 계산(짝수·증분 스냅, 대역폭 예산)은 MIL에 의존하지 않는 순수 값 타입 `ChannelRoi`로 분리해 단위 테스트한다. 하드웨어 적용은 `CameraChannel.ApplyRoi()`가 담당하고, 기존 `ReloadWithDcf()`와 같은 절차(정지 → `FreeCamera` → 변경 → `AllocateCamera` → 재개)를 따른다. 표시 스로틀은 두 레버 — MIL의 `M_UPDATE_RATE_MAX`와 훅의 `MbufCopy` 시간 기준 감축 — 를 함께 쓴다.

**Tech Stack:** C# / WPF / net10.0-windows / x64, MIL 10.70 (GenICam 정수 피처, `MdispControl`), xunit (신규 테스트 프로젝트)

**Spec:** [docs/superpowers/specs/2026-08-20-avn-anomaly-detection-design.md](../specs/2026-08-20-avn-anomaly-detection-design.md) — 2절(ROI), 5절(표시 경로)

## Global Constraints

- 대상 프레임워크 `net10.0-windows`, 플랫폼 `x64` 고정. 변경하지 않는다.
- 출력 exe: `src\bin\x64\Release\net10.0-windows\MatroxFrameGrabber.exe`
- 빌드: `dotnet build MatroxFrameGrabber.slnx -c Release` — **경고 0개, 오류 0개**를 유지한다. 현재 상태가 0/0이므로 새 경고는 이 작업이 만든 것이다.
- **호스트 DMA 천장: 약 1.7 GB/s (채널 공유).** 실측값. 경고 임계는 그 80% = 1.36 GB/s.
- **카메라 ROI는 원본 면적의 1/4 이하** (≈1030×770, 프레임당 2.3 MB, 3채널 184 fps = 1.25 GB/s).
- **`OffsetX/Y`, `Width/Height`는 짝수여야 한다.** Bayer CFA 위상이 어긋나면 색이 뒤집힌다. 하드웨어 증분은 `M_FEATURE_INCREMENT`로 조회해 그 배수로 스냅하되, **최소 2를 강제**한다.
- **이 GenICam 피처들은 정수형이다. `M_TYPE_MIL_INT`로 써야 한다** — `M_TYPE_DOUBLE`로 쓰면 예외 없이 조용히 무시된다.
- **`DecimationHorizontal`/`Vertical`과 `AcquisitionFrameRate`는 카메라에 영속된다.** 이 계획은 decimation을 쓰지 않지만(ROI를 쓴다), 실험 중 건드렸다면 1로 되돌릴 것.
- **`M_UPDATE_RATE_MAX`는 디스플레이당 스레드를 추가로 띄우고, 과포화 상태에서 한 채널을 붕괴시킨다.** 반드시 **ROI를 먼저 적용해 과포화를 없앤 뒤** 캡을 얹는다. Task 순서가 이 제약을 반영한다.
- 취득 훅(`OnGrabbedFrame`)의 프레임 예산은 184 fps에서 **5.4 ms**.
- 인수 기준: **3채널 grab에서 `M_PROCESS_FRAME_MISSED`가 증가하지 않는다.**

## File Structure

| 파일 | 책임 |
|---|---|
| `src/Infrastructure/ChannelRoi.cs` (신규) | ROI 좌표 값 타입. 스냅·클램프·대역폭 산술. **MIL 의존 없음** |
| `tests/MatroxFrameGrabber.Tests.csproj` (신규) | `ChannelRoi.cs`를 소스로 직접 포함(`<Compile Include>`)해 MIL 없이 테스트 |
| `tests/ChannelRoiTests.cs` (신규) | 위의 단위 테스트 |
| `src/Infrastructure/OutputSettings.cs` (수정) | 채널별 ROI 4개 + `DisplayUpdateFps` 영속화 |
| `src/Mil/GenICamFeatures.cs` (수정) | 정수 피처 읽기/쓰기 (`SetInt`, `TryGetInt`) |
| `src/Mil/CameraChannel.cs` (수정) | ROI 적용 + 재할당, 표시 스로틀 두 레버 |
| `src/Views/CameraPaneView.xaml(.cs)` (수정) | pane 설정에 ROI 입력 + Apply / Full |
| `src/Views/MainWindow.xaml` (수정) | ⚙ 팝업에 표시 fps 입력 |
| `CLAUDE.md` (수정) | 녹화 모드 표의 `● Rec` 소스 오기 수정 |

**테스트 프로젝트를 새로 만드는 이유:** 이 리포에는 테스트 프로젝트가 없고 기존 스펙들도 "확인 절차를 명시한다"로 갔다. 그 관례를 여기서만 깨는 이유는, ROI 스냅 산술이 **틀려도 화면에 즉시 드러나지 않는** 유일한 부분이기 때문이다. 홀수 offset은 색이 뒤집혀 눈에 보이지만, 증분 스냅이 1픽셀 어긋나거나 클램프가 경계에서 실패하는 것은 특정 입력에서만 나타난다. 하드웨어 없이 검증 가능한 순수 산술이므로 테스트 비용이 거의 없다. MIL을 끌어오지 않으려고 프로젝트 참조 대신 `<Compile Include>`로 파일만 가져온다.

> 이 결정을 원치 않으면 Task 1을 건너뛰고 Task 2로 가되, Task 8의 수동 확인에 경계값 케이스(홀수 좌표, 최대 크기 초과, 0 크기)를 추가할 것.

---

### Task 1: `ChannelRoi` 값 타입과 단위 테스트

**Files:**
- Create: `src/Infrastructure/ChannelRoi.cs`
- Create: `tests/MatroxFrameGrabber.Tests.csproj`
- Test: `tests/ChannelRoiTests.cs`
- Modify: `MatroxFrameGrabber.slnx`

**Interfaces:**
- Consumes: 없음 (이 계획의 첫 작업)
- Produces:
  - `readonly struct ChannelRoi` — `int OffsetX, OffsetY, Width, Height`
  - `bool ChannelRoi.IsFullFrame { get; }`
  - `ChannelRoi ChannelRoi.FullFrame { get; }` (static, 네 값이 모두 0)
  - `ChannelRoi ChannelRoi.Snap(int xInc, int yInc, int wInc, int hInc, int maxWidth, int maxHeight)`
  - `long ChannelRoi.BytesPerFrame(int bands, int maxWidth, int maxHeight)`
  - `static double ChannelRoi.HostDmaCeilingBytesPerSecond` (= 1.7e9)
  - `static double ChannelRoi.WarnBytesPerSecond` (= 1.36e9)

- [ ] **Step 1: 테스트 프로젝트를 만든다**

`tests/MatroxFrameGrabber.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <RootNamespace>MatroxFrameGrabber.Tests</RootNamespace>
  </PropertyGroup>

  <!-- Source-included, NOT a ProjectReference: referencing the app project would drag in the
       MIL NuGet packages (x64-only, and only present where MIL is installed). ChannelRoi has no
       MIL dependency, so pulling the single file in keeps these tests runnable anywhere. -->
  <ItemGroup>
    <Compile Include="..\src\Infrastructure\ChannelRoi.cs" LinkBase="Included" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  </ItemGroup>

</Project>
```

`MatroxFrameGrabber.slnx`에 프로젝트를 추가한다. 현재 내용을 열어 `src/MatroxFrameGrabber.csproj` 항목 옆에 같은 형식으로 `tests/MatroxFrameGrabber.Tests.csproj`를 넣는다.

- [ ] **Step 2: 실패하는 테스트를 쓴다**

`tests/ChannelRoiTests.cs`:

```csharp
using MatroxFrameGrabber.Infrastructure;
using Xunit;

namespace MatroxFrameGrabber.Tests
{
    public class ChannelRoiTests
    {
        const int MaxW = 2064;
        const int MaxH = 1544;

        [Fact]
        public void FullFrame_IsFullFrame()
        {
            Assert.True(ChannelRoi.FullFrame.IsFullFrame);
            Assert.True(new ChannelRoi(0, 0, 0, 0).IsFullFrame);
        }

        [Fact]
        public void ZeroOrNegativeSize_MeansFullFrame()
        {
            Assert.True(new ChannelRoi(100, 100, 0, 500).IsFullFrame);
            Assert.True(new ChannelRoi(100, 100, 500, -1).IsFullFrame);
        }

        [Fact]
        public void Snap_RoundsOddCoordinatesDownToEven()
        {
            // Increment 1 from the camera must still be forced to 2: an odd offset shifts the
            // Bayer CFA phase and the colours come out swapped.
            var snapped = new ChannelRoi(101, 203, 507, 609).Snap(1, 1, 1, 1, MaxW, MaxH);
            Assert.Equal(100, snapped.OffsetX);
            Assert.Equal(202, snapped.OffsetY);
            Assert.Equal(506, snapped.Width);
            Assert.Equal(608, snapped.Height);
        }

        [Fact]
        public void Snap_HonoursLargerHardwareIncrement()
        {
            var snapped = new ChannelRoi(100, 100, 1000, 1000).Snap(16, 8, 16, 8, MaxW, MaxH);
            Assert.Equal(96, snapped.OffsetX);    // 100 -> down to multiple of 16
            Assert.Equal(96, snapped.OffsetY);    // 100 -> down to multiple of 8
            Assert.Equal(992, snapped.Width);     // 1000 -> down to multiple of 16
            Assert.Equal(1000, snapped.Height);   // already a multiple of 8
        }

        [Fact]
        public void Snap_ClampsSizeSoRoiStaysInsideSensor()
        {
            var snapped = new ChannelRoi(2000, 1500, 500, 500).Snap(2, 2, 2, 2, MaxW, MaxH);
            Assert.True(snapped.OffsetX + snapped.Width <= MaxW);
            Assert.True(snapped.OffsetY + snapped.Height <= MaxH);
            Assert.Equal(64, snapped.Width);    // 2064 - 2000
            Assert.Equal(44, snapped.Height);   // 1544 - 1500
        }

        [Fact]
        public void Snap_ClampsNegativeOffsetToZero()
        {
            var snapped = new ChannelRoi(-50, -1, 800, 600).Snap(2, 2, 2, 2, MaxW, MaxH);
            Assert.Equal(0, snapped.OffsetX);
            Assert.Equal(0, snapped.OffsetY);
        }

        [Fact]
        public void Snap_LeavesFullFrameAlone()
        {
            var snapped = ChannelRoi.FullFrame.Snap(2, 2, 2, 2, MaxW, MaxH);
            Assert.True(snapped.IsFullFrame);
        }

        [Fact]
        public void Snap_OffsetPastSensorYieldsAtLeastOneIncrement()
        {
            // Degenerate input must not produce a zero or negative size, which MIL would reject.
            var snapped = new ChannelRoi(9999, 9999, 800, 600).Snap(2, 2, 2, 2, MaxW, MaxH);
            Assert.True(snapped.Width >= 2);
            Assert.True(snapped.Height >= 2);
            Assert.True(snapped.OffsetX + snapped.Width <= MaxW);
            Assert.True(snapped.OffsetY + snapped.Height <= MaxH);
        }

        [Fact]
        public void BytesPerFrame_FullFrameUsesSensorSize()
        {
            Assert.Equal(2064L * 1544 * 3, ChannelRoi.FullFrame.BytesPerFrame(3, MaxW, MaxH));
        }

        [Fact]
        public void BytesPerFrame_CroppedUsesRoiSize()
        {
            Assert.Equal(1024L * 772 * 3, new ChannelRoi(0, 0, 1024, 772).BytesPerFrame(3, MaxW, MaxH));
        }

        [Fact]
        public void MeasuredCeiling_RejectsThreeFullFrameColourChannelsAt184Fps()
        {
            // The measurement this whole plan exists for: 3 x 184 x 9.12 MB = 5.0 GB/s.
            double load = 3 * 184.0 * ChannelRoi.FullFrame.BytesPerFrame(3, MaxW, MaxH);
            Assert.True(load > ChannelRoi.HostDmaCeilingBytesPerSecond);
        }

        [Fact]
        public void MeasuredCeiling_AcceptsQuarterAreaColourAt184Fps()
        {
            var roi = new ChannelRoi(0, 0, 1030, 770);
            double load = 3 * 184.0 * roi.BytesPerFrame(3, MaxW, MaxH);
            Assert.True(load < ChannelRoi.WarnBytesPerSecond);
        }
    }
}
```

- [ ] **Step 3: 테스트가 실패하는 것을 확인한다**

Run: `dotnet test MatroxFrameGrabber.slnx -c Release`
Expected: 컴파일 실패 — `The type or namespace name 'ChannelRoi' could not be found`

- [ ] **Step 4: `ChannelRoi`를 구현한다**

`src/Infrastructure/ChannelRoi.cs`:

```csharp
using System;

namespace MatroxFrameGrabber.Infrastructure
{
    /// <summary>
    /// One channel's on-board acquisition ROI, in sensor pixels. Cropping on the camera is the
    /// only lever that reduces host DMA traffic — see research.md section 8: the acquisition
    /// ceiling is a shared ~1.7 GB/s and throttling the display does not move it.
    ///
    /// All four values zero means "no crop" (full sensor). This type holds no MIL dependency so
    /// the snapping arithmetic can be tested without hardware.
    /// </summary>
    public readonly struct ChannelRoi
    {
        public readonly int OffsetX;
        public readonly int OffsetY;
        public readonly int Width;
        public readonly int Height;

        /// <summary>Bayer CFA phase floor: an odd offset or size swaps the colours.</summary>
        private const int CfaIncrement = 2;

        /// <summary>Measured host DMA ceiling shared across channels (research.md section 8).</summary>
        public const double HostDmaCeilingBytesPerSecond = 1.7e9;

        /// <summary>80% of the ceiling — above this the UI warns rather than silently dropping frames.</summary>
        public const double WarnBytesPerSecond = 1.36e9;

        public ChannelRoi(int offsetX, int offsetY, int width, int height)
        {
            OffsetX = offsetX;
            OffsetY = offsetY;
            Width = width;
            Height = height;
        }

        public static ChannelRoi FullFrame => new ChannelRoi(0, 0, 0, 0);

        /// <summary>
        /// True when this means "use the whole sensor". A non-positive size counts as full frame
        /// rather than an error, so a half-filled settings file degrades to the safe default.
        /// </summary>
        public bool IsFullFrame => Width <= 0 || Height <= 0;

        /// <summary>
        /// Rounds this ROI onto the hardware's increment grid and clamps it inside the sensor.
        ///
        /// Offsets and sizes both round DOWN: rounding a size up could push the ROI past the
        /// sensor edge, and rounding an offset up would move the crop off the region the operator
        /// picked. The increments come from M_FEATURE_INCREMENT but are floored at 2 regardless of
        /// what the camera reports, because 1 would let an odd offset through.
        /// </summary>
        public ChannelRoi Snap(int xInc, int yInc, int wInc, int hInc, int maxWidth, int maxHeight)
        {
            if (IsFullFrame)
                return FullFrame;

            int ex = Math.Max(CfaIncrement, xInc);
            int ey = Math.Max(CfaIncrement, yInc);
            int ew = Math.Max(CfaIncrement, wInc);
            int eh = Math.Max(CfaIncrement, hInc);

            // Offset first: it bounds how much width is left. Leave at least one width increment.
            int x = RoundDown(Clamp(OffsetX, 0, Math.Max(0, maxWidth - ew)), ex);
            int y = RoundDown(Clamp(OffsetY, 0, Math.Max(0, maxHeight - eh)), ey);

            int w = RoundDown(Clamp(Width, ew, maxWidth - x), ew);
            int h = RoundDown(Clamp(Height, eh, maxHeight - y), eh);

            // Clamp above can leave a rounded-down size of zero when very little room remains.
            // MIL rejects a zero-size ROI, so give back one increment and pull the offset in.
            if (w < ew) { w = ew; x = RoundDown(Math.Max(0, maxWidth - ew), ex); }
            if (h < eh) { h = eh; y = RoundDown(Math.Max(0, maxHeight - eh), ey); }

            return new ChannelRoi(x, y, w, h);
        }

        /// <summary>
        /// Bytes one frame occupies for this ROI. <paramref name="bands"/> is 3 with on-board Bayer
        /// conversion enabled and 1 without — the 3x difference is what makes cropping necessary.
        /// </summary>
        public long BytesPerFrame(int bands, int maxWidth, int maxHeight)
        {
            long w = IsFullFrame ? maxWidth : Width;
            long h = IsFullFrame ? maxHeight : Height;
            return w * h * Math.Max(1, bands);
        }

        public override string ToString() =>
            IsFullFrame ? "full frame" : $"{Width}x{Height} @ {OffsetX},{OffsetY}";

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static int RoundDown(int v, int increment) => v - (v % increment);
    }
}
```

- [ ] **Step 5: 테스트가 통과하는 것을 확인한다**

Run: `dotnet test MatroxFrameGrabber.slnx -c Release`
Expected: PASS — 12개 통과, 실패 0

앱 빌드도 깨지지 않았는지 확인한다.

Run: `dotnet build MatroxFrameGrabber.slnx -c Release`
Expected: 경고 0개, 오류 0개

- [ ] **Step 6: 커밋**

```bash
git add src/Infrastructure/ChannelRoi.cs tests/ MatroxFrameGrabber.slnx
git commit -m "Add ChannelRoi, with the snapping rules under test

Cropping on the camera is the only lever that reduces host DMA traffic, and
the arithmetic that places the crop is the one part of it that can be wrong
without showing on screen. An odd offset swaps the Bayer colours visibly,
but an increment rounded the wrong way, or a clamp that fails at the sensor
edge, only misbehaves for particular inputs.

So this type holds no MIL dependency and the tests pull it in as a source
file rather than referencing the app project, which would drag the x64-only
MIL packages along. The repo has no test project until now; this is the
narrow case that earns one."
```

---

### Task 2: 정수 GenICam 피처 접근

**Files:**
- Modify: `src/Mil/GenICamFeatures.cs`

**Interfaces:**
- Consumes: 없음
- Produces:
  - `bool GenICamFeatures.SetInt(string name, long value)`
  - `bool GenICamFeatures.TryGetInt(long inquireType, string name, out long value)`

- [ ] **Step 1: 왜 필요한지 확인한다**

`Width`, `Height`, `OffsetX`, `OffsetY`는 GenICam **정수(IInteger)** 피처다. 기존 `SetDouble`은
`M_TYPE_DOUBLE`로 쓰는데, 정수 피처에 그렇게 쓰면 **예외도 없이 조용히 무시된다**(2026-08-20에
`DecimationHorizontal`로 실제로 겪었다 — 값이 1에서 바뀌지 않았고 오류도 없었다). `M_TYPE_MIL_INT`
경로가 따로 필요하다.

- [ ] **Step 2: `SetInt` / `TryGetInt`를 추가한다**

`src/Mil/GenICamFeatures.cs`의 `SetDouble` 바로 아래에 넣는다. 기존 메서드들과 같은 규율을 지킨다
— 존재 확인 + `try/catch(MILException)` + 실패 시 `false`.

```csharp
        /// <summary>
        /// Writes an integer feature (e.g. Width, OffsetX). Returns false if unavailable.
        ///
        /// Integer features MUST go through M_TYPE_MIL_INT. Writing one with M_TYPE_DOUBLE is
        /// silently ignored — no exception, no error, the value simply does not change.
        /// </summary>
        public bool SetInt(string name, long value)
        {
            if (!Available(name)) return false;
            try
            {
                MIL_INT v = value;
                MIL.MdigControlFeature(Digitizer, MIL.M_FEATURE_VALUE, name, MIL.M_TYPE_MIL_INT, ref v);
                return true;
            }
            catch (MILException) { return false; }
        }

        /// <summary>
        /// Reads an integer feature property (M_FEATURE_VALUE / _MIN / _MAX / _INCREMENT).
        /// </summary>
        public bool TryGetInt(long inquireType, string name, out long value)
        {
            value = 0;
            if (!HasDigitizer) return false;
            try
            {
                MIL_INT v = 0;
                MIL.MdigInquireFeature(Digitizer, inquireType, name, MIL.M_TYPE_MIL_INT, ref v);
                value = v;
                return true;
            }
            catch (MILException) { return false; }
        }
```

- [ ] **Step 3: 빌드가 통과하는 것을 확인한다**

Run: `dotnet build MatroxFrameGrabber.slnx -c Release`
Expected: 경고 0개, 오류 0개

- [ ] **Step 4: 커밋**

```bash
git add src/Mil/GenICamFeatures.cs
git commit -m "Read and write integer GenICam features

Width, Height and the offsets are IInteger features. Writing one through
the existing SetDouble does nothing at all — no exception, no error return,
the value just stays put. That cost an afternoon when DecimationHorizontal
refused to move, so the new methods say so where someone will read it."
```

---

### Task 3: 채널별 ROI와 표시 fps 영속화

**Files:**
- Modify: `src/Infrastructure/OutputSettings.cs`

**Interfaces:**
- Consumes: `ChannelRoi` (Task 1)
- Produces:
  - `ChannelRoi OutputSettings.GetRoi(int channelIndex)`
  - `void OutputSettings.SetRoi(int channelIndex, ChannelRoi roi)`
  - `int OutputSettings.DisplayUpdateFps { get; set; }` — 0 = 무제한, 그 외 5~120
  - `const int OutputSettings.ChannelCount = 4`

- [ ] **Step 1: 필드와 접근자를 추가한다**

`src/Infrastructure/OutputSettings.cs`의 필드 선언부(`private bool _loading;` 위)에 추가한다.

```csharp
        /// <summary>Acquisition slots on the board — always 4, camera present or not.</summary>
        public const int ChannelCount = 4;

        private readonly ChannelRoi[] _channelRois = new ChannelRoi[ChannelCount];
        private int _displayUpdateFps = 30;
```

`Resolution` 프로퍼티 아래에 추가한다.

```csharp
        /// <summary>
        /// Per-channel on-board acquisition ROI. Index is the channel's board slot (0-3).
        /// Defaults to <see cref="ChannelRoi.FullFrame"/>.
        /// </summary>
        public ChannelRoi GetRoi(int channelIndex) =>
            channelIndex < 0 || channelIndex >= ChannelCount
                ? ChannelRoi.FullFrame
                : _channelRois[channelIndex];

        // No RaiseChanged here: nothing binds the ROI through OutputSettings. The pane binds
        // CameraChannel.RoiInputX/Y/W/H, which CameraChannel raises after it applies the crop.
        public void SetRoi(int channelIndex, ChannelRoi roi)
        {
            if (channelIndex < 0 || channelIndex >= ChannelCount) return;
            _channelRois[channelIndex] = roi;
            Save();
        }

        /// <summary>
        /// Cap for the MIL display's update rate, in frames per second. 0 = uncapped.
        ///
        /// This does NOT recover acquisition frame rate — measured, see research.md section 8 —
        /// it buys back CPU. Clamped to 5..120 when non-zero so a typo cannot make the preview
        /// look frozen or remove the cap entirely.
        /// </summary>
        public int DisplayUpdateFps
        {
            get => _displayUpdateFps;
            set
            {
                int v = value <= 0 ? 0 : (value < 5 ? 5 : (value > 120 ? 120 : value));
                if (_displayUpdateFps != v) { _displayUpdateFps = v; RaiseChanged(nameof(DisplayUpdateFps)); Save(); }
            }
        }
```

- [ ] **Step 2: DTO에 필드를 추가한다**

`private class Dto` 안에 추가한다. `ChannelRoi`는 읽기 전용 구조체라 `System.Text.Json`이 역직렬화하지
못하므로 평범한 DTO를 하나 더 둔다.

```csharp
            public RoiDto[] ChannelRois { get; set; }
            public int DisplayUpdateFps { get; set; } = 30;
```

`Dto` 클래스 바로 아래에 추가한다.

```csharp
        // ChannelRoi is a readonly struct with no parameterless constructor, so it cannot be
        // deserialized directly. This mirror type exists only for the settings file.
        private class RoiDto
        {
            public int OffsetX { get; set; }
            public int OffsetY { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }
```

- [ ] **Step 3: `Load()`에 읽기를 추가한다**

`s._rawScratchFolder = ...;` 줄 바로 뒤에 넣는다.

```csharp
                        s._displayUpdateFps = dto.DisplayUpdateFps <= 0
                            ? 0
                            : (dto.DisplayUpdateFps < 5 ? 5 : (dto.DisplayUpdateFps > 120 ? 120 : dto.DisplayUpdateFps));

                        // A settings file written by an older build has no ROI array, and one
                        // written by hand may have the wrong length. Both degrade to full frame
                        // per channel rather than throwing.
                        if (dto.ChannelRois != null)
                        {
                            for (int i = 0; i < ChannelCount && i < dto.ChannelRois.Length; i++)
                            {
                                RoiDto r = dto.ChannelRois[i];
                                if (r == null) continue;
                                s._channelRois[i] = new ChannelRoi(r.OffsetX, r.OffsetY, r.Width, r.Height);
                            }
                        }
```

- [ ] **Step 4: `Save()`에 쓰기를 추가한다**

`var dto = new Dto { ... }` 를 다음으로 바꾼다 (기존 필드는 그대로 두고 두 개를 더한다).

```csharp
                var rois = new RoiDto[ChannelCount];
                for (int i = 0; i < ChannelCount; i++)
                {
                    rois[i] = new RoiDto
                    {
                        OffsetX = _channelRois[i].OffsetX,
                        OffsetY = _channelRois[i].OffsetY,
                        Width = _channelRois[i].Width,
                        Height = _channelRois[i].Height
                    };
                }
                var dto = new Dto
                {
                    OutputFolder = _outputFolder,
                    Resolution = _resolution,
                    FfmpegPath = _ffmpegPath,
                    RawDurationSeconds = _rawDurationSeconds,
                    RawSegmentSeconds = _rawSegmentSeconds,
                    RawScratchFolder = _rawScratchFolder,
                    ChannelRois = rois,
                    DisplayUpdateFps = _displayUpdateFps
                };
```

- [ ] **Step 5: 왕복을 손으로 확인한다**

앱을 한 번 실행하고 닫은 뒤 설정 파일을 본다.

Run: `cat "$LOCALAPPDATA/MatroxFrameGrabber/settings.json"`
Expected: `ChannelRois` 배열이 4개 항목(전부 0)이고 `DisplayUpdateFps: 30`이다.

기존 설정 파일이 있던 상태에서도 예외 없이 로드되는지(하위 호환) 함께 확인한다.

- [ ] **Step 6: 커밋**

```bash
git add src/Infrastructure/OutputSettings.cs
git commit -m "Persist a per-channel ROI and the display rate cap

The ROI has to survive a restart or the operator re-enters four rectangles
every morning. ChannelRoi is a readonly struct so it needs a mirror DTO to
deserialize; a settings file from an older build simply has no ROI array,
and that reads back as full frame rather than throwing."
```

---

### Task 4: `CameraChannel`에 ROI 적용

**Files:**
- Modify: `src/Mil/CameraChannel.cs` — 피처 이름 상수, `AllocateCamera`, 새 `ApplyRoi`/`Roi`

**Interfaces:**
- Consumes: `ChannelRoi` (Task 1), `GenICamFeatures.SetInt`/`TryGetInt` (Task 2), `OutputSettings.GetRoi`/`SetRoi` (Task 3)
- Produces:
  - `ChannelRoi CameraChannel.Roi { get; }` — 현재 적용된(스냅된) ROI
  - `string CameraChannel.RoiHint { get; }` — `"max 2064x1544 · step 2"` 같은 UI 힌트
  - `bool CameraChannel.ApplyRoi(ChannelRoi requested)` — 스냅·적용·재할당. 성공하면 true
  - `bool CameraChannel.ClearRoi()` — 전체 프레임으로 되돌린다
  - `string CameraChannel.RoiInputX/Y/W/H { get; set; }` — 입력 상자 바인딩용 문자열

- [ ] **Step 1: 피처 이름 상수를 추가한다**

`src/Mil/CameraChannel.cs`의 `F_BALANCE_RATIO` 아래에 넣는다.

```csharp
        private const string F_WIDTH = "Width";
        private const string F_HEIGHT = "Height";
        private const string F_WIDTH_MAX = "WidthMax";
        private const string F_HEIGHT_MAX = "HeightMax";
        private const string F_OFFSET_X = "OffsetX";
        private const string F_OFFSET_Y = "OffsetY";
```

- [ ] **Step 2: 상태와 프로퍼티를 추가한다**

`_features` 선언 근처에 필드를 넣는다.

```csharp
        private ChannelRoi _roi = ChannelRoi.FullFrame;
        private long _sensorMaxW, _sensorMaxH;
        private int _roiIncX = 2, _roiIncY = 2, _roiIncW = 2, _roiIncH = 2;
        private string _roiInputX = "0", _roiInputY = "0", _roiInputW = "0", _roiInputH = "0";
```

`AcqRateHint` 아래에 프로퍼티를 넣는다.

```csharp
        /// <summary>The ROI currently applied on the camera (already snapped to the hardware grid).</summary>
        public ChannelRoi Roi => _roi;

        /// <summary>True when the camera exposes a settable acquisition ROI.</summary>
        public bool SupportsRoi => FeatureAvailable(F_WIDTH) && FeatureAvailable(F_OFFSET_X);

        /// <summary>Sensor bounds and step, for the ROI input hint.</summary>
        public string RoiHint => _sensorMaxW > 0
            ? $"max {_sensorMaxW}x{_sensorMaxH} · step {_roiIncW}"
            : "no camera";

        public string RoiInputX { get => _roiInputX; set { _roiInputX = value; RaisePropertyChanged(nameof(RoiInputX)); } }
        public string RoiInputY { get => _roiInputY; set { _roiInputY = value; RaisePropertyChanged(nameof(RoiInputY)); } }
        public string RoiInputW { get => _roiInputW; set { _roiInputW = value; RaisePropertyChanged(nameof(RoiInputW)); } }
        public string RoiInputH { get => _roiInputH; set { _roiInputH = value; RaisePropertyChanged(nameof(RoiInputH)); } }
```

- [ ] **Step 3: ROI를 카메라에 쓰는 내부 메서드를 추가한다**

`#region Feature helpers` 바로 위에 넣는다.

```csharp
        /// <summary>
        /// Reads the sensor bounds and increments so a requested ROI can be snapped to them.
        ///
        /// Prefers the WidthMax/HeightMax features over Width's own M_FEATURE_MAX. Width's maximum
        /// is offset-dependent — with OffsetX already at 1500 it reports what is left of the row,
        /// not the sensor — so reading it while a previous crop is still applied would shrink the
        /// bounds a little more every time a ROI is set. WidthMax/HeightMax are sensor constants.
        /// This camera exposes both (verified 2026-08-20: 2064 x 1544).
        /// </summary>
        private void RefreshRoiBounds()
        {
            if (!_features.TryGetInt(MIL.M_FEATURE_VALUE, F_WIDTH_MAX, out long maxW) || maxW <= 0)
                if (!_features.TryGetInt(MIL.M_FEATURE_MAX, F_WIDTH, out maxW) || maxW <= 0)
                    _features.TryGetInt(MIL.M_FEATURE_VALUE, F_WIDTH, out maxW);
            if (!_features.TryGetInt(MIL.M_FEATURE_VALUE, F_HEIGHT_MAX, out long maxH) || maxH <= 0)
                if (!_features.TryGetInt(MIL.M_FEATURE_MAX, F_HEIGHT, out maxH) || maxH <= 0)
                    _features.TryGetInt(MIL.M_FEATURE_VALUE, F_HEIGHT, out maxH);
            _sensorMaxW = maxW;
            _sensorMaxH = maxH;

            _roiIncX = ReadIncrement(F_OFFSET_X);
            _roiIncY = ReadIncrement(F_OFFSET_Y);
            _roiIncW = ReadIncrement(F_WIDTH);
            _roiIncH = ReadIncrement(F_HEIGHT);
        }

        private int ReadIncrement(string feature) =>
            _features.TryGetInt(MIL.M_FEATURE_INCREMENT, feature, out long inc) && inc > 0
                ? (int)inc
                : 2;   // ChannelRoi.Snap floors this at 2 anyway; 2 is just the honest default

        /// <summary>
        /// Writes <see cref="_roi"/> to the camera. Called from AllocateCamera BEFORE
        /// AllocateBuffers, so M_SIZE_X/M_SIZE_Y are inquired after the ROI has taken effect.
        ///
        /// The write order matters and is not negotiable: offsets go to zero first, then the
        /// sizes, then the real offsets. Setting a size while an old offset is still in place is
        /// rejected whenever offset+size would exceed the sensor, which is exactly what happens
        /// when moving from a small far-right crop to a large one.
        /// </summary>
        private void WriteRoiToCamera()
        {
            if (_digId == MIL.M_NULL || !SupportsRoi)
                return;

            RefreshRoiBounds();
            if (_sensorMaxW <= 0 || _sensorMaxH <= 0)
                return;

            _features.SetInt(F_OFFSET_X, 0);
            _features.SetInt(F_OFFSET_Y, 0);

            if (_roi.IsFullFrame)
            {
                _features.SetInt(F_WIDTH, _sensorMaxW);
                _features.SetInt(F_HEIGHT, _sensorMaxH);
                return;
            }

            ChannelRoi snapped = _roi.Snap(_roiIncX, _roiIncY, _roiIncW, _roiIncH,
                                           (int)_sensorMaxW, (int)_sensorMaxH);
            _roi = snapped;

            _features.SetInt(F_WIDTH, snapped.Width);
            _features.SetInt(F_HEIGHT, snapped.Height);
            _features.SetInt(F_OFFSET_X, snapped.OffsetX);
            _features.SetInt(F_OFFSET_Y, snapped.OffsetY);
        }
```

- [ ] **Step 4: `AllocateCamera`에서 ROI를 적용한다**

`src/Mil/CameraChannel.cs`의 `AllocateCamera` 안, `M_BAYER_CONVERSION`을 다시 켠 블록과
`CanRecord = ...` 사이에 넣는다. **`AllocateBuffers` 호출보다 앞이어야 한다** — 버퍼 크기가
`M_SIZE_X`/`M_SIZE_Y` 조회에서 나오기 때문이다.

```csharp
                    // ROI before AllocateBuffers: the buffer sizes come from M_SIZE_X/M_SIZE_Y,
                    // which only reflect the crop once it is written. Cropping here is what makes
                    // 3 colour channels at 184 fps fit the host DMA ceiling at all.
                    _roi = Output?.GetRoi(_index) ?? ChannelRoi.FullFrame;
                    WriteRoiToCamera();
```

- [ ] **Step 5: 공개 적용 메서드를 추가한다**

`ReloadWithDcf` 바로 아래에 넣는다. 절차를 그대로 따른다.

```csharp
        /// <summary>
        /// Applies a new acquisition ROI: snaps it to the hardware grid, persists it, and
        /// reallocates the digitizer and buffers. Resumes grabbing if it was active.
        ///
        /// Reallocation is unavoidable — the buffers are sized from the payload, so a crop that
        /// did not resize them would leave MIL writing a smaller frame into a larger buffer.
        /// Mirrors <see cref="ReloadWithDcf"/>, which solves the same problem for the DCF.
        /// </summary>
        public bool ApplyRoi(ChannelRoi requested)
        {
            if (!CameraPresent || !SupportsRoi)
                return false;

            RefreshRoiBounds();
            ChannelRoi snapped = requested.IsFullFrame
                ? ChannelRoi.FullFrame
                : requested.Snap(_roiIncX, _roiIncY, _roiIncW, _roiIncH,
                                 (int)_sensorMaxW, (int)_sensorMaxH);

            bool wasGrabbing = _isGrabbing;
            if (wasGrabbing)
                StopGrab();

            Output?.SetRoi(_index, snapped);

            FreeCamera();
            _cameraAvailable = true;
            AllocateCamera();          // re-reads the ROI from Output and writes it to the camera

            if (wasGrabbing)
                TryStartGrab();

            RaisePropertyChanged(nameof(DisplayId));
            RaisePropertyChanged(nameof(Roi));
            RaisePropertyChanged(nameof(RoiHint));
            SyncRoiInputs();
            return CameraPresent;
        }

        /// <summary>Returns to the full sensor.</summary>
        public bool ClearRoi() => ApplyRoi(ChannelRoi.FullFrame);

        /// <summary>Parses the four input boxes and applies them. False if any is not a number.</summary>
        public bool ApplyRoiFromInputs()
        {
            if (!int.TryParse(_roiInputX, out int x) || !int.TryParse(_roiInputY, out int y) ||
                !int.TryParse(_roiInputW, out int w) || !int.TryParse(_roiInputH, out int h))
                return false;
            return ApplyRoi(new ChannelRoi(x, y, w, h));
        }

        /// <summary>Writes the applied (snapped) ROI back into the input boxes, so the operator
        /// sees what the hardware actually took rather than what they typed.</summary>
        private void SyncRoiInputs()
        {
            RoiInputX = _roi.OffsetX.ToString(CultureInfo.InvariantCulture);
            RoiInputY = _roi.OffsetY.ToString(CultureInfo.InvariantCulture);
            RoiInputW = (_roi.IsFullFrame ? 0 : _roi.Width).ToString(CultureInfo.InvariantCulture);
            RoiInputH = (_roi.IsFullFrame ? 0 : _roi.Height).ToString(CultureInfo.InvariantCulture);
        }
```

`RefreshFeatureState()` 끝에 `SyncRoiInputs();`와 `RaisePropertyChanged(nameof(SupportsRoi));`,
`RaisePropertyChanged(nameof(RoiHint));`를 추가해 최초 할당 후에도 입력 상자가 채워지게 한다.

- [ ] **Step 6: 빌드하고 실기로 확인한다**

Run: `dotnet build MatroxFrameGrabber.slnx -c Release`
Expected: 경고 0개, 오류 0개

이 시점에는 UI가 없으므로 설정 파일로 확인한다. 앱을 닫고 `settings.json`의
`ChannelRois[0]`을 `{"OffsetX":512,"OffsetY":384,"Width":1024,"Height":772}`로 손으로 고친 뒤
앱을 실행한다.

Expected: CAM0 pane의 영상이 센서 중앙 부분만 보이고, **색이 정상**이다(타일처럼 깨지거나 색이
뒤집히면 CFA 위상이 어긋난 것 — 짝수 스냅을 다시 볼 것). `Fit`이 새 크기에 맞춰 다시 맞춰진다.

- [ ] **Step 7: 커밋**

```bash
git add src/Mil/CameraChannel.cs
git commit -m "Crop on the camera, and reallocate when the crop changes

The ROI is written before AllocateBuffers because the buffer sizes come
from M_SIZE_X/M_SIZE_Y, which only reflect the crop once it is set. The
write order inside WriteRoiToCamera is likewise not a style choice: offsets
to zero, then sizes, then the real offsets. Setting a size while an old
offset still stands is rejected whenever the two would overrun the sensor,
which is precisely what happens moving from a small right-hand crop to a
large one.

ApplyRoi follows ReloadWithDcf step for step, since a crop and a DCF change
invalidate the same buffers for the same reason. It writes the snapped
rectangle back into the input boxes so the operator sees what the hardware
took, not what they asked for."
```

---

### Task 5: 표시 스로틀 — `M_UPDATE_RATE_MAX`

**Files:**
- Modify: `src/Mil/CameraChannel.cs` — `AllocateBuffers`의 `MdispControl` 블록

**Interfaces:**
- Consumes: `OutputSettings.DisplayUpdateFps` (Task 3)
- Produces: `void CameraChannel.ApplyDisplayUpdateCap()` — 재할당 없이 캡을 다시 적용

- [ ] **Step 1: 순서 제약을 확인한다**

**Task 4가 먼저 끝나 있어야 한다.** `M_UPDATE_RATE_MAX`는 디스플레이당 MIL 스레드를 추가로 띄우고,
대역폭이 과포화인 상태(원본 해상도 컬러 3채널)에서는 한 채널이 2.3 fps로 붕괴하는 것이 관측되었다.
ROI로 과포화를 없앤 뒤에 캡을 얹는다.

- [ ] **Step 2: 캡을 적용하는 메서드를 추가한다**

`FitToWindow()` 위에 넣는다.

```csharp
        /// <summary>
        /// Caps the MIL display's update rate at OutputSettings.DisplayUpdateFps (0 = uncapped).
        ///
        /// This buys CPU, not frame rate: with the display switched off entirely the aggregate
        /// acquisition rate did not move, because the ceiling is board/PCIe DMA rather than host
        /// memory bandwidth (research.md section 8). Apply it only once the ROI has taken the
        /// load off — under oversubscription the extra display threads starve a channel.
        /// </summary>
        public void ApplyDisplayUpdateCap()
        {
            if (_dispId == MIL.M_NULL)
                return;
            int fps = Output?.DisplayUpdateFps ?? 0;
            try
            {
                MIL.MdispControl(_dispId, MIL.M_UPDATE_RATE_MAX,
                    fps > 0 ? (double)fps : MIL.M_MAX_REFRESH_RATE);
            }
            catch (MILException)
            {
                // An older board/driver may not support the control. An uncapped display is a
                // performance regression, not a failure — never take the app down for it.
            }
        }
```

- [ ] **Step 3: 할당 시 호출한다**

`AllocateBuffers`의 `MIL.MdispControl(_dispId, MIL.M_SCALE_DISPLAY, MIL.M_ONCE);` 바로 뒤에 넣는다.

```csharp
            ApplyDisplayUpdateCap();
```

- [ ] **Step 4: 실기로 확인한다**

Run: `dotnet build MatroxFrameGrabber.slnx -c Release` → 0/0

앱을 실행해 3채널을 시작하고 작업 관리자에서 프로세스 CPU를 본다. 그 뒤 `settings.json`의
`DisplayUpdateFps`를 `0`으로 바꿔 다시 실행하고 비교한다.

Expected: 30일 때 CPU가 눈에 띄게 낮다(실측 기준 1.29 → 0.78 코어). 프리뷰가 여전히 부드럽고,
pane의 `fps` 수치(취득 레이트)는 두 경우가 비슷하다 — 캡은 취득을 바꾸지 않는다.

- [ ] **Step 5: 커밋**

```bash
git add src/Mil/CameraChannel.cs
git commit -m "Cap the display update rate

Every grabbed frame currently triggers a display update, because the hook
copies into a buffer that is MdispSelect'ed; the measured display rate
tracked the acquisition rate exactly. Nobody reads 184 fps, so the cap
gives back most of a core.

It gives back no frames, though. With the display disabled outright the
aggregate acquisition rate was unchanged, so the ceiling is board DMA and
not host memory bandwidth. Ordering matters for a second reason: the cap
spawns another MIL thread per display, and under oversubscription that was
enough to starve a channel down to 2 fps. The ROI has to come first."
```

---

### Task 6: 표시 스로틀 — 훅의 `MbufCopy` 감축

**Files:**
- Modify: `src/Mil/CameraChannel.cs` — `OnGrabbedFrame`
- Modify: `CLAUDE.md` — 녹화 모드 표의 `● Rec` 소스 오기

**Interfaces:**
- Consumes: `OutputSettings.DisplayUpdateFps` (Task 3)
- Produces: 없음 (내부 동작)

- [ ] **Step 1: 라이브 녹화가 영향받지 않음을 확인한다**

`src/Mil/RecordingSession.cs:134`의 `Feed(MIL_ID grabbedBuffer)`를 읽는다. **grab 버퍼만** 쓰고
디스플레이 버퍼는 건드리지 않는다(`MimResize`/`MimShift`/`MbufCopy`의 원본이 모두
`grabbedBuffer`다). 따라서 표시 복사를 건너뛰어도 녹화 프레임은 하나도 잃지 않는다.

`CLAUDE.md`의 녹화 모드 표가 `● Rec`의 소스를 "디스플레이 버퍼"라고 적어 두었는데 이는 오기다.
코드는 grab 버퍼를 넘긴다. 이 작업의 안전성 논거가 그 표에 걸려 있으므로 함께 고친다.

`CLAUDE.md`의 해당 행을 찾아 바꾼다.

```
| 소스 | 디스플레이 버퍼 (3밴드 컬러) | grab 버퍼 (1밴드 Bayer) |
```
→
```
| 소스 | grab 버퍼 (3밴드 컬러) | grab 버퍼 (1밴드 Bayer) |
```

- [ ] **Step 2: 감축 상태를 추가한다**

`_roi` 필드 근처에 넣는다.

```csharp
        // Display-copy decimation. Time-based rather than every-Nth-frame: channels can grab at
        // very different rates (a long exposure caps one camera at 10 fps while its neighbours
        // run at 184), and a fixed divider would give each of them a different display rate.
        private readonly Stopwatch _dispClock = Stopwatch.StartNew();
        private long _lastDispCopyMs;
```

`System.Diagnostics`는 이미 `using`에 있다.

- [ ] **Step 3: `OnGrabbedFrame`에서 복사를 감축한다**

`// ---- Per-frame processing / display update ----` 블록을 다음으로 바꾼다.

```csharp
            // ---- Per-frame processing / display update ----
            // The display copy is 2.3 MB (cropped colour) and triggers a UI-thread update, so it
            // runs at DisplayUpdateFps rather than every frame. Recording is unaffected:
            // RecordingSession.Feed works from the grab buffer, never the display buffer.
            int dispFps = Output?.DisplayUpdateFps ?? 0;
            bool copyToDisplay = true;
            if (dispFps > 0)
            {
                long now = _dispClock.ElapsedMilliseconds;
                long interval = 1000 / dispFps;
                if (now - _lastDispCopyMs < interval)
                    copyToDisplay = false;
                else
                    _lastDispCopyMs = now;
            }
            if (copyToDisplay)
                MIL.MbufCopy(grabbedBuffer, displayBuffer);

            // ---- Recording feed (RecordingSession guards start/stop vs feed internally) ----
            _recording?.Feed(grabbedBuffer);
```

**RAW 경로는 손대지 않는다.** 위쪽 `if (seg != null)` 블록이 이미 `RAW_DISPLAY_EVERY`로 자기
감축을 하고 그 뒤 `return`한다.

- [ ] **Step 4: 실기로 확인한다**

Run: `dotnet build MatroxFrameGrabber.slnx -c Release` → 0/0

앱을 실행해 3채널을 시작한다.

Expected:
1. 프리뷰가 부드럽게 움직인다(30 fps).
2. pane의 취득 fps가 Task 5 때와 같다 — 감축이 취득을 바꾸지 않는다.
3. CPU가 Task 5보다 더 낮다(실측 기준 0.78 → 0.58 코어).
4. **`● Rec` 라이브 녹화를 10초 돌려 결과 mp4의 프레임 수를 확인한다.** 감축된 30 fps가 아니라
   취득 레이트에 해당하는 프레임 수여야 한다. 이것이 Step 1의 논거를 실제로 검증하는 항목이다.

Run: `ffmpeg -i <출력.mp4> -map 0:v:0 -c copy -f null - 2>&1 | grep frame=`

- [ ] **Step 5: 커밋**

```bash
git add src/Mil/CameraChannel.cs CLAUDE.md
git commit -m "Skip the display copy on frames nobody will see

The copy is megabytes and it drags a UI-thread update behind it, so doing
it 184 times a second to feed a 30 fps preview is waste. It is timed rather
than every-Nth-frame on purpose: one camera here runs at 10 fps because of
a long exposure while its neighbours run at 184, and a fixed divider would
hand each of them a different preview rate.

Live recording is untouched — RecordingSession.Feed works from the grab
buffer. CLAUDE.md claimed it read the display buffer, which would have made
this change quietly drop recorded frames. The table was wrong; the code was
always right."
```

---

### Task 7: pane UI — ROI 입력, 그리고 ⚙ 팝업의 표시 fps

**Files:**
- Modify: `src/Views/CameraPaneView.xaml` — Settings expander에 ROI 행 추가
- Modify: `src/Views/CameraPaneView.xaml.cs` — click 핸들러 2개
- Modify: `src/Views/MainWindow.xaml` — ⚙ 팝업에 표시 fps 입력

**Interfaces:**
- Consumes: `CameraChannel.RoiInputX/Y/W/H`, `ApplyRoiFromInputs()`, `ClearRoi()`, `SupportsRoi`, `RoiHint` (Task 4); `OutputSettings.DisplayUpdateFps` (Task 3)
- Produces: 없음 (UI 말단)

- [ ] **Step 1: pane 설정에 ROI 행을 추가한다**

`src/Views/CameraPaneView.xaml`의 White balance 행 뒤, Settings `StackPanel` 안에 넣는다.

```xml
                    <!-- Acquisition ROI. Cropping on the camera is what keeps three colour
                         channels at 184 fps inside the host DMA ceiling; 0 width or height
                         means the full sensor. -->
                    <StackPanel Orientation="Horizontal" Margin="0,0,0,3">
                        <Label Content="ROI" Width="34" />
                        <TextBox Width="44" FontSize="11" VerticalContentAlignment="Center"
                                 Text="{Binding RoiInputX, UpdateSourceTrigger=PropertyChanged}"
                                 IsEnabled="{Binding SupportsRoi}" ToolTip="OffsetX" />
                        <TextBox Width="44" Margin="2,0,0,0" FontSize="11" VerticalContentAlignment="Center"
                                 Text="{Binding RoiInputY, UpdateSourceTrigger=PropertyChanged}"
                                 IsEnabled="{Binding SupportsRoi}" ToolTip="OffsetY" />
                        <TextBlock Text="+" Margin="3,0" VerticalAlignment="Center" Foreground="#9A9A9A" />
                        <TextBox Width="50" FontSize="11" VerticalContentAlignment="Center"
                                 Text="{Binding RoiInputW, UpdateSourceTrigger=PropertyChanged}"
                                 IsEnabled="{Binding SupportsRoi}" ToolTip="Width (0 = full sensor)" />
                        <TextBox Width="50" Margin="2,0,0,0" FontSize="11" VerticalContentAlignment="Center"
                                 Text="{Binding RoiInputH, UpdateSourceTrigger=PropertyChanged}"
                                 IsEnabled="{Binding SupportsRoi}" ToolTip="Height (0 = full sensor)" />
                        <TextBlock Text="{Binding RoiHint}" Margin="4,0,6,0"
                                   VerticalAlignment="Center" Foreground="#9A9A9A" FontSize="10" />
                        <Button Content="Apply" Click="ApplyRoi_Click" IsEnabled="{Binding SupportsRoi}" />
                        <Button Content="Full" Margin="2,0,0,0" Click="ClearRoi_Click"
                                IsEnabled="{Binding SupportsRoi}"
                                ToolTip="Back to the full sensor" />
                    </StackPanel>
```

- [ ] **Step 2: click 핸들러를 추가한다**

`src/Views/CameraPaneView.xaml.cs`의 `ApplyAcqRate_Click` 아래에 넣는다. 기존 핸들러와 같은
형태를 지킨다.

```csharp
        private void ApplyRoi_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;
            // Reallocating the digitizer stops and restarts the grab, so a mistyped value costs
            // a visible hiccup — say what went wrong rather than failing silently.
            if (!channel.ApplyRoiFromInputs())
                MessageBox.Show("ROI를 적용하지 못했습니다. 네 값이 모두 정수여야 하고, 카메라가 ROI를 지원해야 합니다.",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ClearRoi_Click(object sender, RoutedEventArgs e)
        {
            var channel = Channel;
            if (channel == null) return;
            if (!channel.ClearRoi())
                MessageBox.Show("전체 프레임으로 되돌리지 못했습니다.",
                    channel.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
```

- [ ] **Step 3: ⚙ 팝업에 표시 fps 입력을 추가한다**

`src/Views/MainWindow.xaml`의 녹화 설정 `Popup` 안 `Grid`에 행을 하나 더 넣는다.
`Grid.RowDefinitions`(현재 `MainWindow.xaml:63-68`, `Auto` 4개)에 다섯 번째
`<RowDefinition Height="Auto" />`를 추가하고, `ffmpeg` 행(`Grid.Row="3"`) 아래에 `Grid.Row="4"`로
넣는다.

```xml
                                    <TextBlock Grid.Row="4" Grid.Column="0" Text="표시 fps"
                                               VerticalAlignment="Center" Margin="0,7,12,0" Foreground="#D0D0D0" />
                                    <StackPanel Grid.Row="4" Grid.Column="1" Orientation="Horizontal" Margin="0,7,0,0">
                                        <TextBox Width="46" FontSize="11" VerticalContentAlignment="Center"
                                                 Text="{Binding Output.DisplayUpdateFps, UpdateSourceTrigger=LostFocus}" />
                                        <TextBlock Text="0 = 무제한 · 취득 fps와 무관"
                                                   Margin="6,0,0,0" VerticalAlignment="Center"
                                                   Foreground="{StaticResource MutedTextBrush}" FontSize="10" />
                                    </StackPanel>
```

`UpdateSourceTrigger=LostFocus`인 이유: `PropertyChanged`면 한 글자마다 `Save()`가 돌아 설정
파일을 다시 쓴다(`research.md` 6절이 이미 이 문제를 지적하고 있다).

- [ ] **Step 4: 실기로 확인한다**

Run: `dotnet build MatroxFrameGrabber.slnx -c Release` → 0/0

앱을 실행한다.

Expected:
1. CAM0 Settings를 펼치면 ROI 행이 보이고 `max 2064x1544 · step 2` 힌트가 있다.
2. `512 384 + 1024 772` 입력 후 Apply → 영상이 중앙만 보이고 **색이 정상**이다. 입력 상자가
   스냅된 값으로 갱신된다.
3. 홀수를 넣어도(`513 385 + 1025 773`) 짝수로 스냅되어 색이 정상이다.
4. 센서를 넘는 값(`2000 1500 + 800 600`)을 넣어도 예외 없이 클램프된다.
5. `Full` → 전체 프레임으로 돌아온다.
6. 앱을 닫고 다시 열면 ROI가 유지된다.
7. ⚙ 팝업의 표시 fps를 30 ↔ 0으로 바꾸면 CPU가 달라진다.
8. CAM3(카메라 없음)의 ROI 입력이 비활성이다.

- [ ] **Step 5: 커밋**

```bash
git add src/Views/CameraPaneView.xaml src/Views/CameraPaneView.xaml.cs src/Views/MainWindow.xaml
git commit -m "Let the operator set the crop and the preview rate

Four numbers per pane, which is the least that is reproducible and the most
that can be reviewed. Drag-selection on the live preview and automatic
screen detection are the obvious next steps, and the design doc keeps them
out of scope for now; the coordinates and the reallocation path they will
need already exist.

The display-fps box commits on lost focus rather than per keystroke,
because the setter saves the settings file and research.md already flags
that pattern elsewhere in this popup."
```

---

### Task 8: 인수 검증 — 대역폭과 유실 프레임

**Files:** 없음 (검증 전용). 실패하면 그 원인을 고치는 커밋이 나온다.

**Interfaces:**
- Consumes: Task 1-7 전부
- Produces: 없음

- [ ] **Step 1: 유실 프레임을 확인한다 — 이것이 합격선이다**

스트립의 진단 툴팁(`MainWindow.xaml.cs`의 `BrightnessStrip.ToolTip`)은 밝기 측정 시간만 보여주고
유실은 보여주지 않는다. `StatusText`가 RAW 중에만 유실을 노출한다. 이 검증에서는 pane의 프레임
수를 직접 쓴다.

세 채널의 노출을 5388 µs로 맞춘다(Camera 1이 100000 µs면 10 fps에 묶인다 — pane의 `Exp`에
`5388` 입력 후 Apply). 세 채널에 ROI `1024x772`를 적용하고 `Start All` 후 60초 둔다.

Expected: 세 pane 모두 **184.x fps**이고 `(N frames)`가 서로 **거의 같다**(약 11000). 한 채널만
낮으면 그 채널의 노출이나 ROI가 다른 것이다.

- [ ] **Step 2: 대역폭 산술을 확인한다**

세 채널의 프레임당 바이트를 계산한다: `1024 × 772 × 3 = 2.26 MB`. 합계
`3 × 184 × 2.26 MB = 1.25 GB/s`. `ChannelRoi.WarnBytesPerSecond`(1.36 GB/s) 아래이므로 통과.

ROI를 `1460x1090`(원본의 약 1/2)으로 올려 같은 시험을 반복한다.

Expected: **유실이 나타난다** — 합계 2.5 GB/s로 천장을 넘기 때문이다. 프레임 수가 서로 벌어지고
fps가 184 아래로 떨어진다. 이것이 2절의 1/4 상한이 실재함을 보이는 반례 시험이다. 확인 후 다시
`1024x772`로 되돌린다.

- [ ] **Step 3: 표시 감축이 녹화를 훼손하지 않는지 확인한다**

ROI `1024x772`, 표시 fps 30으로 CAM0에서 `● Rec`를 10초 돌린다.

Run: `ffmpeg -i <출력.mp4> -map 0:v:0 -c copy -f null - 2>&1 | grep frame=`
Expected: 약 1840 프레임(184 × 10). **30 × 10 = 300에 가까우면 실패다** — 표시 감축이 녹화
경로로 새어 나간 것이므로 Task 6 Step 3을 다시 볼 것.

- [ ] **Step 4: 카메라에 남는 설정을 되돌린다**

이 검증에서 `ExposureTime`을 바꿨다. `AcquisitionFrameRate`의 최대값이 노출에 종속되므로 원래
값을 기록해 두고 되돌린다. Camera 1의 원래 값은 100000 µs였다.

ROI도 카메라에 남는 설정이다. 현장 구성으로 둘 것이 아니면 각 pane에서 `Full`을 누른다.

- [ ] **Step 5: 최종 확인과 커밋**

Run: `dotnet build MatroxFrameGrabber.slnx -c Release`
Expected: 경고 0개, 오류 0개

Run: `dotnet test MatroxFrameGrabber.slnx -c Release`
Expected: 전부 통과

Run: `git status --short`
Expected: 비어 있음 (Step 1-4에서 코드 수정이 없었다면)

측정값을 문서에 남긴다. `research.md` 8절의 표에 ROI 1024×772 3채널 결과 한 줄을 추가한다.

```bash
git add research.md
git commit -m "Record the ROI configuration that actually holds 184 fps

The half-area counter-test is the useful half of this: it loses frames, so
the quarter-area limit in the design doc is a measured boundary rather than
a cautious guess."
```

---

## Self-Review

**스펙 커버리지 (2절·5절):**

| 스펙 요구 | 담당 Task |
|---|---|
| 2절 — ROI는 원본 면적의 1/4 이하 | Task 1(산술·테스트), Task 8 Step 2(반례 시험) |
| 2절 — 카메라 ROI / 분석 ROI 구분 | Task 4는 카메라 ROI만 구현. 분석 ROI는 계획 ③ 소관 |
| 2절 — 짝수 경계, `M_FEATURE_INCREMENT` 스냅 | Task 1(`Snap`), Task 4(`ReadIncrement`) |
| 2절 — 정수형 피처는 `M_TYPE_MIL_INT` | Task 2 |
| 2절 — 버퍼 재할당 경로 | Task 4(`ApplyRoi`, `ReloadWithDcf` 답습) |
| 2절 — v1은 숫자 입력 + 채널별 영속화 | Task 3, Task 7 |
| 5절 — `M_UPDATE_RATE_MAX` | Task 5 |
| 5절 — 훅 `MbufCopy` 시간 기준 감축 | Task 6 |
| 5절 — ROI를 먼저, 캡을 나중에 | Task 순서(4 → 5), Task 5 Step 1에 명시 |
| 인수 기준 — `M_PROCESS_FRAME_MISSED` 불변 | Task 8 Step 1 |

**범위 밖으로 확인한 것:** 프리뷰 드래그 선택과 화면 자동 검출은 스펙 2절이 명시적으로 v1
범위 밖이라 Task가 없다. 타일 축약·분석 스레드·판정은 계획 ③이다.

**타입 일관성 확인:** `ChannelRoi.Snap`의 인자 순서(`xInc, yInc, wInc, hInc, maxWidth, maxHeight`)가
Task 1의 테스트, Task 4의 `WriteRoiToCamera`와 `ApplyRoi` 세 곳에서 동일하다.
`BytesPerFrame(bands, maxWidth, maxHeight)`는 Task 1에서만 쓰인다(Task 8은 손계산). `RoiInputX/Y/W/H`
이름이 Task 4의 프로퍼티와 Task 7의 바인딩에서 일치한다. `ApplyRoiFromInputs`/`ClearRoi`가 Task 4
정의와 Task 7 호출에서 일치한다.

**남은 주의:** `MIL.M_MAX_REFRESH_RATE`는 어셈블리 메타데이터에 존재를 확인했으나 `M_UPDATE_RATE_MAX`의
"무제한" 값으로 실제 동작하는지는 검증하지 않았다. Task 5 Step 4에서 `DisplayUpdateFps = 0`이
실제로 캡을 푸는지 확인한다. 풀지 않으면 캡을 걸지 않는 분기(`if (fps > 0)`)로 바꾼다.
