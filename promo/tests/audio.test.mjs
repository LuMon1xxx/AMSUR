import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import config from '../promo.config.json' with { type: 'json' };

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const promoDir = path.resolve(__dirname, '..');
const configPath = path.join(promoDir, 'promo.config.json');
const voicePs1 = path.join(promoDir, 'voice.ps1');
const makeMusicPy = path.join(promoDir, 'make_music.py');
const srtPath = path.join(promoDir, 'captions.srt');

function runCmd(cmd, args) {
  return spawnSync(cmd, args, { encoding: 'utf8' });
}

function ffprobeJson(filePath) {
  const res = spawnSync(
    'ffprobe',
    ['-v', 'error', '-show_streams', '-show_format', '-of', 'json', filePath],
    { encoding: 'utf8' }
  );
  assert.equal(
    res.status,
    0,
    `ffprobe failed for ${filePath}: status=${res.status} stderr=${res.stderr}`
  );
  return JSON.parse(res.stdout);
}

function streamDurationMs(probe) {
  const stream = probe.streams?.[0];
  const raw = stream?.duration ?? probe.format?.duration;
  const seconds = Number(raw);
  assert.ok(
    Number.isFinite(seconds) && seconds > 0,
    `expected positive duration, got: ${String(raw)}`
  );
  return { seconds, stream };
}

function parseSrtTime(token) {
  const m = token.match(/^(\d+):(\d{2}):(\d{2}),(\d{3})$/);
  assert.ok(m, `bad SRT timestamp: ${token}`);
  const hh = Number(m[1]);
  const mm = Number(m[2]);
  const ss = Number(m[3]);
  const ms = Number(m[4]);
  return ((hh * 60 + mm) * 60 + ss) * 1000 + ms;
}

function parseSrt(raw) {
  const text = raw.replace(/^\uFEFF/, '').replace(/\r\n/g, '\n');
  const blocks = text
    .split(/\n\n+/)
    .map((b) => b.trim())
    .filter((b) => b.length > 0);
  return blocks.map((block) => {
    const lines = block.split('\n');
    assert.ok(lines.length >= 3, `bad SRT block (need index, timing, text): ${block}`);
    const index = Number(lines[0].trim());
    const timing = lines[1].trim();
    const parts = timing.split(/\s*-->\s*/);
    assert.equal(parts.length, 2, `bad SRT timing line: ${timing}`);
    return {
      index,
      startMs: parseSrtTime(parts[0].trim()),
      endMs: parseSrtTime(parts[1].trim()),
      text: lines.slice(2).join('\n'),
    };
  });
}

const norm = (s) => s.replace(/\s+/g, ' ').trim();

function ffmpegF32Peak(wavPath) {
  const res = spawnSync(
    'ffmpeg',
    [
      '-v', 'error',
      '-i', wavPath,
      '-map', '0:a',
      '-f', 'f32le',
      '-acodec', 'pcm_f32le',
      '-ac', '2',
      '-ar', '48000',
      '-',
    ],
    { maxBuffer: 64 * 1024 * 1024 }
  );
  assert.equal(
    res.status,
    0,
    `ffmpeg decode failed for ${wavPath}: status=${res.status} stderr=${String(res.stderr).slice(0, 500)}`
  );
  const buf = res.stdout;
  assert.ok(buf && buf.length > 0, `expected decoded PCM bytes for ${wavPath}`);
  assert.ok(buf.length % 4 === 0, `expected f32le byte length multiple of 4, got ${buf.length}`);
  let peak = 0;
  for (let off = 0; off < buf.length; off += 4) {
    const v = Math.abs(buf.readFloatLE(off));
    if (v > peak) peak = v;
  }
  return peak;
}

test('voice clips, music bed and subtitles match promo.config.json', async () => {
  // 1) config voice: exactly 5 entries -> vo-<id>.wav; TTS rate pinned by root-cause ruling
  assert.equal(config.voiceRate, 4, `expected config.voiceRate 4, got ${String(config.voiceRate)}`);
  assert.ok(Array.isArray(config.voice), 'config.voice must be an array');
  assert.equal(config.voice.length, 5, `expected 5 voice entries, got ${config.voice.length}`);
  const ids = config.voice.map((v) => v.id);
  assert.equal(new Set(ids).size, ids.length, `voice ids must be unique: ${ids.join(', ')}`);
  for (const v of config.voice) {
    assert.equal(typeof v.id, 'string', `voice id must be a string: ${JSON.stringify(v)}`);
    assert.ok(v.id.length > 0, 'voice id must be non-empty');
    assert.ok(Number.isFinite(v.start) && Number.isFinite(v.end), `voice '${v.id}' needs numeric start/end`);
    assert.ok(v.end > v.start, `voice '${v.id}' needs end > start`);
    assert.ok(typeof v.text === 'string' && norm(v.text).length > 0, `voice '${v.id}' needs non-empty text`);
  }
  const expectedVoFiles = config.voice.map((v) => `vo-${v.id}.wav`);

  // 2) temp dir in os.tmpdir; spawn each generator exactly once
  const tmpDir = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'amsur-promo-audio-'));
  try {
    const musicPath = path.join(tmpDir, 'music.wav');

    const voiceRes = runCmd('powershell', [
      '-NoProfile',
      '-ExecutionPolicy', 'Bypass',
      '-File', voicePs1,
      '-Config', configPath,
      '-OutputDir', tmpDir,
    ]);
    assert.equal(
      voiceRes.status,
      0,
      `voice.ps1 failed: status=${voiceRes.status} stderr=${voiceRes.stderr} stdout=${voiceRes.stdout}`
    );

    const musicRes = runCmd('python', [
      makeMusicPy,
      '--output', musicPath,
      '--duration', '30',
      '--sample-rate', '48000',
      '--channels', '2',
    ]);
    assert.equal(
      musicRes.status,
      0,
      `make_music.py failed: status=${musicRes.status} stderr=${musicRes.stderr} stdout=${musicRes.stdout}`
    );

    // 3) ffprobe VO: non-empty, duration > 0, fits window with 100ms safety margin; exactly 5 vo-*.wav
    const entries = await fs.promises.readdir(tmpDir);
    const voFiles = entries.filter((n) => /^vo-.*\.wav$/.test(n)).sort();
    assert.deepEqual(voFiles.sort(), [...expectedVoFiles].sort(), 'expected exactly 5 vo-*.wav, no extras');
    assert.equal(voFiles.length, 5, `expected 5 vo files, got ${voFiles.length}: ${voFiles.join(', ')}`);

    for (const entry of config.voice) {
      const wavPath = path.join(tmpDir, `vo-${entry.id}.wav`);
      const stat = await fs.promises.stat(wavPath);
      assert.ok(stat.size > 0, `expected non-empty VO file: ${wavPath}`);
      const probe = ffprobeJson(wavPath);
      const { seconds } = streamDurationMs(probe);
      assert.ok(seconds > 0, `expected VO duration > 0 for ${entry.id}, got ${seconds}`);
      const window = entry.end - entry.start;
      assert.ok(
        seconds <= window - 0.1,
        `VO '${entry.id}' duration ${seconds}s exceeds window ${window}s minus 0.10s safety margin`
      );
    }

    // 4) music: PCM WAV 48kHz stereo, 30.0 +- 0.05s, peak < 0.25
    const musicStat = await fs.promises.stat(musicPath);
    assert.ok(musicStat.size > 0, `expected non-empty music file: ${musicPath}`);
    const musicProbe = ffprobeJson(musicPath);
    const musicStream = musicProbe.streams?.[0];
    assert.ok(musicStream, 'expected at least one audio stream in music.wav');
    assert.match(String(musicStream.codec_name), /^pcm/, `expected PCM codec, got ${musicStream.codec_name}`);
    assert.equal(Number(musicStream.sample_rate), 48000, `expected 48000 Hz, got ${musicStream.sample_rate}`);
    assert.equal(Number(musicStream.channels), 2, `expected 2 channels, got ${musicStream.channels}`);
    const musicSeconds = Number(musicStream.duration ?? musicProbe.format?.duration);
    assert.ok(
      Math.abs(musicSeconds - 30) <= 0.05,
      `expected music duration 30.0+-0.05s, got ${musicSeconds}`
    );
    const peak = ffmpegF32Peak(musicPath);
    assert.ok(peak < 0.25, `expected music peak < 0.25, got ${peak}`);

    // 5) SRT: exactly 5 blocks, normalized text == config voice text, timing within 1ms
    const srtRaw = await fs.promises.readFile(srtPath, 'utf8');
    const blocks = parseSrt(srtRaw);
    assert.equal(blocks.length, 5, `expected 5 SRT blocks, got ${blocks.length}`);
    blocks.forEach((b, i) => assert.equal(b.index, i + 1, `expected SRT index ${i + 1}, got ${b.index}`));
    blocks.forEach((b, i) => {
      const voice = config.voice[i];
      assert.equal(
        norm(b.text),
        norm(voice.text),
        `SRT block ${b.index} text mismatch: got ${JSON.stringify(norm(b.text))}, want ${JSON.stringify(norm(voice.text))}`
      );
      const wantStartMs = Math.round(voice.start * 1000);
      const wantEndMs = Math.round(voice.end * 1000);
      assert.ok(
        Math.abs(b.startMs - wantStartMs) <= 1,
        `SRT block ${b.index} start ${b.startMs}ms differs from voice ${wantStartMs}ms by >1ms`
      );
      assert.ok(
        Math.abs(b.endMs - wantEndMs) <= 1,
        `SRT block ${b.index} end ${b.endMs}ms differs from voice ${wantEndMs}ms by >1ms`
      );
    });
  } finally {
    // 6) cleanup only the temp dir
    await fs.promises.rm(tmpDir, { recursive: true, force: true });
  }
});
