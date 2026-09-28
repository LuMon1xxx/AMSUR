#!/usr/bin/env python3
"""Deterministic procedural music bed for the AMSUR 30s promo.

Standard library only (argparse, wave, math, array, pathlib).
Output: 16-bit PCM stereo WAV of exactly --duration seconds.

Structure: slow four-chord pad (equal segments) with raised-cosine
crossfades between chords (no clicks), sparse high bell notes,
0.8 s intro fade and 1.5 s outro fade. Final peak is normalized
to 0.22 so the bed sits under narration (test requires peak < 0.25).

No external assets, no network, no randomness, no cleanup outside
the single target --output file.
"""

import argparse
import array
import math
import wave
from pathlib import Path

INTRO_FADE_S = 0.8
OUTRO_FADE_S = 1.5
TARGET_PEAK = 0.22
CROSSFADE_S = 0.6

# Four slow pad chords (Hz): C - Am - F - G.
CHORDS = (
    (130.81, 164.81, 196.00, 261.63),
    (110.00, 130.81, 164.81, 220.00),
    (87.31, 110.00, 130.81, 174.61),
    (98.00, 123.47, 146.83, 196.00),
)

# Sparse high bell notes: (start_seconds, freq_hz, length_seconds).
SPARKS = (
    (4.0, 659.25, 2.5),
    (11.5, 783.99, 2.5),
    (19.5, 880.00, 2.5),
    (26.0, 1046.50, 2.5),
)
SPARK_AMP = 0.05
SPARK_DECAY = 1.6


def raised_cosine(x):
    """Smooth 0..1 ramp for x in [0, 1]."""
    return 0.5 - 0.5 * math.cos(math.pi * x)


def chord_weights(n_chords, n_frames, sample_rate, seg_len):
    """Per-chord weight arrays with constant-sum raised-cosine crossfades.

    Each internal boundary gets a CROSSFADE_S window centred on it:
    the outgoing chord fades raised_cosine(1-k), the incoming chord
    fades raised_cosine(k), so their sum is exactly 1 (no dips/clicks).
    First chord starts at weight 1 (global intro fade handles t=0),
    last chord ends at weight 1 (global outro fade handles the end).
    """
    half = CROSSFADE_S / 2.0
    weights = []
    for ci in range(n_chords):
        start = ci * seg_len
        end = start + seg_len
        w = array.array("d", [0.0]) * n_frames
        for i in range(n_frames):
            t = i / sample_rate
            if t < start - half or t >= end + half:
                continue
            v = 1.0
            if ci > 0 and t < start + half:
                v = raised_cosine((t - (start - half)) / CROSSFADE_S)
            if ci < n_chords - 1 and t >= end - half:
                v = min(v, raised_cosine(((end + half) - t) / CROSSFADE_S))
            w[i] = v
        weights.append(w)
    return weights


def render(duration, sample_rate):
    n_frames = int(round(duration * sample_rate))
    seg_len = duration / len(CHORDS)
    two_pi = 2.0 * math.pi
    weights = chord_weights(len(CHORDS), n_frames, sample_rate, seg_len)

    mix = array.array("d", [0.0]) * n_frames

    for i in range(n_frames):
        t = i / sample_rate
        sample = 0.0
        for ci, chord in enumerate(CHORDS):
            w = weights[ci][i]
            if w <= 0.0:
                continue
            voice = 0.0
            for f in chord:
                phase = two_pi * f * t
                voice += math.sin(phase) + 0.15 * math.sin(2.0 * phase)
            voice *= 0.125  # 4 notes x (1 + 0.15) harmonics, kept soft
            # Gentle deterministic swell inside each segment.
            swell = 1.0 + 0.08 * math.sin(two_pi * 0.15 * t + ci)
            sample += w * voice * swell
        for st, freq, length in SPARKS:
            dt = t - st
            if 0.0 <= dt < length:
                attack = min(1.0, dt / 0.02)
                bell = attack * math.exp(-dt * SPARK_DECAY)
                sample += SPARK_AMP * bell * math.sin(two_pi * freq * t)
        mix[i] = sample

    peak = max(1e-9, max(abs(v) for v in mix))
    gain = TARGET_PEAK / peak

    intro_n = int(round(INTRO_FADE_S * sample_rate))
    outro_n = int(round(OUTRO_FADE_S * sample_rate))

    out = array.array("h")
    for i in range(n_frames):
        v = mix[i] * gain
        if i < intro_n:
            v *= raised_cosine(i / intro_n)
        tail = n_frames - 1 - i
        if tail < outro_n:
            v *= raised_cosine(tail / outro_n)
        v = max(-1.0, min(1.0, v))
        out.append(int(round(v * 32767)))
    return out


def main():
    parser = argparse.ArgumentParser(
        description="Render deterministic AMSUR promo music bed (stdlib only)."
    )
    parser.add_argument("--output", required=True)
    parser.add_argument("--duration", type=float, default=30)
    parser.add_argument("--sample-rate", type=int, default=48000)
    parser.add_argument("--channels", type=int, default=2)
    args = parser.parse_args()

    mono = render(args.duration, args.sample_rate)

    output = Path(args.output)
    if output.parent and str(output.parent) not in ("", "."):
        output.parent.mkdir(parents=True, exist_ok=True)

    frames = array.array("h")
    for s in mono:
        for _ in range(args.channels):
            frames.append(s)

    with wave.open(str(output), "wb") as wf:
        wf.setnchannels(args.channels)
        wf.setsampwidth(2)
        wf.setframerate(args.sample_rate)
        wf.writeframes(frames.tobytes())

    print(str(output))


if __name__ == "__main__":
    main()
