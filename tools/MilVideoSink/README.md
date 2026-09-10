# MilVideoSink — 납품사에 넘기는 슬라이스

Rapixo CXP 한 채널에서 grab해 `IVideoSink` 하나에 프레임을 먹이는 콘솔 하네스.
**`MilSeqVideoSink`를 여기서 개발하고 계측합니다** — 애플리케이션 없이.

충족 기준과 함정은 [ACCEPTANCE.md](ACCEPTANCE.md)에 있습니다. 그 문서를 먼저 읽는 편이 시간을
아낍니다.

## 왜 별도 프로젝트인가

MIL로 파일을 쓰는 데 필요한 것은 시스템·디지타이저·grab 링·훅·싱크뿐입니다. 이 하네스에는
그것만 있습니다.

**들어 있지 않은 것:** 상태이상 검출기, 타일 축약, 임계값, 교정, 밝기 측정, 무손실 정지화면,
WPF 창, 설정 저장. 애플리케이션의 채널 클래스가 2,400줄인 이유가 그 목록이고, MIL 인코딩과는
아무 관계가 없습니다.

## 빌드와 실행

```
dotnet build tools/MilVideoSink/MilVideoSink.csproj -c Release
```

출력: `tools/MilVideoSink/bin/x64/Release/net10.0-windows/MilVideoSink.exe`

```
MilVideoSink.exe --seconds 60 --sink mil
MilVideoSink.exe --seconds 60 --sink mil --segments
MilVideoSink.exe --seconds 60 --sink ffmpeg     # 통과하는 기준선
```

| 인자 | 기본 | 뜻 |
|---|---|---|
| `--channel` | 0 | 디지타이저 인덱스 |
| `--seconds` | 30 | 실행 길이 |
| `--exposure` | 8000 | 노출 µs. 0이면 건드리지 않음 |
| `--decim` | 2 | decimation. 0이면 건드리지 않음 |
| `--sink` | mil | `mil` 또는 `ffmpeg` |
| `--segments` | 꺼짐 | 2초 세그먼트 + 0.5초 GOP |
| `--out` | `%TEMP%\MilVideoSink` | 출력 폴더 |

`--sink ffmpeg`에는 `ffmpeg.exe`가 `PATH`에 있거나 표준 위치에 있어야 합니다. MIL 쪽만 볼 것이면
필요 없습니다.

## 작업할 파일은 하나입니다

**`src/Mil/Video/MilSeqVideoSink.cs`**

그 파일에 의도한 `Mseq` 호출 순서와, 아직 추측인 부분이 주석으로 적혀 있습니다. 현재는
`MseqAlloc`까지만 실제로 하고 MIL이 준 이유를 그대로 돌려줍니다 — 반쯤 쓰인 인코더가 아무도
확인하지 않은 파일을 만드는 것보다 정직한 거절이 낫기 때문입니다. 그 파일이 만드는 파일이
상태이상의 유일한 기록이 됩니다.

나머지 파일은 읽기용입니다. **`FfmpegVideoSink.cs`가 동작하는 `IVideoSink` 구현 한 개**이니
무엇을 해야 하는지 비교 대상으로 쓰십시오.

## 소스는 한 벌만 존재합니다

이 프로젝트는 애플리케이션의 파일을 복사하지 않고 `<Compile Include>`로 끌어옵니다. 그래서
**돌려주신 `MilSeqVideoSink.cs`가 수정 없이 애플리케이션에 들어갑니다** — 두 벌이 갈라지지
않습니다.

## 넘기는 파일 목록

`tools/MilVideoSink/` 폴더와, 그 프로젝트가 참조하는 아래 파일들입니다.

| 파일 | 역할 |
|---|---|
| `src/Mil/Video/MilSeqVideoSink.cs` | **작업 대상** |
| `src/Mil/Video/IVideoSink.cs` | 구현할 계약 |
| `src/Mil/Video/VideoStreamSpec.cs` | 지오메트리·레이트·출력 목록 |
| `src/Mil/Video/VideoSinkStats.cs` | fed / skipped / dropped / µs |
| `src/Mil/Video/FfmpegVideoSink.cs` | 동작하는 구현 (참고) |
| `src/Mil/GenICamFeatures.cs` | 피처 읽기·쓰기 + read-back 규율 |
| `src/Infrastructure/MilErrorLog.cs` | 로깅 (모달 대화상자 대신) |
| `src/Infrastructure/FfmpegRecorder.cs` | ffmpeg 프로세스·파이프 (참고) |
| `src/Infrastructure/Video/FfmpegArgs.cs` | ffmpeg 명령줄 (참고) |
| `src/Infrastructure/Video/VideoRatePolicy.cs` | 어느 레이트를 선언하는가 |

한 번에 묶으려면:

```bash
git archive HEAD -o milvideosink.zip \
  tools/MilVideoSink \
  src/Mil/Video/MilSeqVideoSink.cs \
  src/Mil/Video/IVideoSink.cs \
  src/Mil/Video/VideoStreamSpec.cs \
  src/Mil/Video/VideoSinkStats.cs \
  src/Mil/Video/FfmpegVideoSink.cs \
  src/Mil/GenICamFeatures.cs \
  src/Infrastructure/MilErrorLog.cs \
  src/Infrastructure/FfmpegRecorder.cs \
  src/Infrastructure/Video/FfmpegArgs.cs \
  src/Infrastructure/Video/VideoRatePolicy.cs
```

`nuget.config`가 폴더에 들어 있어 MIL 10.70 설치본의 로컬 패키지 소스를 그대로 씁니다.

## 안 되면

애플리케이션은 ffmpeg로 계속 돕니다. `VideoSinkFactory`가 시작할 때 MIL이 인코딩할 수 있는지
판정해 로그에 남기고, 못 하면 MIL 싱크를 아예 제시하지 않습니다. **라이선스를 기다리는 코드
경로는 없습니다.**
