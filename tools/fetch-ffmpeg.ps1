#requires -Version 5.1
# tools/ffmpeg/ffmpeg.exe 를 이 PC에 설치된 ffmpeg에서 채운다.
# 바이너리는 약 212MB이므로 저장소에 커밋하지 않는다.
# 설계: docs/superpowers/specs/2026-08-19-ffmpeg-bundling-design.md
$ErrorActionPreference = 'Stop'
# 한국어 메시지가 en-US 로캘 빌드 머신에서 물음표로 뭉개지지 않도록 출력 인코딩을 고정한다.
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()

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

# 최종 경로에 직접 쓰지 않는다. 212MB 복사가 중간에 끊기면 잘린 파일이 남고, 위의 존재 검사가
# 그것을 성공으로 오인해 영영 복구되지 않는다. 게다가 앱은 자기 폴더를 먼저 뒤지므로, 깨진
# 사본이 멀쩡한 시스템 ffmpeg를 계속 가리게 된다.
$staging = "$dest.tmp"
try {
    Copy-Item -LiteralPath $source -Destination $staging -Force
    $copied = (Get-Item -LiteralPath $staging).Length
    $expected = (Get-Item -LiteralPath $source).Length
    if ($copied -ne $expected) {
        throw "복사본 크기가 다릅니다: $copied != $expected"
    }
    Move-Item -LiteralPath $staging -Destination $dest -Force
}
catch {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Force }
    Write-Warning "ffmpeg.exe 복사에 실패했습니다: $($_.Exception.Message)"
    exit 1
}

Write-Host "복사함: $source"
Write-Host "     -> $dest"
