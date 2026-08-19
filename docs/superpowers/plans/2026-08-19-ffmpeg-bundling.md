# ffmpeg 산출물 고정 구현 계획

> **에이전트 작업자에게:** 필수 하위 스킬 — 이 계획은 `superpowers:subagent-driven-development`(권장) 또는 `superpowers:executing-plans`로 작업 단위별로 실행한다. 단계는 체크박스(`- [ ]`) 문법으로 추적한다.

**목표:** 실행 계정이나 winget 상태와 무관하게, 빌드 산출물에 동봉된 ffmpeg로 항상 동작하게 만든다.

**설계 개요:** 빌드가 `tools/ffmpeg/ffmpeg.exe`를 확보해 출력 폴더로 복사하고, 런타임 탐색은 앱 폴더를 가장 먼저 본다. 기존 탐색 경로(PATH·WinGet·`C:\ffmpeg\bin`)는 폴백으로 그대로 남긴다.

**기술 스택:** C# / WPF / net6.0-windows / x64, MSBuild 커스텀 타깃, PowerShell 5.1

**스펙:** `docs/superpowers/specs/2026-08-19-ffmpeg-bundling-design.md`

## 전역 제약

- 대상 프레임워크는 `net6.0-windows`, 플랫폼은 `x64` 고정. 변경하지 않는다.
- **ffmpeg가 없어도 빌드를 깨뜨리지 않는다.** 경고만 낸다 — ffmpeg 없이도 라이브 뷰는 동작해야 한다.
- **ffmpeg 바이너리(약 212MB)를 저장소에 커밋하지 않는다.** 현재 `.git`은 1.3MB다.
- 기존 폴백 탐색 경로를 **제거하지 않는다.** 현장 빌드 중 `tools/`가 비어 있어도 지금처럼 동작해야 한다.
- 용어는 `CONTEXT.md`를 따른다. 특히 `Missed frame`(취득 측)과 `Dropped frame`(인코딩 측)을 섞지 않는다.
- XAML의 UI 라벨과 소스 주석은 **영어**(주변과 일치), 문서와 스크립트 출력 메시지는 **한국어**.
- 이 리포에는 테스트 프로젝트가 없다. 스펙이 자동 테스트를 범위 밖으로 두었으므로, 각 작업은 **실행 가능한 확인 명령과 기대 출력**으로 검증한다. 테스트 코드를 지어내지 않는다.

---

### 작업 1: 해석된 ffmpeg 경로를 UI에 노출

지금은 어떤 ffmpeg로 돌고 있는지 앱에서 볼 방법이 없다. 이후 작업들의 검증 수단이 되므로 가장 먼저 만든다.

**파일:**
- 수정: `src/ViewModels/MainViewModel.cs` (78행 `AnyCanRecord` 뒤)
- 수정: `src/Views/MainWindow.xaml:63-95` (Recording settings 팝업의 Grid)

**인터페이스:**
- 사용: 없음
- 제공: `MainViewModel.FfmpegPathText` (`string`) — 해석된 전체 경로, 못 찾으면 안내 문구

- [ ] **1단계: 현재 상태를 눈으로 확인**

앱을 실행해 툴바의 Recording settings 팝업을 연다.

실행: `src\bin\x64\Release\net6.0-windows\MatroxFrameGrabber.exe`
기대: 팝업에 `RAW auto-stop`, `RAW segment`, `Resolution` 세 줄만 있고 ffmpeg 관련 표시는 **없다**.

- [ ] **2단계: 뷰모델에 속성 추가**

`src/ViewModels/MainViewModel.cs`의 `AnyCanRecord` 속성이 끝나는 78행 다음, `StartAllCommand`(80행) 앞에 넣는다. `using MatroxFrameGrabber.Infrastructure;`는 이미 5행에 있으므로 추가하지 않는다.

```csharp
        /// <summary>
        /// The ffmpeg.exe actually resolved for this run, for the recording settings popup.
        /// Bound once at load: the configured path has no editor, so this cannot change while
        /// the window is open.
        /// </summary>
        public string FfmpegPathText
        {
            get
            {
                string path = FfmpegRecorder.ResolveFfmpegPath(Output.FfmpegPath);
                return string.IsNullOrEmpty(path)
                    ? "not found — recording disabled"
                    : path;
            }
        }
```

- [ ] **3단계: 팝업에 행 추가**

`src/Views/MainWindow.xaml`의 `Grid.RowDefinitions`(63-67행)에 네 번째 행을 추가한다.

```xml
                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="Auto" />
                                        <RowDefinition Height="Auto" />
                                    </Grid.RowDefinitions>
```

그리고 `Resolution` 행(89-94행) 바로 뒤, `</Grid>` 앞에 넣는다.

```xml
                                    <TextBlock Grid.Row="3" Grid.Column="0" Text="ffmpeg"
                                               VerticalAlignment="Center" Margin="0,7,12,0" Foreground="#D0D0D0" />
                                    <TextBlock Grid.Row="3" Grid.Column="1" MaxWidth="180" Margin="0,7,0,0"
                                               Text="{Binding FfmpegPathText}"
                                               ToolTip="{Binding FfmpegPathText}"
                                               TextTrimming="CharacterEllipsis"
                                               VerticalAlignment="Center"
                                               Foreground="{StaticResource MutedTextBrush}" />
```

- [ ] **4단계: 빌드**

실행: `dotnet build MatroxFrameGrabber.slnx -c Release --nologo`
기대: `오류 0개`. 경고는 2개(`NETSDK1138`, net6.0 EOL)이며 이번 작업 이전부터 있던 것이다.

- [ ] **5단계: 실행해 확인**

실행: `src\bin\x64\Release\net6.0-windows\MatroxFrameGrabber.exe`
기대: 팝업에 `ffmpeg` 행이 생기고 값이 `...\WinGet\Packages\Gyan.FFmpeg...\bin\ffmpeg.exe`로 보인다. 폭이 좁아 잘리면 마우스를 올려 툴팁에 전체 경로가 뜨는지 확인한다.

- [ ] **6단계: 커밋**

```bash
git add src/ViewModels/MainViewModel.cs src/Views/MainWindow.xaml
git commit -m "Show which ffmpeg the app resolved

Nothing in the UI said which ffmpeg.exe was in use, so a machine with a
stale or missing one looked identical to a healthy one."
```

---

### 작업 2: 앱 폴더를 가장 먼저 탐색

**파일:**
- 수정: `src/Infrastructure/FfmpegRecorder.cs:1-96`

**인터페이스:**
- 사용: 없음
- 제공: `FfmpegRecorder.LocateFfmpeg(IEnumerable<string> dirs)` (`internal static string`) — 주어진 디렉터리 중 `ffmpeg.exe`가 있는 첫 경로, 없으면 `null`. `ResolveFfmpegPath(string configured)`의 시그니처와 동작은 그대로 유지된다.

- [ ] **1단계: using 추가**

`src/Infrastructure/FfmpegRecorder.cs`의 2행 `using System.Collections.Concurrent;` **다음 줄**에 넣어 알파벳 순서를 유지한다. 현재 `System.Collections.Generic`이 없어 `IEnumerable<string>`을 쓸 수 없다.

```csharp
using System.Collections.Generic;
```

- [ ] **2단계: `LocateFfmpeg()` 전체를 교체**

65-96행의 기존 `private static string LocateFfmpeg()` 메서드 전체를 아래로 바꾼다.

```csharp
        /// <summary>
        /// Directories searched for ffmpeg.exe, in order. The application folder comes first so a
        /// copy shipped next to the exe always wins over whatever happens to be installed on the
        /// machine — the WinGet paths below live under the running account's profile, so they
        /// vanish when somebody else logs in.
        /// </summary>
        private static IEnumerable<string> DefaultSearchDirs()
        {
            yield return AppContext.BaseDirectory;

            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (!string.IsNullOrWhiteSpace(dir))
                    yield return dir.Trim();

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");

            yield return @"C:\ffmpeg\bin";
            yield return @"C:\Program Files\ffmpeg\bin";
        }

        /// <summary>First directory in <paramref name="dirs"/> that holds ffmpeg.exe, or null.</summary>
        internal static string LocateFfmpeg(IEnumerable<string> dirs)
        {
            foreach (string dir in dirs)
            {
                try
                {
                    string candidate = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }   // one unusable PATH entry must not abort the whole search
            }
            return null;
        }

        private static string LocateFfmpeg()
        {
            string hit = LocateFfmpeg(DefaultSearchDirs());
            if (hit != null) return hit;

            // WinGet keeps the real binary under a Packages folder whose name carries the version
            // and so changes on every update — it cannot be written as a fixed directory.
            try
            {
                string packages = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(packages))
                    return Directory.EnumerateFiles(packages, "ffmpeg.exe", SearchOption.AllDirectories)
                                    .FirstOrDefault();
            }
            catch { }

            return null;
        }
```

- [ ] **3단계: 빌드**

실행: `dotnet build MatroxFrameGrabber.slnx -c Release --nologo`
기대: `오류 0개`, 경고 2개.

- [ ] **4단계: 폴백이 유지되는지 확인**

앱 폴더에는 아직 ffmpeg가 없다. 앱을 실행하고 Recording settings 팝업을 연다.
기대: `ffmpeg` 값이 여전히 WinGet 경로다. 녹화 버튼이 활성 상태다.

- [ ] **5단계: 앱 폴더가 이기는지 확인**

```bash
cp "$LOCALAPPDATA/Microsoft/WinGet/Packages/Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe/ffmpeg-9.0-full_build/bin/ffmpeg.exe" \
   src/bin/x64/Release/net6.0-windows/ffmpeg.exe
```

앱을 다시 실행해 팝업을 연다.
기대: `ffmpeg` 값이 `...\bin\x64\Release\net6.0-windows\ffmpeg.exe`로 바뀐다. **이 단계가 통과하지 못하면 작업 2는 실패한 것이다.**

- [ ] **6단계: 복사본 제거**

작업 4에서 빌드가 스스로 넣게 되므로, 손으로 둔 사본은 지운다.

```bash
rm src/bin/x64/Release/net6.0-windows/ffmpeg.exe
```

- [ ] **7단계: 커밋**

```bash
git add src/Infrastructure/FfmpegRecorder.cs
git commit -m "Search the app folder for ffmpeg before anything else

Every path the search knew about lived under the running account's
profile, so the app found ffmpeg only for whoever installed it. A copy
sitting next to the exe now wins, and the search order is a list a
caller can supply."
```

---

### 작업 3: `tools/fetch-ffmpeg.ps1`로 원본 확보

**파일:**
- 생성: `tools/fetch-ffmpeg.ps1`
- 수정: `.gitignore`

**인터페이스:**
- 사용: 없음
- 제공: `tools/ffmpeg/ffmpeg.exe` — 작업 4의 MSBuild 타깃이 이 경로를 참조한다. 스크립트는 성공 시 `0`, 못 찾으면 `1`을 반환한다.

- [ ] **1단계: `.gitignore`에 추가**

파일 끝에 붙인다.

```
# ffmpeg 실행 파일은 약 212MB다. 저장소에 넣지 않고 tools/fetch-ffmpeg.ps1로 채운다.
tools/ffmpeg/
```

- [ ] **2단계: 스크립트 작성**

`tools/fetch-ffmpeg.ps1`을 만든다. WinGet의 `Links` 폴더는 심볼릭 링크 shim이라 복사 대상으로 부적절하므로, **실제 파일이 있는 `Packages`를 먼저** 본다.

```powershell
#requires -Version 5.1
# tools/ffmpeg/ffmpeg.exe 를 이 PC에 설치된 ffmpeg에서 채운다.
# 바이너리는 약 212MB이므로 저장소에 커밋하지 않는다.
# 설계: docs/superpowers/specs/2026-08-19-ffmpeg-bundling-design.md
$ErrorActionPreference = 'Stop'

$dest = Join-Path $PSScriptRoot 'ffmpeg\ffmpeg.exe'
if (Test-Path -LiteralPath $dest) {
    Write-Host "이미 있습니다: $dest"
    exit 0
}

$candidates = New-Object System.Collections.Generic.List[string]

# WinGet Packages 를 먼저 본다. Links 폴더는 심볼릭 링크 shim이라 복사본이 단독으로 동작하지 않는다.
$packages = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
if (Test-Path -LiteralPath $packages) {
    $hit = Get-ChildItem -LiteralPath $packages -Filter ffmpeg.exe -Recurse -File -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if ($hit) { $candidates.Add($hit.FullName) }
}

$command = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
if ($command) { $candidates.Add($command.Source) }

$candidates.Add('C:\ffmpeg\bin\ffmpeg.exe')
$candidates.Add('C:\Program Files\ffmpeg\bin\ffmpeg.exe')

$source = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $source) {
    Write-Warning "ffmpeg.exe를 찾지 못했습니다. 'winget install Gyan.FFmpeg' 후 다시 실행하거나 tools/ffmpeg/ 에 직접 넣으세요."
    exit 1
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
Copy-Item -LiteralPath $source -Destination $dest -Force
Write-Host "복사함: $source"
Write-Host "     -> $dest"
```

- [ ] **3단계: 실행해 확인**

실행: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\fetch-ffmpeg.ps1`
기대: `복사함: ...` 두 줄이 출력되고 종료 코드가 0이다.

- [ ] **4단계: 결과물 확인**

실행: `ls -l tools/ffmpeg/ffmpeg.exe`
기대: 파일이 존재하고 크기가 원본과 같다(이 PC 기준 약 212MB). 심볼릭 링크가 아니라 실제 파일이어야 한다.

- [ ] **5단계: 재실행 시 무동작 확인**

실행: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\fetch-ffmpeg.ps1`
기대: `이미 있습니다: ...` 한 줄, 종료 코드 0. 다시 복사하지 않는다.

- [ ] **6단계: git이 무시하는지 확인**

실행: `git status --short`
기대: `tools/ffmpeg/`나 `ffmpeg.exe`가 목록에 **나타나지 않는다**. `.gitignore`와 `tools/fetch-ffmpeg.ps1`만 보인다.

- [ ] **7단계: 커밋**

```bash
git add .gitignore tools/fetch-ffmpeg.ps1
git commit -m "Add a script that stages ffmpeg for the build

The binary is 212 MB against a 1.3 MB repository, so it stays out of
git and gets fetched from whatever the machine already has installed."
```

---

### 작업 4: MSBuild가 ffmpeg를 출력 폴더에 넣게 한다

**파일:**
- 수정: `src/MatroxFrameGrabber.csproj` (`</Project>` 앞)

**인터페이스:**
- 사용: `tools/ffmpeg/ffmpeg.exe` (작업 3)
- 제공: 빌드 출력 폴더의 `ffmpeg.exe` — 작업 2의 앱 폴더 탐색이 이것을 집는다

- [ ] **1단계: 타깃 추가**

`src/MatroxFrameGrabber.csproj`의 `</Project>` 바로 앞에 넣는다.

출력 복사를 `ItemGroup`의 `CopyToOutputDirectory`가 아니라 별도 타깃으로 하는 이유: `Condition="Exists(...)"`가 걸린 아이템은 **평가 시점**에 판정되므로, 같은 빌드에서 `EnsureFfmpegTool`이 방금 만든 파일을 잡지 못한다.

```xml
  <!-- ffmpeg는 런타임 의존성이라 NuGet으로 오지 않는다. 빌드가 산출물에 직접 넣어,
       실행 계정이나 winget 상태와 무관하게 같은 바이너리로 돌게 한다.
       설계: docs/superpowers/specs/2026-08-19-ffmpeg-bundling-design.md -->
  <PropertyGroup>
    <FfmpegToolPath>$(MSBuildProjectDirectory)\..\tools\ffmpeg\ffmpeg.exe</FfmpegToolPath>
  </PropertyGroup>

  <Target Name="EnsureFfmpegTool" BeforeTargets="BeforeBuild"
          Condition="!Exists('$(FfmpegToolPath)')">
    <Exec Command="powershell -NoProfile -ExecutionPolicy Bypass -File &quot;$(MSBuildProjectDirectory)\..\tools\fetch-ffmpeg.ps1&quot;"
          ContinueOnError="true" />
    <Warning Condition="!Exists('$(FfmpegToolPath)')"
             Text="ffmpeg.exe를 찾지 못해 산출물에 동봉하지 않습니다. 'winget install Gyan.FFmpeg' 후 다시 빌드하거나 tools/ffmpeg/ 에 직접 넣으세요. 녹화는 런타임 탐색 결과에 따라 동작할 수도, 비활성화될 수도 있습니다." />
  </Target>

  <Target Name="CopyFfmpegToOutput" AfterTargets="Build"
          Condition="Exists('$(FfmpegToolPath)')">
    <Copy SourceFiles="$(FfmpegToolPath)" DestinationFolder="$(OutDir)" SkipUnchangedFiles="true" />
  </Target>
```

- [ ] **2단계: 빌드해서 복사되는지 확인**

실행: `dotnet build MatroxFrameGrabber.slnx -c Release --nologo`
기대: `오류 0개`. `tools/ffmpeg/ffmpeg.exe`가 이미 있으므로 `EnsureFfmpegTool`은 건너뛴다.

실행: `ls -l src/bin/x64/Release/net6.0-windows/ffmpeg.exe`
기대: 파일이 존재하고 크기가 `tools/ffmpeg/ffmpeg.exe`와 같다.

- [ ] **3단계: 증분 빌드가 다시 복사하지 않는지 확인**

실행: `dotnet build MatroxFrameGrabber.slnx -c Release --nologo`
기대: 두 번째 빌드가 첫 번째보다 눈에 띄게 빠르다. 212MB를 매번 복사하면 안 된다.

- [ ] **4단계: ffmpeg가 없을 때 빌드가 살아남는지 확인**

전역 제약 "빌드를 깨뜨리지 않는다"의 검증이다. 이 PC에는 ffmpeg가 설치돼 있으므로 `fetch-ffmpeg.ps1`이 다시 찾아 채운다. 즉 여기서의 기대는 "빌드 성공 + 출력 폴더에 ffmpeg 재생성"이다.

```bash
rm -f tools/ffmpeg/ffmpeg.exe src/bin/x64/Release/net6.0-windows/ffmpeg.exe
dotnet build MatroxFrameGrabber.slnx -c Release --nologo
ls -l tools/ffmpeg/ffmpeg.exe src/bin/x64/Release/net6.0-windows/ffmpeg.exe
```

기대: `오류 0개`, 두 파일 모두 다시 생성됨.

ffmpeg가 설치되지 않은 PC에서의 기대 동작은 다르다: 빌드는 **성공**하되 `ffmpeg.exe를 찾지 못해...` 경고가 추가로 나오고 출력 폴더에 `ffmpeg.exe`가 없다. 그런 PC에 접근할 수 있으면 거기서도 확인한다.

- [ ] **5단계: 앱이 동봉본을 쓰는지 확인**

앱을 실행하고 Recording settings 팝업을 연다.
기대: `ffmpeg` 값이 `...\bin\x64\Release\net6.0-windows\ffmpeg.exe`다. 더 이상 WinGet 경로가 아니다.

- [ ] **6단계: 커밋**

```bash
git add src/MatroxFrameGrabber.csproj
git commit -m "Put ffmpeg in the build output

A maintenance handover copies the output folder, so ffmpeg has to be
inside it. Missing ffmpeg warns rather than fails: live view does not
need it."
```

---

### 작업 5: 라이선스 고지를 산출물에 포함

이 프로그램은 고객사에 인도된다. ffmpeg를 동봉하는 순간 GPL 코드를 인도하는 것이 되므로, 인도받는 쪽에 라이선스 원문과 소스 입수처를 함께 준다. 공개 저장소나 웹사이트 고지는 필요 없다.

**파일:**
- 생성: `LICENSES/ffmpeg/COPYING.GPLv3`
- 생성: `LICENSES/ffmpeg/README.md`
- 수정: `src/MatroxFrameGrabber.csproj` (`ItemGroup` 추가)

**인터페이스:**
- 사용: 없음
- 제공: 빌드 출력 폴더의 `LICENSES/ffmpeg/`

- [ ] **1단계: 라이선스 원문 복사**

배포본 루트에 `LICENSE`(GPL v3 원문)와 `README.txt`가 들어 있다.

```bash
mkdir -p LICENSES/ffmpeg
cp "$LOCALAPPDATA/Microsoft/WinGet/Packages/Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe/ffmpeg-9.0-full_build/LICENSE" \
   LICENSES/ffmpeg/COPYING.GPLv3
head -3 LICENSES/ffmpeg/COPYING.GPLv3
```

기대: `GNU GENERAL PUBLIC LICENSE` / `Version 3, 29 June 2007`이 보인다.

- [ ] **2단계: 고지 문서 작성**

`LICENSES/ffmpeg/README.md`를 만든다. **버전은 실제로 동봉한 것을 적는다** — 다른 빌드를 넣었다면 그 값으로 바꾼다.

````markdown
# ffmpeg

이 프로그램은 녹화를 위해 `ffmpeg.exe`를 별도 프로세스로 실행하며, 실행 파일을 함께 배포한다.

- **버전**: 9.0-full_build
- **출처**: https://www.gyan.dev/ffmpeg/builds/ (`winget install Gyan.FFmpeg`)
- **라이선스**: GPL v3 (`COPYING.GPLv3`). 이 빌드는 `--enable-gpl --enable-libx264`로 구성돼 있다.
- **소스 코드**: https://github.com/FFmpeg/FFmpeg 및 위 배포처의 소스 아카이브.
  이 소프트웨어를 인도받은 자는 위 소스를 요청할 수 있다.

이 프로그램 자체는 ffmpeg를 라이브러리로 링크하지 않는다. 별도 프로세스로 호출할 뿐이다.
근거는 `docs/adr/0001-ffmpeg-for-all-encoding.md` 참고.
````

- [ ] **3단계: csproj에 복사 규칙 추가**

`src/MatroxFrameGrabber.csproj`의 기존 `PackageReference` `ItemGroup`(20-23행) 바로 뒤에 넣는다.

```xml
  <!-- 동봉한 ffmpeg의 라이선스 고지. 산출물 폴더가 그대로 고객사에 인도되므로 함께 따라가야 한다. -->
  <ItemGroup>
    <None Include="..\LICENSES\**\*" LinkBase="LICENSES" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **4단계: 빌드해서 확인**

실행: `dotnet build MatroxFrameGrabber.slnx -c Release --nologo`
기대: `오류 0개`.

실행: `ls -R src/bin/x64/Release/net6.0-windows/LICENSES`
기대: `ffmpeg/COPYING.GPLv3`와 `ffmpeg/README.md`가 보인다.

- [ ] **5단계: 커밋**

```bash
git add LICENSES src/MatroxFrameGrabber.csproj
git commit -m "Ship ffmpeg's licence notice with the binary

Handing the output folder to the customer now hands over GPL software,
so the licence text and where to get the sources travel with it."
```

---

### 작업 6: README에 두 납품 절차를 적는다

현재 `README.md`의 전제조건은 .NET SDK 설치를 가정하고 있어 개발 PC 기준이다. 현장 구축과 유지보수 교체 절차가 어디에도 없다.

**파일:**
- 수정: `README.md` (요구사항 절 뒤)

**인터페이스:**
- 사용: 없음
- 제공: 없음 (문서)

- [ ] **1단계: 현재 요구사항 절 확인**

실행: `grep -n "요구\|필요\|Windows x64" README.md`
기대: 42행 부근에 `Windows x64, MIL 10.70 설치, WindowsDesktop(net6.0) 런타임이 포함된 .NET SDK` 줄이 있다.

- [ ] **2단계: 절 추가**

요구사항 절 뒤에 아래 내용을 넣는다.

````markdown
## 설치와 납품

이 프로그램은 두 경로로 현장에 올라간다.

### 현장 구축 — 장비 PC에서 직접 빌드

현장 PC에 필요한 것:

- Rapixo CXP 보드와 드라이버, **MIL 10.70**
- **.NET SDK** (net6.0-windows 대상 빌드가 가능한 버전)
- **ffmpeg** — `winget install Gyan.FFmpeg`. 없으면 빌드는 되지만 녹화 버튼이 비활성화된다.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools\fetch-ffmpeg.ps1
dotnet build MatroxFrameGrabber.slnx -c Release
```

첫 명령이 `tools/ffmpeg/ffmpeg.exe`를 채우고, 빌드가 그것을 산출물에 복사한다.
`tools/ffmpeg/`는 git에 포함되지 않는다(약 212MB).

### 유지보수 교체 — 회사에서 빌드해 복사

회사에서 위와 같이 빌드한 뒤 **출력 폴더 전체**를 현장 PC의 설치 위치에 덮어쓴다.

```
src\bin\x64\Release\net6.0-windows\
```

이 폴더에 `ffmpeg.exe`와 `LICENSES/`가 함께 들어 있으므로 따로 챙길 것이 없다.
현장 PC에 이미 있어야 하는 것은 **MIL 10.70**, **Rapixo 드라이버**, **WindowsDesktop(net6.0) 런타임**이다. SDK는 이 경로에서는 필요 없다.

앱은 자기 폴더의 `ffmpeg.exe`를 가장 먼저 찾으므로 실행 계정이나 그 PC의 winget 설치 상태와 무관하게 동작한다. 실제로 어떤 ffmpeg가 쓰이는지는 툴바의 Recording settings 팝업에서 확인할 수 있다.
````

- [ ] **3단계: 렌더링 확인**

실행: `grep -n "^#" README.md`
기대: `## 설치와 납품`, `### 현장 구축 — 장비 PC에서 직접 빌드`, `### 유지보수 교체 — 회사에서 빌드해 복사`가 순서대로 있다. 중첩 코드 펜스가 문서를 깨뜨리지 않았는지 눈으로 확인한다.

- [ ] **4단계: 커밋**

```bash
git add README.md
git commit -m "Document the two ways this reaches the customer

The prerequisites described a developer's machine. Neither the on-site
build nor the maintenance handover was written down anywhere."
```

---

## 최종 인수 확인

작업 1-6을 마친 뒤 실행한다. 스펙의 "검증 방법" 절에 대응한다.

- [ ] 깨끗한 빌드 후 출력 폴더에 `ffmpeg.exe`와 `LICENSES/ffmpeg/`가 있다
- [ ] `tools/ffmpeg/`를 비우고 빌드해도 성공한다
- [ ] **다른 Windows 계정으로 로그인해 실행해도 녹화 버튼이 활성이다**
- [ ] 라이브 녹화 1회 — MP4가 생성되고 재생된다
- [ ] RAW 녹화 1회 — segment가 MP4로 변환되고, 종료 후 컬러 파이프라인이 정상 복구된다

세 번째 항목이 이 작업 전체의 목적이다. 나머지가 모두 통과해도 이것이 실패하면 목표를 달성하지 못한 것이다. **다른 계정 로그인이 필요하므로 사람이 직접 확인해야 한다.**

다섯 번째 항목은 카메라가 연결된 상태에서만 가능하다. RAW 종료 후 `M_BAYER_CONVERSION`이 복구되지 않으면 다음 실행에서 화면이 타일처럼 깨지므로 여기서 반드시 확인한다.
