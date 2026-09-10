# MIL 영상 싱크 — 충족 기준과 함정

`MilSeqVideoSink`가 만족해야 하는 조건과, 이 프로젝트가 실측으로 밟은 함정들. 함정 쪽이 더
값나갑니다 — 하나하나가 측정 한 번씩 걸려서 찾은 것들이고, 증상이 전부 "코드는 맞는데 값이 안
바뀐다" 계열입니다.

## 운전점

| | 값 |
|---|---|
| 보드 | Matrox Rapixo CXP, `M_SYSTEM_RAPIXOCXP`, PCIe Gen2 x8 |
| 해상도 | **1024×772**, 3밴드 8-bit (decimation 2) |
| 프레임 크기 | **2.26 MiB** |
| 프레임레이트 | **124.316 fps** = `31079/250` (노출 8000 µs) |
| 프레임 주기 | **8043 µs** ← `Feed`가 이 안에서 끝나야 합니다 |
| 채널 | 3대 동시 |

## 충족 기준

**1. 손실 0.** 60초 실행에서 `M_PROCESS_FRAME_MISSED` 증가가 **0**이어야 합니다.

취득 훅에서 하는 일은 취득 예산 안에서 돕니다. 넘치면 프리뷰가 느려지는 게 아니라 **프레임이
사라집니다.** 참조값:

| | 평균 | 최대 |
|---|---|---|
| ffmpeg 경로 추출 (호스트로 프레임 읽어내기) | 359~466 µs | 1453~9874 µs |
| MIL→MIL `MbufCopy` (정지화면 보관) | 132~149 µs | 195~542 µs |
| 타일 축약 (검출) | 168~241 µs | 764~2320 µs |

`MseqFeed`는 MIL 버퍼를 그대로 받으므로 **호스트 읽기가 없어야** 합니다. 즉 위 표의 두 번째 줄에
가까울 것이 기대치이고, 첫 줄만큼 든다면 그 이유를 알아야 합니다.

**2. 파일의 시간축이 참일 것.** 파일 duration이 실행의 실제 경과 시간과 일치해야 합니다.

이것이 이 프로젝트에서 가장 오래 숨어 있던 결함입니다. 120.0초 녹화가 ffprobe에서 **81.07초 /
184.06 fps**로 나왔고, **프레임은 14,922개 전부 들어 있었습니다.** 헤더만 거짓이라 1.48배 빨리
재생됐습니다. 원인 두 가지 모두 아래 함정에 있습니다.

**3. 넣은 프레임 수 = 파일의 프레임 수.** `Stats.FramesFed`와 ffprobe의 `nb_read_frames`가
같아야 합니다. 다르면 인코더가 버린 것이고, 사건 전후 구간을 잘라낼 수 없게 됩니다.

**4. GOP 제어가 가능할 것.** `M_STREAM_GROUP_OF_PICTURE_SIZE`로 키프레임 간격을 지정할 수
있어야 합니다. 목표는 **0.5초**(124.316 fps에서 62프레임).

**5. 가능하면: 재인코딩 없는 구간 잘라내기.** 기존 파일에서 시각 구간을 잘라내되 다시
인코딩하지 않는 방법이 MIL에 있는지 알려주십시오. ffmpeg는 `-c copy`로 하고, 실측 오차가
**1프레임(8 ms)** 이었습니다. 이것이 없으면 인코딩만 MIL로 하고 잘라내기는 ffmpeg에 남깁니다.

## 함정 — 전부 실측으로 겪은 것

**노출은 요청값이 아니라 `ResultingFrameRate`를 봐야 합니다.**
`AcquisitionFrameRate`는 **184.060**(요청)이고 `ResultingFrameRate`는 **124.316**(8000 µs 노출이
허용하는 값)입니다. `M_SELECTED_FRAME_RATE`도 요청값을 답합니다. 파일 헤더에 요청값을 쓰면 위의
1.48배 문제가 그대로 재현됩니다. 하네스가 두 값을 나란히 출력합니다.

**GenICam 쓰기는 read-back 하기 전까지 검증되지 않았습니다.**
`MdigControlFeature`는 예외를 던지지 않고, `M_PRINT_DISABLE` 상태에서는 출력도 없어서 감싼
헬퍼가 **true를 반환합니다.** 그런데 값은 그대로일 수 있습니다. `M_FEATURE_ACCESS_MODE`도 못
믿습니다 — **RW라고 답하면서 쓰기를 무시한 경우를 실측했습니다.**

**정수형 피처는 `M_TYPE_MIL_INT`로 써야 합니다.**
`DecimationHorizontal` / `DecimationVertical` / `AcquisitionFrameRate*`가 그렇습니다.
`M_TYPE_DOUBLE`로 쓰면 **조용히 무시됩니다.** `GenICamFeatures.SetInt`이 이를 처리합니다.

**`M_BAYER_CONVERSION`은 보드에 남는 영속 설정입니다.**
한 번 끄면 grab이 끝나도, 앱을 종료해도, **재부팅해도** 그대로입니다. 그 상태에서 컬러
파이프라인이 raw 데이터를 잘못 읽어 타일처럼 깨진 이미지가 나옵니다. `M_SIZE_BAND`를 조회하기
**전에** 다시 켜야 합니다 — 그 답이 이 설정에 종속됩니다.

**빈 포트에 `MdigAlloc`하면 모달 대화상자가 뜹니다.**
`M_THROW_EXCEPTION` 아래에서도 뜹니다. 보드는 카메라가 없어도 디지타이저 4개를 보고합니다.
`MappControl(M_ERROR, M_PRINT_DISABLE)`로 감싸야 하고, **반드시 복원**해야 합니다 — 안 그러면
이후 모든 MIL 오류가 조용히 사라집니다.

**`MbufGet`은 행 패딩을 그대로 복사합니다.**
pitch 2112 > width 2064입니다. 촘촘한 행으로 되읽으면 이미지가 사선으로 밀립니다. 논리적 W×H를
packed로 받아야 하면 **`MbufGet2d`** 를 쓰고, X 오프셋을 받는 형태도 그것뿐입니다.

**컬러 바이트를 뽑을 때 `MbufGetColor`를 쓰지 마십시오.**
이 버퍼들에서 패킹 경로가 멈추고, 플래나 경로는 **조용히 0만** 돌려줍니다. `MbufChildColor`로
밴드별 자식을 만들어 각각 `MbufGet`하고, 자식을 부모보다 **먼저** 해제해야 합니다.

**`MbufBayer`는 예외도 오류도 없이 블록합니다.**
취득 훅에서 호출하면 훅이 첫 프레임에서 멈추고, UI 스레드에서 호출하면 앱 전체가 정지합니다.

**`MimStat`은 Image Processing 모듈이 필요하고 이 장비에는 없습니다.**
`Mim*`이 하나의 라이선싱 그룹이 아닙니다 — `MimResize`·`MimShift`는 되고 `MimStat`은 안 됩니다.

**앱을 강제 종료하면 지오메트리 노드가 잠깁니다.**
작업 관리자 종료·디버거 중단·크래시 뒤로 `DecimationHorizontal` 같은 피처 쓰기가 **조용히
거부됩니다**(예외도 출력도 없고 반환값은 성공). **복구는 전원 재인가가 아니라 정상 종료 한
번**입니다. 검증 스크립트에서 `Process.Kill()`을 쓰지 마십시오.

**전원을 내리면 카메라 설정이 초기화됩니다.**
앱 종료와 다릅니다 — 노출이 5388 µs로, `AcquisitionFrameRateEnable`이 Off로 돌아옵니다.
하드웨어를 만진 뒤에는 다시 확인해야 합니다.

**취득 대역폭에 공유 천장이 있습니다 — 호스트 DMA 3.68 GB/s.**
채널 수와 무관하게 합계가 여기서 고정되고, 초과분은 `M_PROCESS_FRAME_MISSED`로 사라집니다.
현재 운전점은 3채널 합계 0.84 GiB/s로 여유가 있습니다.

## 하네스 사용법

```
dotnet build tools/MilVideoSink/MilVideoSink.csproj -c Release

MilVideoSink.exe --seconds 60 --sink mil
MilVideoSink.exe --seconds 60 --sink mil --segments      # 2초 세그먼트 + 0.5초 GOP
MilVideoSink.exe --seconds 60 --sink ffmpeg              # 동작하는 비교 대상
```

`--channel` / `--exposure` / `--decim` / `--out` 으로 다른 카메라에서도 돕니다.

`--sink ffmpeg`가 **현재 통과하는 기준선**입니다. 실측(2026-09-10, 15초):

```
grabbed   1865 frames, 124.34 fps, 0 missed
sink      ffmpeg/libx264: 1865 fed, 0 skipped, 0 dropped
rate      124.26 fps written, 124.316 declared, 124.316 deliverable
feed      mean 288 us, max 1290 us of the 8042 us frame period
```

세그먼트 8개 합계가 **1865프레임 = 넣은 수와 정확히 일치**했습니다.

## 현재 이 장비의 상태

```
MseqAlloc(M_DEFAULT, M_DEFAULT, M_SEQ_COMPRESS, M_DEFAULT, M_DEFAULT, ref seq)
  → M_NULL, 예외 없음
  → MIL error 0x68: Licensing error.
     / Your license does not allow the use of JPEG-compressed sequences.

MappInquire(M_LICENSE_MODULES)      = 0x1  (M_LICENSE_LITE)
MsysInquire(M_BOARD_TYPE)           = 0x200026e   (M_H264 비트 clear)
```

`MseqAlloc`은 **예외를 던지지 않고 `M_NULL`을 반환**하며, 오류 출력이 process-wide로 꺼져 있으니
이유는 `MappGetError(M_CURRENT + M_MESSAGE)`로 직접 읽어야 합니다. 앱은 시작할 때마다 이 판정을
로그에 한 줄 남깁니다.
