<#
.SYNOPSIS
    Moves finished continuous-recording segments from the local staging folder to the NAS.

.DESCRIPTION
    The app writes the continuous recording into <output>\continuous\ and never sends it anywhere;
    this is the other half. It copies each finished file, verifies it, and only then deletes the
    local copy - so a failure at any point costs a retry rather than a recording.

    Every rule below came out of a measurement on this rig, not a preference:

    * ONE FILE AT A TIME. The three cameras start together, so their segments close in the same
      second. Sending them in parallel makes one 900 MB burst out of three 300 MB ones.

    * IN SMALL BLOCKS, WITH A PAUSE BETWEEN THEM. Frame loss tracks burst intensity, not average
      throughput: a copy limited to 30 MB/s on average but still writing in full-speed bursts cost
      as many frames as an unlimited one (extract tail 19.5 ms against an 8.04 ms frame period).
      Copying at 334 MB/s cost 0.0089% of frames - about one anomaly window in ten with a hole in
      it. A single 300 MB transfer per boundary, serialized, cost nothing measurable.

    * NOTHING YOUNGER THAN A MINUTE. ffmpeg is still writing the newest segment, and a boundary is
      when it closes one file and opens another - the worst instant to add disk work.

    * VERIFY, THEN DELETE. A reliability test's recordings are controlled records; "it copied
      without an error" is not the same as "the bytes are there".

    The local folder is a staging area, and the app will delete the oldest files in it if the disk
    runs low. Keeping this running is what stops that from ever being needed.

    Remuxing .ts to .mp4 is deliberately NOT done here. The copy is verified by hash, which only
    means anything if the bytes are identical; convert on the NAS afterwards if a viewer needs it:
        ffmpeg -i seg.ts -c copy -movflags +faststart seg.mp4

.PARAMETER Destination
    Where the files go. A date folder is created under it from each file's timestamp.

.PARAMETER Source
    The staging folder. Read from the app's settings.json when omitted.

.PARAMETER RateMBps
    Ceiling for the copy, in MB/s. The default is 250 times what the measured content produces and
    3 times what a lit screen would; there is no reason to go faster and every reason not to.

.PARAMETER MinAgeSeconds
    Leave a file alone until it is this old.

.PARAMETER Once
    Do one pass and exit. Without it the script loops.

.PARAMETER WhatIf
    Say what would move, and move nothing.

.EXAMPLE
    .\move-to-nas.ps1 -Destination \\nas\dr\panel-a -Once -WhatIf
    .\move-to-nas.ps1 -Destination \\nas\dr\panel-a
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination,

    [string] $Source,

    [double] $RateMBps = 10.0,

    [int] $MinAgeSeconds = 60,

    [int] $IntervalSeconds = 30,

    [switch] $Once,

    # Compare sizes instead of hashing. Faster, and weaker: it catches a truncated copy but not a
    # corrupted one. For a controlled record, prefer the default.
    [switch] $FastVerify
)

$ErrorActionPreference = 'Stop'

# The app writes these; only they are moved. A .json or .csv is the record of a recording and
# travels with it - they are kilobytes and they outlive the video they describe.
$MediaExtensions  = @('.ts', '.mp4', '.mkv', '.mov', '.avi')
$RecordExtensions = @('.json', '.csv')

function Get-StagingFolderFromSettings {
    $settings = Join-Path $env:LOCALAPPDATA 'MatroxFrameGrabber\settings.json'
    if (-not (Test-Path $settings)) { return $null }
    try {
        $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($json.OutputFolder)) { return $null }
        return (Join-Path $json.OutputFolder 'continuous')
    } catch {
        return $null
    }
}

# Copies in blocks with a pause between them, so the instantaneous rate never spikes. This is the
# whole point of the script's pacing: the storage stack's tail latency is what costs frames, and
# the tail responds to how hard a burst hits rather than to the average over a minute.
function Copy-Paced {
    param([string] $From, [string] $To, [double] $RateMBps)

    $blockBytes = 1MB
    $secondsPerBlock = $blockBytes / ($RateMBps * 1MB)

    $in  = [System.IO.File]::OpenRead($From)
    try {
        $out = [System.IO.File]::Create($To)
        try {
            $buffer = New-Object byte[] $blockBytes
            while ($true) {
                $started = [Diagnostics.Stopwatch]::StartNew()
                $read = $in.Read($buffer, 0, $blockBytes)
                if ($read -le 0) { break }
                $out.Write($buffer, 0, $read)
                $out.Flush()

                $spent = $started.Elapsed.TotalSeconds
                $rest = $secondsPerBlock - $spent
                if ($rest -gt 0) { Start-Sleep -Milliseconds ([int]($rest * 1000)) }
            }
        } finally { $out.Dispose() }
    } finally { $in.Dispose() }
}

function Test-CopyMatches {
    param([string] $Left, [string] $Right, [switch] $SizeOnly)

    $l = Get-Item $Left
    $r = Get-Item $Right
    if ($l.Length -ne $r.Length) { return $false }
    if ($SizeOnly) { return $true }

    $lh = (Get-FileHash -Path $Left  -Algorithm SHA256).Hash
    $rh = (Get-FileHash -Path $Right -Algorithm SHA256).Hash
    return $lh -eq $rh
}

# An evidence dump is the one piece of disk work that must not be competed with: it happens at the
# moment a fault was detected, which is the moment the rig exists for. The app has no flag for it
# yet, so recent writes in the evidence folder stand in for one.
function Test-EvidenceBusy {
    param([string] $EvidenceFolder)

    if (-not (Test-Path $EvidenceFolder)) { return $false }
    $cutoff = (Get-Date).AddSeconds(-15)
    $recent = Get-ChildItem -Path $EvidenceFolder -File -ErrorAction SilentlyContinue |
              Where-Object { $_.LastWriteTime -gt $cutoff }
    return ($null -ne $recent) -and ($recent.Count -gt 0)
}

function Invoke-Pass {
    param([string] $From, [string] $To, [string] $EvidenceFolder)

    if (Test-EvidenceBusy -EvidenceFolder $EvidenceFolder) {
        Write-Verbose 'evidence is being written - waiting'
        return 0
    }

    $cutoff = (Get-Date).AddSeconds(-$MinAgeSeconds)
    $wanted = $MediaExtensions + $RecordExtensions
    $files = Get-ChildItem -Path $From -File -ErrorAction SilentlyContinue |
             Where-Object { $wanted -contains $_.Extension.ToLowerInvariant() } |
             Where-Object { $_.LastWriteTime -lt $cutoff } |
             Sort-Object LastWriteTime

    $moved = 0
    foreach ($f in $files) {          # one at a time, oldest first - never in parallel
        $dayFolder = Join-Path $To $f.LastWriteTime.ToString('yyyy-MM-dd')
        $target = Join-Path $dayFolder $f.Name
        $partial = "$target.part"

        if ($PSCmdlet.ShouldProcess($f.FullName, "move to $target") -eq $false) {
            Write-Host ("would move {0} ({1:N1} MB) -> {2}" -f $f.Name, ($f.Length / 1MB), $target)
            continue
        }

        try {
            if (-not (Test-Path $dayFolder)) { New-Item -ItemType Directory -Path $dayFolder -Force | Out-Null }
            if (Test-Path $partial) { Remove-Item $partial -Force }

            # Into a .part name and renamed on success, so nothing on the NAS side ever sees a
            # half-written file and takes it for a finished one.
            Copy-Paced -From $f.FullName -To $partial -RateMBps $RateMBps

            if (-not (Test-CopyMatches -Left $f.FullName -Right $partial -SizeOnly:$FastVerify)) {
                Remove-Item $partial -Force -ErrorAction SilentlyContinue
                Write-Warning ("verification failed, kept local: {0}" -f $f.Name)
                continue
            }

            if (Test-Path $target) { Remove-Item $target -Force }
            Rename-Item -Path $partial -NewName $f.Name
            Remove-Item $f.FullName -Force        # only now, and only after it verified
            $moved++
            Write-Host ("moved {0} ({1:N1} MB)" -f $f.Name, ($f.Length / 1MB))
        } catch {
            Write-Warning ("{0}: {1}" -f $f.Name, $_.Exception.Message)
            if (Test-Path $partial) { Remove-Item $partial -Force -ErrorAction SilentlyContinue }
        }
    }
    return $moved
}

# ----- main -----

if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Get-StagingFolderFromSettings
    if ([string]::IsNullOrWhiteSpace($Source)) {
        throw 'Could not read the staging folder from settings.json. Pass -Source.'
    }
}
if (-not (Test-Path $Source)) { throw "Staging folder not found: $Source" }

$evidence = Split-Path $Source -Parent    # the output folder, where clips and stills land

Write-Host "staging   : $Source"
Write-Host "evidence  : $evidence (writes here pause the mover)"
Write-Host "nas       : $Destination"
Write-Host ("pacing    : {0} MB/s in 1 MB blocks, one file at a time, nothing younger than {1}s" -f $RateMBps, $MinAgeSeconds)
Write-Host ("verify    : {0}" -f $(if ($FastVerify) { 'size only' } else { 'SHA-256' }))

while ($true) {
    $n = Invoke-Pass -From $Source -To $Destination -EvidenceFolder $evidence
    if ($n -gt 0) { Write-Host ("pass done - {0} file(s)" -f $n) }
    if ($Once) { break }
    Start-Sleep -Seconds $IntervalSeconds
}
