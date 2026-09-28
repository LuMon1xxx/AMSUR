import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const promoDir = path.resolve(__dirname, '..');
const outDir = path.join(promoDir, 'out');

const mp4Path = path.join(outDir, 'amsur-promo-1080p.mp4');
const outSrtPath = path.join(outDir, 'amsur-promo-captions.srt');
const outDescPath = path.join(outDir, 'amsur-promo-description.txt');
const srcSrtPath = path.join(promoDir, 'captions.srt');
const srcDescPath = path.join(promoDir, 'description.txt');

const RENDER_HINT = 'output missing — run the render pipeline to produce out/ artifacts (this test never renders)';

function statFile(filePath, hint) {
  assert.ok(fs.existsSync(filePath), `missing file: ${filePath} — ${hint}`);
  const stat = fs.statSync(filePath);
  assert.ok(stat.isFile(), `not a regular file: ${filePath}`);
  return stat;
}

function readFile(filePath, hint) {
  statFile(filePath, hint);
  return fs.readFileSync(filePath);
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

test('expected outputs exist, are non-empty, MP4 >100KB', () => {
  const mp4Stat = statFile(mp4Path, RENDER_HINT);
  assert.ok(mp4Stat.size > 0, `expected non-empty MP4: ${mp4Path}`);
  assert.ok(
    mp4Stat.size > 100 * 1024,
    `expected MP4 >100KB, got ${mp4Stat.size} bytes: ${mp4Path}`
  );
  const srtStat = statFile(outSrtPath, RENDER_HINT);
  assert.ok(srtStat.size > 0, `expected non-empty SRT: ${outSrtPath}`);
  const descStat = statFile(outDescPath, RENDER_HINT);
  assert.ok(descStat.size > 0, `expected non-empty description: ${outDescPath}`);
});

test('output SRT is byte-identical to source captions', () => {
  const src = readFile(srcSrtPath, 'source captions missing — expected promo/captions.srt');
  assert.ok(src.length > 0, `expected non-empty source captions: ${srcSrtPath}`);
  const out = readFile(outSrtPath, RENDER_HINT);
  assert.deepEqual(
    out,
    src,
    `output SRT differs from source captions (out ${out.length} bytes vs source ${src.length} bytes)`
  );
});

test('output description equals source description', () => {
  const src = readFile(srcDescPath, 'source description missing — expected promo/description.txt');
  assert.ok(src.length > 0, `expected non-empty source description: ${srcDescPath}`);
  const out = readFile(outDescPath, RENDER_HINT);
  assert.deepEqual(
    out,
    src,
    `output description differs from source (out ${out.length} bytes vs source ${src.length} bytes)`
  );
});

test('ffprobe: exactly one h264 1080p30 video and one AAC 48kHz stereo audio, ~30s', () => {
  statFile(mp4Path, RENDER_HINT);
  const probe = ffprobeJson(mp4Path);
  const streams = probe.streams ?? [];
  const video = streams.filter((s) => s.codec_type === 'video');
  const audio = streams.filter((s) => s.codec_type === 'audio');
  assert.equal(video.length, 1, `expected exactly one video stream, got ${video.length}`);
  assert.equal(audio.length, 1, `expected exactly one audio stream, got ${audio.length}`);

  const v = video[0];
  assert.equal(v.codec_name, 'h264', `expected video codec h264, got ${v.codec_name}`);
  assert.equal(Number(v.width), 1920, `expected video width 1920, got ${v.width}`);
  assert.equal(Number(v.height), 1080, `expected video height 1080, got ${v.height}`);
  assert.equal(v.pix_fmt, 'yuv420p', `expected pix_fmt yuv420p, got ${v.pix_fmt}`);
  assert.equal(v.r_frame_rate, '30/1', `expected r_frame_rate 30/1, got ${v.r_frame_rate}`);
  assert.equal(Number(v.nb_frames), 900, `expected nb_frames 900, got ${v.nb_frames}`);

  const a = audio[0];
  assert.equal(a.codec_name, 'aac', `expected audio codec aac, got ${a.codec_name}`);
  assert.equal(Number(a.sample_rate), 48000, `expected sample_rate 48000, got ${a.sample_rate}`);
  assert.equal(Number(a.channels), 2, `expected 2 audio channels, got ${a.channels}`);

  const formatSeconds = Number(probe.format?.duration);
  assert.ok(
    Number.isFinite(formatSeconds),
    `expected finite format duration, got ${String(probe.format?.duration)}`
  );
  assert.ok(
    formatSeconds >= 29.9 && formatSeconds <= 30.1,
    `expected format duration 29.90..30.10s, got ${formatSeconds}`
  );
  for (const [label, s] of [['video', v], ['audio', a]]) {
    if (s.duration !== undefined) {
      const sd = Number(s.duration);
      assert.ok(
        Number.isFinite(sd) && sd >= 29.0 && sd <= 31.0,
        `expected sane ${label} stream duration 29.0..31.0s, got ${String(s.duration)}`
      );
    }
  }
});

test('loudness: integrated -16±1 LUFS, reported peak <= -1.0 dB', () => {
  statFile(mp4Path, RENDER_HINT);
  const res = spawnSync(
    'ffmpeg',
    [
      '-v', 'info',
      '-i', mp4Path,
      '-filter_complex', 'ebur128=peak=true',
      '-f', 'null', '-',
    ],
    { encoding: 'utf8' }
  );
  assert.equal(
    res.status,
    0,
    `ffmpeg ebur128 failed for ${mp4Path}: status=${res.status} stderr=${String(res.stderr).slice(0, 500)}`
  );
  const log = String(res.stderr);
  const integrated = [...log.matchAll(/I:\s+(-?\d+(?:\.\d+)?)\s+LUFS/g)].map((m) => Number(m[1]));
  const peaks = [...log.matchAll(/Peak:\s+(-?\d+(?:\.\d+)?)\s+dB(?:TP|FS)/g)].map((m) => Number(m[1]));
  assert.ok(integrated.length > 0, `no ebur128 Integrated loudness summary found in ffmpeg output`);
  assert.ok(peaks.length > 0, `no ebur128 reported peak summary found in ffmpeg output`);
  const integratedLufs = integrated.at(-1);
  const reportedPeakDb = peaks.at(-1);
  assert.ok(
    Math.abs(integratedLufs - (-16)) <= 1,
    `expected Integrated loudness -16±1 LUFS, got ${integratedLufs}`
  );
  assert.ok(
    reportedPeakDb <= -1.0,
    `expected reported peak <= -1.0 dB, got ${reportedPeakDb}`
  );
});
