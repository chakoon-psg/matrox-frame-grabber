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
