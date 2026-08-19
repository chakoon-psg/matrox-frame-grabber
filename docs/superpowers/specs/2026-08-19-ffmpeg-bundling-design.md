# ffmpeg 산출물 고정 + 두 납품 경로 일원화

작성일: 2026-08-19

## 배경

이 프로그램은 SI 납품물이고 두 가지 경로로 고객사 PC에 올라간다.

- **현장 구축** — 고객사 장비 PC에 SDK와 소스를 두고 그 자리에서 빌드한다.
- **유지보수 교체** — 회사에서 빌드한 뒤 빌드 출력 폴더를 복사해 교체한다.

두 경로 모두 녹화 기능이 외부 `ffmpeg.exe`에 의존하는데, 지금은 그 위치가 실행 계정에
묶여 있다. 이 PC에서 실제로 해석되는 경로는 다음과 같다.

```
C:\Users\user\AppData\Local\Microsoft\WinGet\Packages\
    Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0-full_build\bin\ffmpeg.exe
```

여기서 두 가지 문제가 나온다.

1. **계정 종속** — `FfmpegRecorder.LocateFfmpeg()`의 WinGet 탐색은
   `Environment.SpecialFolder.LocalApplicationData`, 즉 실행 중인 계정의 프로필을 본다.
   현장에서 다른 계정으로 로그인해 실행하면 ffmpeg를 찾지 못하고 `CanRecord`가 false가 되어
   녹화 버튼 두 개가 조용히 비활성화된다.
2. **버전 드리프트** — winget이 ffmpeg를 갱신하면 경로의 `ffmpeg-9.0-full_build` 부분이
   바뀐다. 재귀 탐색이라 찾기는 하지만, 어느 버전으로 돌고 있는지 확인할 방법이 앱에 없다.

## 목표

현장 빌드로 깔든 회사 빌드본을 복사하든, **실행 계정과 winget 상태에 무관하게 항상 같은
ffmpeg로 동작**하게 한다.

## 설계

### 1. `tools/ffmpeg/` — 빌드가 참조하는 단일 원본

리포 루트에 `tools/ffmpeg/` 폴더를 두고 `.gitignore`에 등록한다. `ffmpeg.exe`는 212MB이고
현재 `.git`이 1.3MB이므로 저장소에 넣지 않는다.

### 2. `tools/fetch-ffmpeg.ps1` — 원본 채우기

`tools/ffmpeg/ffmpeg.exe`가 없을 때 다음 순서로 찾아 복사한다.

1. `PATH`
2. `%LocalAppData%\Microsoft\WinGet\Links`
3. `%LocalAppData%\Microsoft\WinGet\Packages` (재귀)
4. `C:\ffmpeg\bin`, `C:\Program Files\ffmpeg\bin`

찾은 경로를 표준 출력에 적는다. 못 찾으면 0이 아닌 종료 코드를 반환한다.

### 3. MSBuild 타깃 (`src/MatroxFrameGrabber.csproj`)

```xml
<PropertyGroup>
  <FfmpegToolPath>$(MSBuildProjectDirectory)\..\tools\ffmpeg\ffmpeg.exe</FfmpegToolPath>
</PropertyGroup>

<Target Name="EnsureFfmpegTool" BeforeTargets="BeforeBuild"
        Condition="!Exists('$(FfmpegToolPath)')">
  <Exec Command="powershell -NoProfile -ExecutionPolicy Bypass -File &quot;$(MSBuildProjectDirectory)\..\tools\fetch-ffmpeg.ps1&quot;"
        ContinueOnError="true" />
  <Warning Condition="!Exists('$(FfmpegToolPath)')"
           Text="ffmpeg.exe를 찾지 못해 산출물에 동봉하지 않습니다. tools/ffmpeg/ffmpeg.exe에 직접 넣거나 winget install Gyan.FFmpeg 후 다시 빌드하세요. 녹화 기능은 런타임 탐색 결과에 따라 동작할 수도, 비활성화될 수도 있습니다." />
</Target>

<Target Name="CopyFfmpegToOutput" AfterTargets="Build"
        Condition="Exists('$(FfmpegToolPath)')">
  <Copy SourceFiles="$(FfmpegToolPath)" DestinationFolder="$(OutDir)"
        SkipUnchangedFiles="true" />
</Target>
```

**빌드를 깨뜨리지 않는다.** ffmpeg가 없어도 라이브 뷰는 동작해야 하므로 경고만 낸다.

출력 복사를 `ItemGroup`의 `CopyToOutputDirectory`가 아니라 별도 타깃으로 하는 이유:
`Condition="Exists(...)"`가 걸린 아이템은 **평가 시점**에 판정되므로, 같은 빌드에서
`EnsureFfmpegTool`이 방금 만든 파일을 잡지 못한다.

### 4. 라이선스 고지 (`LICENSES/ffmpeg/`)

리포에 커밋하는 텍스트 파일이다.

- `COPYING.GPLv3` — 배포본 루트의 `LICENSE` 파일(GPL v3 원문)을 그대로 복사한다.
  gyan.dev 빌드는 배포 폴더에 `LICENSE`와 `README.txt`를 포함하고 있다.
- `README.md` — 빌드 출처(gyan.dev), 버전(현재 `9.0-full_build`), 소스 아카이브 입수처

`csproj`에서 출력 폴더로 복사한다.

```xml
<ItemGroup>
  <None Include="..\LICENSES\**\*" LinkBase="LICENSES"
        CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

GPL이 요구하는 것은 **인도받은 상대방에 대한 소스 제공**이므로, 산출물 폴더에 고지가 함께
가면 충족된다. 공개 저장소나 웹사이트 고지는 필요 없다.

### 5. 탐색 순서 변경 (`src/Infrastructure/FfmpegRecorder.cs`)

```
현재:  설정 → PATH → WinGet Links → WinGet Packages(재귀) → C:\ffmpeg\bin
변경:  설정 → 앱 폴더 → PATH → WinGet Links → WinGet Packages(재귀) → C:\ffmpeg\bin
                ^^^^^^ 추가
```

`AppContext.BaseDirectory`를 `PATH`보다 **앞**에 둔다. 산출물에 동봉한 것이 항상 이기게 하는
것이 이 설계의 핵심이다. 뒤쪽 fallback은 그대로 둔다 — 현장 빌드 중 `tools/`가 비어 있어도
지금처럼 동작해야 한다.

같이 하는 작은 개선: `LocateFfmpeg()`를 `LocateFfmpeg(IEnumerable<string> dirs)`와
`DefaultSearchDirs()`로 분리한다. 지금은 정적 메서드가 환경변수를 직접 읽어 테스트가
불가능하다. WinGet Packages 재귀 탐색은 디렉터리 목록으로 표현되지 않으므로 별도 fallback으로
남긴다.

### 6. 진단 노출

어떤 ffmpeg로 돌고 있는지 앱에서 확인할 수단이 없다. **해석된 ffmpeg 경로를 UI에 노출**한다. 위치는 `MainWindow.xaml`의 출력 폴더 행
(현재 102-110행, 폴더 버튼과 `Browse…` 버튼이 있는 곳)이다. 툴바 공간이 좁으므로 짧은
상태 표시를 두고 전체 경로는 `ToolTip`에 넣는다 — 출력 폴더 버튼이 이미 쓰는 방식이다.
현장에서 "왜 녹화가 안 되지"를 즉시 판별하는 것이 목적이다.

버전 문자열까지 뽑는 것은 프로세스를 한 번 더 띄워야 하므로 이번 범위에서 제외한다.

### 7. 문서 (`README.md`)

두 절차를 명시한다.

- **현장 구축** — 전제조건(net6.0 WindowsDesktop 런타임, MIL 10.70, Rapixo 드라이버,
  .NET SDK), 빌드 명령, `tools/ffmpeg/` 채우는 방법
- **유지보수 교체** — 회사에서 빌드 → 어느 폴더를 복사 → 현장에 이미 있어야 하는 것

현재 `README.md:42`의 전제조건은 SDK 설치를 가정하고 있어 개발 PC 기준이다.

## 검증 방법

이 리포에는 테스트 프로젝트가 없다. 자동 테스트를 이번 범위에 넣지 않고 확인 절차를 명시한다.

1. `dotnet build MatroxFrameGrabber.slnx -c Release` 후 출력 폴더에 `ffmpeg.exe`와
   `LICENSES/`가 있는지 확인
2. `tools/ffmpeg/`를 비우고 다시 빌드 → 경고는 나되 빌드 성공, 앱은 기존 fallback으로 동작
3. **다른 Windows 계정으로 실행 → 녹화 버튼이 활성인지** (이번 작업의 실제 목표)
4. 실제 녹화 1회 — 라이브 녹화와 RAW 녹화 각각

3번이 통과하지 못하면 이 작업은 목적을 달성하지 못한 것이다.

## 범위 밖

- **H.264 / AV1 코덱 결정** — 고객사 요구사항 확인이 선행돼야 한다. 이 PC 실측으로
  libx264 ultrafast 421.9 fps, libsvtav1 p12 232.2 fps, h264_amf 206.9 fps이며, 이 iGPU에는
  AV1 하드웨어 인코더가 없다(`av1_amf` 초기화 실패).
- **ffmpeg 라이브러리 직접 링크** — 검토했으나 채택하지 않았다. 네이티브 DLL을 여전히
  동봉해야 해 단일 산출물이 되지 않고, 크래시 격리를 잃으며(RAW 모드는 보드의
  `M_BAYER_CONVERSION`을 꺼 놓고 돌므로 프로세스 사망이 하드웨어 상태를 남긴다), 테스트가
  없는 상태로 767줄을 재작성해야 한다.
- **설치 관리자 제작** — 현재 두 경로 모두 필요로 하지 않는다.
- **`net6.0-windows` EOL 대응** — 별건이다.
