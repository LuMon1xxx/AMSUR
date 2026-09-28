param(
  [string]$Config = (Join-Path $PSScriptRoot 'promo.config.json'),
  [string]$OutputDir = (Join-Path $PSScriptRoot 'assets\audio')
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Speech

# Read config as UTF-8 (BOM-tolerant). No network, no secrets.
$configText = [System.IO.File]::ReadAllText($Config, [System.Text.Encoding]::UTF8)
$promo = $configText | ConvertFrom-Json

if (-not (Test-Path -LiteralPath $OutputDir)) {
  New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
  try {
    $synth.SelectVoice('Microsoft Irina Desktop')
  } catch {
    $available = ($synth.GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name }) -join ', '
    throw "Voice 'Microsoft Irina Desktop' not found. Installed voices: $available"
  }
  if ($null -eq $promo.voiceRate) { throw "promo.config.json must define top-level voiceRate (0..10)." }
  $voiceRate = [int]$promo.voiceRate
  if ($voiceRate -lt 0 -or $voiceRate -gt 10) { throw "voiceRate out of range 0..10: $voiceRate" }
  # Rate evidence: Irina at Rate=0 totals ~31.9s across the five clips and
  # exceeds every voice window; Rate=8 fits but runs ~2.4x fast (rushed,
  # intelligibility risk). voiceRate=4 from promo.config.json is the chosen
  # compromise: calmer pace that still fits each window with margin.
  # Deterministic fixed rate; no randomness, no network.
  $synth.Rate = $voiceRate

  foreach ($entry in $promo.voice) {
    # Overwrite only our own deterministic output; never delete anything.
    $outPath = Join-Path $OutputDir ("vo-{0}.wav" -f $entry.id)
    $escaped = [System.Security.SecurityElement]::Escape($entry.text)
    $ssml = "<speak version=`"1.0`" xmlns=`"http://www.w3.org/2001/10/synthesis`" xml:lang=`"ru-RU`"><voice name=`"Microsoft Irina Desktop`" xml:lang=`"ru-RU`">$escaped</voice></speak>"
    # NOTE: System.Speech has no SpeakSsmlToWaveFile method; the equivalent
    # is SetOutputToWaveFile + SpeakSsml. Overwrites only this deterministic
    # output; nothing else is created, deleted or touched.
    $synth.SetOutputToWaveFile($outPath)
    try {
      $synth.SpeakSsml($ssml)
    } finally {
      $synth.SetOutputToNull()
    }
    Write-Output $outPath
  }
} finally {
  $synth.Dispose()
}
