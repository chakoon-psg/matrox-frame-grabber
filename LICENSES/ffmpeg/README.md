# ffmpeg

이 프로그램은 녹화를 위해 `ffmpeg.exe`를 별도 프로세스로 실행하며, 실행 파일을 함께 배포한다.

- **버전**: 9.0-full_build
- **출처**: https://www.gyan.dev/ffmpeg/builds/ (`winget install Gyan.FFmpeg`)
- **라이선스**: GPL v3 (`COPYING.GPLv3`). 이 빌드는 `--enable-gpl --enable-libx264`로 구성돼 있다.
- **소스 코드**: https://github.com/FFmpeg/FFmpeg 및 위 배포처의 소스 아카이브.
  이 소프트웨어를 인도받은 자는 위 소스를 요청할 수 있다.

이 프로그램 자체는 ffmpeg를 라이브러리로 링크하지 않는다. 이 장비의 MIL 라이선스가 압축을
허용하지 않고 보드에 인코더도 없어서, 별도 프로세스로 호출하는 것이 유일한 경로였다.
