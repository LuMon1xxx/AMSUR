# AMSUR promo end-to-end render (plan Task 4, Step 4).
# PowerShell 5.1 compatible. Local tools only, no network, no secrets.
# Never deletes or cleans any user path or the timestamped Temp frames dir.

$ErrorActionPreference = 'Stop'

Push-Location $PSScriptRoot
try {
    $promoRoot = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($promoRoot)) { throw '$PSScriptRoot is empty.' }
    if (-not (Test-Path -LiteralPath $promoRoot -PathType Container)) { throw "promo root not found: $promoRoot" }

    $renderStart = Get-Date

    # Output dirs only after verifying $PSScriptRoot.
    $audioDir = Join-Path $promoRoot 'assets\audio'
    $outDir = Join-Path $promoRoot 'out'
    if (-not (Test-Path -LiteralPath $audioDir -PathType Container)) {
        New-Item -ItemType Directory -Path $audioDir | Out-Null
    }
    if (-not (Test-Path -LiteralPath $outDir -PathType Container)) {
        New-Item -ItemType Directory -Path $outDir | Out-Null
    }

    # Unique preapproved Temp frames dir only after verifying TEMP parent.
    $tempRoot = $env:TEMP
    if ([string]::IsNullOrWhiteSpace($tempRoot)) { throw '$env:TEMP is empty.' }
    if (-not (Test-Path -LiteralPath $tempRoot -PathType Container)) { throw "TEMP root not found: $tempRoot" }
    $tempBase = Join-Path $tempRoot 'opencode'
    if (-not (Test-Path -LiteralPath $tempBase -PathType Container)) {
        New-Item -ItemType Directory -Path $tempBase | Out-Null
    }
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $framesDir = Join-Path $tempBase ("amsur-promo-frames-" + $stamp)
    $suffix = 0
    while (Test-Path -LiteralPath $framesDir) {
        $suffix += 1
        $framesDir = Join-Path $tempBase ("amsur-promo-frames-" + $stamp + "-" + $suffix)
    }
    New-Item -ItemType Directory -Path $framesDir | Out-Null
    Write-Output ("frames dir: " + $framesDir)

    # Phase 1: pre-render contract. output.test is intentionally excluded:
    # the MP4 does not exist yet.
    & node --test tests\timeline.test.mjs tests\assets.test.mjs tests\audio.test.mjs
    if ($LASTEXITCODE -ne 0) { throw "pre-render tests failed with exit code $LASTEXITCODE" }

    # Phase 2: procedural music bed, exactly 30 s stereo 48 kHz.
    $musicOut = Join-Path $audioDir 'music.wav'
    & python make_music.py --output $musicOut --duration 30 --sample-rate 48000 --channels 2
    if ($LASTEXITCODE -ne 0) { throw "make_music.py failed with exit code $LASTEXITCODE" }

    # Phase 3: Russian TTS voice clips via voice.ps1.
    $configPath = Join-Path $promoRoot 'promo.config.json'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $promoRoot 'voice.ps1') -Config $configPath -OutputDir $audioDir
    if ($LASTEXITCODE -ne 0) { throw "voice.ps1 failed with exit code $LASTEXITCODE" }

    # Phase 4: deterministic capture, 30 s * 30 fps = 900 frames.
    & node capture.mjs --duration 30 --out $framesDir --width 1920 --height 1080 --fps 30
    if ($LASTEXITCODE -ne 0) { throw "capture.mjs failed with exit code $LASTEXITCODE" }
    $frameCount = @(Get-ChildItem -LiteralPath $framesDir -Filter 'frame_*.png').Count
    if ($frameCount -ne 900) { throw "expected 900 frames in $framesDir, got $frameCount" }

    # Phase 5: FFmpeg mux with the exact input order from the plan.
    # NOTE: installed FFmpeg 9.0 has no -filter_complex_script option
    # (verified via `ffmpeg -h long`; the *_script file options were dropped
    # upstream). The graph still lives canonically in audio-filter.txt and is
    # passed byte-identical via -filter_complex.
    $filterGraph = ((Get-Content -LiteralPath (Join-Path $promoRoot 'audio-filter.txt') -Raw) -replace '\r?\n', '').Trim()
    if ([string]::IsNullOrWhiteSpace($filterGraph)) { throw 'audio-filter.txt is empty.' }
    $mp4Path = Join-Path $outDir 'amsur-promo-1080p.mp4'
    $ffmpegArgs = @(
      '-y',
      '-framerate', '30',
      '-i', (Join-Path $framesDir 'frame_%04d.png'),
      '-i', (Join-Path $audioDir 'vo-hook.wav'),
      '-i', (Join-Path $audioDir 'vo-data.wav'),
      '-i', (Join-Path $audioDir 'vo-generate.wav'),
      '-i', (Join-Path $audioDir 'vo-choices.wav'),
      '-i', (Join-Path $audioDir 'vo-end.wav'),
      '-i', (Join-Path $audioDir 'music.wav'),
      '-filter_complex', $filterGraph,
      '-map', '0:v:0',
      '-map', '[aout]',
      '-vf', "subtitles=captions.srt:charenc=UTF-8:force_style='FontName=Segoe UI,FontSize=18,PrimaryColour=&H00FFFFFF,BackColour=&H80000000,BorderStyle=3,Outline=1,Shadow=0,Alignment=2,MarginV=20'",
      '-c:v', 'libx264',
      '-preset', 'medium',
      '-crf', '18',
      '-pix_fmt', 'yuv420p',
      '-r', '30',
      '-t', '30',
      '-c:a', 'aac',
      '-b:a', '192k',
      '-ar', '48000',
      '-movflags', '+faststart',
      $mp4Path
    )
    & ffmpeg @ffmpegArgs
    if ($LASTEXITCODE -ne 0) { throw "FFmpeg failed with exit code $LASTEXITCODE" }

    # Phase 6: copy sidecars byte-identical to out/. Never delete anything.
    Copy-Item -LiteralPath (Join-Path $promoRoot 'captions.srt') -Destination (Join-Path $outDir 'amsur-promo-captions.srt') -Force
    Copy-Item -LiteralPath (Join-Path $promoRoot 'description.txt') -Destination (Join-Path $outDir 'amsur-promo-description.txt') -Force

    # Phase 7: final ffprobe JSON and concise summary.
    & ffprobe -v error -show_streams -show_format -of json $mp4Path
    if ($LASTEXITCODE -ne 0) { throw "ffprobe failed with exit code $LASTEXITCODE" }
    $renderSecs = [int]((Get-Date).Subtract($renderStart).TotalSeconds)
    Write-Output ("render complete: frames=" + $frameCount + " render_s=" + $renderSecs + " mp4=" + $mp4Path)
} finally {
    Pop-Location
}
