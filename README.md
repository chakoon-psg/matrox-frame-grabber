# Matrox Rapixo CXP — 멀티 카메라 뷰어

Matrox **Rapixo CXP**(CoaXPress) 프레임그래버 보드 한 장에 연결된 카메라(최대 **4대**)를
**MIL**(Matrox Imaging Library)로 실시간 표시하고 프레임마다 처리하는 C# **WPF** 애플리케이션입니다.

![스크린샷 — 4채널 뷰어](docs/images/screenshot.png)

## 기능

- **2×2 라이브 뷰**: 하나의 Rapixo CXP 보드에서 최대 4대 (`MdigProcess` 그랩 + 프레임별 훅 처리)
- **카메라 제어**(GenICam 피처 계층): **노출**(+ 자동), **취득 프레임레이트** 상한,
  **트리거**(모드/소스/소프트웨어 트리거), **화이트 밸런스**(자동/1회/R·B 비율),
  **DCF** 로드, MIL **GenICam 피처 브라우저**
- **설정 일괄 적용**: 한 카메라의 노출·Acq Rate·트리거·화이트밸런스를 나머지 카메라에 한 번에 복사
  (해당 기능이 없는 카메라는 자동으로 건너뜀)
- **녹화 2종**: 라이브 컬러 H.264(`● Rec`) / 무손실 RAW-Bayer 연속 녹화(`◆ RAW`) — 아래 참조
- **스냅샷** 저장(PNG), 카메라별 · 전체 Start/Stop, 녹화 중 정지 시 확인
- **화면 맞춤(Fit)**으로 잘림 없이 전체 표시 + 휠 **줌** / 드래그 **팬**, **1:1**,
  **더블클릭 전체화면**(ESC로 복귀)
- **카메라 분리 감지**(2회 연속 미검출 시 경고 + 녹화 자동 중지), 다크 테마

## 녹화

두 모드는 카메라별로 **상호 배타**이며, 부하가 걸렸을 때의 정책이 정반대입니다.

| | `● Rec` (라이브 컬러) | `◆ RAW` (무손실) |
|---|---|---|
| 경로 | MIL → 메모리 → ffmpeg **파이프** | MIL → 로컬 `.raw` 세그먼트 → ffmpeg **배치 변환** |
| 부하 시 | **프레임 드롭**(라이브 뷰 우선) | **취득 스레드 대기**(프레임 보존 우선) |
| 프리뷰 | 정상 컬러 | **흑백**(6프레임마다) |
| 해상도 프리셋 | 적용됨(Original/1080p/720p) | 항상 원본 해상도 |

RAW는 설정한 길이(기본 60초)마다 세그먼트를 끊어 백그라운드에서 MP4로 변환하므로
디스크가 허용하는 한 **무기한 연속 녹화**가 가능합니다. RAW 임시 파일은 빠른 로컬(NVMe) 스크래치
폴더에 쓰고, 변환된 MP4만 출력 폴더(NAS 가능)로 보냅니다.

출력 폴더·해상도·RAW 자동정지/세그먼트 길이는
`%LocalAppData%\MatroxFrameGrabber\settings.json`에 자동 저장됩니다.

## 요구 사항

- Windows x64, **MIL 10.70** 설치, WindowsDesktop(net6.0) 런타임이 포함된 .NET SDK
- 라이브 그랩에는 카메라가 연결된 Rapixo CXP 보드 필요 (하드웨어가 없으면 기본 MIL 시스템으로
  대체되어 "No camera" 패널 표시)
- **녹화에는 `ffmpeg.exe` 필요** — 설정 경로 → `PATH` → WinGet → `C:\ffmpeg\bin` 순으로 자동 탐색.
  찾지 못하면 `● Rec` / `◆ RAW` 버튼이 모두 비활성화됩니다.

## 빌드 & 실행

```bash
dotnet build MatroxFrameGrabber.slnx -c Release
```

실행: `src/bin/x64/Release/net6.0-windows/MatroxFrameGrabber.exe`

## 프로젝트 구조

```
MatroxFrameGrabber.slnx     솔루션
src/                        소스 (Views / ViewModels / Mil / Infrastructure)
docs/                       문서 · 이미지
research.md                 src/ 전체 심층 분석 리포트
```

빌드 상세와 구현 노트는 [CLAUDE.md](CLAUDE.md), 코드 수준의 상세 분석은
[research.md](research.md)를 참고하세요.
