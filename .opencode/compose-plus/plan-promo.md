# AMSUR Promo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Создать воспроизводимый бесплатный 30-секундный MP4-проморолик AMSUR в формате 1920×1080 с реальным UI, русской TTS-озвучкой, субтитрами, музыкой и проверяемыми claims.

**Architecture:** Один неизменяемый JSON-таймлайн управляет HTML/CSS/JS-композицией и тестами. Playwright через системный Edge детерминированно снимает PNG-кадры в Temp; локальные TTS/music клипы и FFmpeg собирают финальный MP4. Продуктовый WPF-код не меняется.

**Tech Stack:** HTML/CSS/JavaScript, Node.js 24, Playwright, Microsoft Edge, PowerShell 5.1 `System.Speech`, Microsoft Irina `ru-RU`, Python 3.11 standard library, FFmpeg/FFprobe 9.

**Spec:** `.opencode/compose-plus/promo-design.md`

## Global Constraints

- Рабочая папка: `D:\LuMon1x\AMSUR`.
- Создавать только `promo/**` и promo-артефакты `.opencode/compose-plus/**`; не менять `src/**`, `данные/**`, WPF, алгоритм и тесты проекта.
- Итог: ровно 30 секунд, 1920×1080, 30 fps, H.264 `yuv420p`, AAC 48 kHz.
- Только бесплатные локальные инструменты; никаких платных API/сервисов, шрифтов или музыки.
- Реальные UI PNG и логотип читаются из существующих путей и не перерисовываются.
- Не показывать hero claim «140 vs 139», «победа над человеком», «оптимальное/идеальное» или «0 окон вообще».
- `~0,1 с` всегда показывается вместе с greedy-footnote и `12 с` Standard budget.
- `0 окон` всегда формулируется как `0 окон у учеников` и ограничивается эталоном 40 классов / 100 учителей / 1196 уроков.
- Крупный текст ≥48 px, footnote ≥24 px, safe area 8%, без strobe/резких зумов.
- В репозитории уже есть пользовательские изменения; commits не выполнять без отдельной команды.
- Preflight hash tracked diff: `2eb4b7ad92090a2658ec898e60d84834c279c5b6`.

---

## File Map

**Create by cp-implementer:**

- `promo/package.json` — scripts и точная dev-зависимость Playwright.
- `promo/package-lock.json` — зафиксированное разрешение зависимостей.
- `promo/promo.config.json` — единственный источник 7 сцен, текстов, VO, asset paths и токенов.
- `promo/index.html` — фиксированная сцена 1920×1080.
- `promo/styles.css` — layout, safe area, motion-safe transitions.
- `promo/scene.js` — чистая функция `window.renderFrame(timeSeconds, config)`.
- `promo/capture.mjs` — capture PNG-последовательности Playwright/system Edge.
- `promo/voice.ps1` — пять Microsoft Irina WAV-клипов.
- `promo/make_music.py` — 30-секундный stereo WAV из standard library.
- `promo/audio-filter.txt` — FFmpeg voice delays, mix, ducking, loudnorm.
- `promo/captions.srt` — дословные русские субтитры.
- `promo/description.txt` — описание с полными оговорками.
- `promo/render.ps1` — end-to-end reproducible render.
- Generated: `promo/assets/audio/*.wav`, `promo/out/*.mp4`, `promo/out/*.srt`, `promo/out/*.txt`.

**Create by cp-tester only:**

- `promo/tests/timeline.test.mjs`
- `promo/tests/assets.test.mjs`
- `promo/tests/audio.test.mjs`
- `promo/tests/output.test.mjs`

**Compose+ artifacts:**

- `.opencode/compose-plus/screens/promo-v1-*.png` — 7 Vision QA keyframes.
- `.opencode/compose-plus/acceptance-promo.md` — финальный acceptance report.

---

### Task 1: Timeline and copy contract

**Files:**
- Create by cp-tester: `promo/tests/timeline.test.mjs`
- Create by cp-implementer: `promo/package.json`, `promo/promo.config.json`, `promo/captions.srt`

**Interfaces:**
- Produces: JSON scenes `{id,start,end,asset,headline,subhead,footnote,badge}`; VO `{id,start,end,text}`; `window.renderFrame` later consumes this object.
- Consumes: `.opencode/compose-plus/promo-design.md`.

- [ ] **Step 1: cp-implementer creates only the directory scaffold and package metadata**

Create directories `promo/` and `promo/tests/`, then create this minimal `promo/package.json`:

```json
{
  "name": "amsur-promo",
  "private": true,
  "type": "module",
  "scripts": {
    "test": "node --test \"tests/*.test.mjs\"",
    "capture": "node capture.mjs",
    "render": "powershell -NoProfile -ExecutionPolicy Bypass -File render.ps1"
  }
}
```

Do not create `promo.config.json` yet; the next step must be RED.

- [ ] **Step 2: cp-tester writes the failing timeline contract**

Test exact invariants:

```js
import test from 'node:test';
import assert from 'node:assert/strict';
import config from '../promo.config.json' with { type: 'json' };

test('timeline is contiguous and exactly 30 seconds', () => {
  assert.equal(config.width, 1920);
  assert.equal(config.height, 1080);
  assert.equal(config.fps, 30);
  assert.equal(config.duration, 30);
  assert.equal(config.scenes.length, 7);
  assert.equal(config.scenes[0].start, 0);
  assert.equal(config.scenes.at(-1).end, 30);
  config.scenes.forEach((scene, index) => {
    assert.ok(scene.end > scene.start);
    if (index > 0) assert.equal(scene.start, config.scenes[index - 1].end);
  });
});

test('claims keep their required scope', () => {
  const text = JSON.stringify(config).toLowerCase();
  assert.match(text, /0 окон у учеников/);
  assert.match(text, /1196/);
  assert.match(text, /12 с/);
  assert.doesNotMatch(text, /оптимальное расписание|идеальное расписание|победа над человеком|140.{0,10}139/);
});
```

- [ ] **Step 3: Run the test and verify RED**

Run from `D:\LuMon1x\AMSUR\promo`:

```powershell
node --test tests\timeline.test.mjs
```

Expected: FAIL because `promo.config.json` does not exist.

- [ ] **Step 4: cp-implementer creates the exact scene, VO and caption contract**

`promo.config.json` must use these boundaries, assets and claims:

```json
{
  "width": 1920,
  "height": 1080,
  "fps": 30,
  "duration": 30,
  "scenes": [
    {"id":"hook","start":0,"end":3,"asset":null,"headline":"Сотни уроков. Десятки ограничений. Одно расписание."},
    {"id":"data","start":3,"end":7,"asset":"../.opencode/compose-plus/screens/v2-final.png","headline":"Загрузите нагрузку"},
    {"id":"generate","start":7,"end":13,"asset":"../.opencode/compose-plus/screens/v2-generate.png","headline":"Первый рабочий вариант ~0,1 с","subhead":"Режим “Стандарт”: лимит поиска — 12 с","footnote":"~0,1 с — первый greedy-вариант на эталоне 1196 уроков; время зависит от ПК и режима"},
    {"id":"quality","start":13,"end":18,"asset":"../.opencode/compose-plus/screens/v2-schedule-matrix.png","headline":"0 окон у учеников · 0 жёстких нарушений","subhead":"Эталон: 40 классов · 100 учителей · 1196 уроков","badge":"Окна учителей — отдельная метрика"},
    {"id":"choices","start":18,"end":23,"asset":"../.opencode/compose-plus/screens/v2-generate.png","headline":"До пяти объяснённых вариантов"},
    {"id":"roles","start":23,"end":27,"asset":null,"headline":"Не замена завуча. Освобождение от рутины"},
    {"id":"end","start":27,"end":30,"asset":"../stitch_amsur_desktop_design_system/amsur_logo/screen.png","headline":"АМСУР. Человек решает. Программа считает.","footnote":"Показываем лучшее найденное, не “оптимальное”. СанПиН-блок требует сверки с НПА"}
  ],
  "voiceRate": 4,
  "voice": [
    {"id":"hook","start":0.25,"end":2.75,"text":"Расписание — не просто таблица"},
    {"id":"data","start":3.2,"end":6.8,"text":"Загрузите нагрузку — АМСУР проверит данные"},
    {"id":"generate","start":7.25,"end":12.75,"text":"Первый вариант — почти мгновенно. Стандартный поиск — до двенадцати секунд"},
    {"id":"choices","start":18.2,"end":22.8,"text":"АМСУР объясняет варианты. Человек выбирает"},
    {"id":"end","start":27.0,"end":29.85,"text":"Человек решает — АМСУР считает"}
  ]
}
```

`promo/captions.srt` must be:

```srt
1
00:00:00,250 --> 00:00:02,750
Расписание — не просто таблица

2
00:00:03,200 --> 00:00:06,800
Загрузите нагрузку — АМСУР проверит данные

3
00:00:07,250 --> 00:00:12,750
Первый вариант — почти мгновенно.
Стандартный поиск — до двенадцати секунд

4
00:00:18,200 --> 00:00:22,800
АМСУР объясняет варианты. Человек выбирает

5
00:00:27,000 --> 00:00:29,850
Человек решает — АМСУР считает
```

Then run `npm install --save-dev --save-exact playwright` to create the exact dependency and lockfile. Do not install a browser package; use system Edge.

- [ ] **Step 5: cp-tester runs the contract GREEN**

```powershell
node --test tests\timeline.test.mjs
```

Expected: PASS, 2 tests.

- [ ] **Step 6: Review gate**

Read the config and subtitles. Verify no scene exceeds two headline lines and every footnote is readable at 1920×1080.

---

### Task 2: Visual renderer and deterministic capture

**Files:**
- Create by cp-tester: `promo/tests/assets.test.mjs`
- Create by cp-implementer: `promo/index.html`, `promo/styles.css`, `promo/scene.js`, `promo/capture.mjs`

**Interfaces:**
- Produces: `window.renderFrame(timeSeconds, config) -> {sceneId, progress}` and `captureFrames(options) -> {count,outDir}`.
- Consumes: `promo.config.json`, existing screenshots, system Edge.

- [ ] **Step 1: cp-tester writes failing asset and capture tests**

Tests must assert:

```js
// every config asset resolves relative to promo/
// captureFrames({duration:1,fps:1,width:1920,height:1080,outDir}) returns count 1
// produced PNG is a non-empty file
// renderFrame(10, config).sceneId === 'generate'
```

Use `fs.mkdtemp(path.join(os.tmpdir(), 'amsur-promo-test-'))`; do not write test artifacts into the repository.

- [ ] **Step 2: Run and verify RED**

```powershell
node --test tests\assets.test.mjs
```

Expected: FAIL because renderer/capture files do not exist.

- [ ] **Step 3: cp-implementer builds the visual system**

Use these existing assets without copying personal data:

```text
../stitch_amsur_desktop_design_system/amsur_logo/screen.png
../.opencode/compose-plus/screens/v2-final.png
../.opencode/compose-plus/screens/v2-generate.png
../.opencode/compose-plus/screens/v2-schedule-matrix.png
```

Required design tokens:

```css
:root {
  --bg: #f8fafc;
  --surface: #ffffff;
  --accent: #4f46e5;
  --text: #0f172a;
  --muted: #475569;
  --success: #059669;
  --safe-x: 8%;
  --safe-y: 8%;
}
```

`scene.js` must select scene by `start <= t < end`, calculate local progress, and use only opacity/transform transitions. It must not generate fake controls or change screenshot content.

- [ ] **Step 4: cp-implementer implements `capture.mjs`**

Launch system Edge first:

```js
const edge = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const browser = await chromium.launch({ executablePath: fs.existsSync(edge) ? edge : undefined });
```

Capture pattern for every frame:

```js
await page.goto(pathToFileURL(indexPath).href, { waitUntil: 'load' });
await page.evaluate(({ config, t }) => window.renderFrame(t, config), { config, t: frame / fps });
await page.evaluate(async () => { await document.fonts.ready; });
await page.screenshot({ path: path.join(outDir, `frame_${String(frame).padStart(4, '0')}.png`) });
```

Export `captureFrames`; run CLI only when `import.meta.url === pathToFileURL(process.argv[1]).href`.

- [ ] **Step 5: cp-tester runs capture GREEN**

```powershell
node --test tests\assets.test.mjs
```

Expected: PASS; one non-empty 1920×1080 PNG.

- [ ] **Step 6: Technical 5-second spike**

```powershell
node capture.mjs --duration 5 --out "C:\Users\C39C~1\AppData\Local\Temp\opencode\amsur-promo-spike" --width 1920 --height 1080 --fps 30
ffmpeg -framerate 30 -i "C:\Users\C39C~1\AppData\Local\Temp\opencode\amsur-promo-spike\frame_%04d.png" -c:v libx264 -pix_fmt yuv420p -r 30 "C:\Users\C39C~1\AppData\Local\Temp\opencode\amsur-promo-spike\spike.mp4"
```

Expected: 150 frames, valid H.264 video, Cyrillic visible, no missing assets.

- [ ] **Step 7: Review gate**

Extract `0.0s`, `1.5s`, `3.5s`, `4.9s` spike frames. Verify brand, safe area, screenshot fidelity and typography before full render.

---

### Task 3: Voice, music and subtitle media

**Files:**
- Create by cp-tester: `promo/tests/audio.test.mjs`
- Create by cp-implementer: `promo/voice.ps1`, `promo/make_music.py`

**Interfaces:**
- Produces: `vo-hook.wav`, `vo-data.wav`, `vo-generate.wav`, `vo-choices.wav`, `vo-end.wav`, `music.wav`.
- Consumes: `promo.config.json`.

- [ ] **Step 1: cp-tester writes failing audio tests**

Spawn PowerShell/Python once, then use `ffprobe -v error -show_streams -show_format -of json`. Assert:

- `promo.config.json.voiceRate` equals 4;
- all five VO files exist and are non-empty;
- every VO clip ends at least 0.10 s before its configured `voice.end`, while still starting inside the scene;
- SRT text/timing match the five config voice entries;
- music duration is 30.0 ±0.05 s, 48 kHz, stereo;
- no VO duration is zero and no clip exceeds 6 s.

- [ ] **Step 2: Run and verify RED**

```powershell
node --test tests\audio.test.mjs
```

Expected: FAIL because voice/music scripts do not exist.

- [ ] **Step 3: cp-implementer implements TTS**

`voice.ps1` must:

1. load `promo.config.json`;
2. select `Microsoft Irina Desktop`;
3. use `SetOutputToWaveFile` + `SpeakSsml`, then reset with `SetOutputToNull`;
4. escape XML text;
5. write deterministic file names to `assets\audio`;
6. print each clip path.

Use `Rate = 4` from `promo.config.json.voiceRate`; the shorter copy keeps every clip at least 0.10 s inside its window. Do not use Rate=8: measured ~2.4× natural speed and poor intelligibility risk. Do not add external files or secrets.

- [ ] **Step 4: cp-implementer implements procedural music**

`make_music.py` uses only `wave`, `math`, `array`, `pathlib`. Output 48 kHz stereo PCM WAV, exactly 30 seconds, a slow four-chord pad, sparse high notes, 0.8 s intro fade and 1.5 s outro fade. Keep peak below 0.25 so it can sit under narration.

- [ ] **Step 5: cp-tester runs audio GREEN**

```powershell
node --test tests\audio.test.mjs
```

Expected: PASS.

- [ ] **Step 6: Spoken-copy gate**

Verify that every TTS clip is generated from the matching `voice` text, starts within its configured 50 ms tolerance and fits before `voice.end`. FFmpeg creates the final voice/music mix in Task 4; no standalone `voice-master.wav` is required. Technical duration/loudness checks must pass, but subjective intelligibility remains a user-review item.

---

### Task 4: End-to-end render and output contract

**Files:**
- Create by cp-tester: `promo/tests/output.test.mjs`
- Create by cp-implementer: `promo/audio-filter.txt`, `promo/description.txt`, `promo/render.ps1`

**Interfaces:**
- Consumes: Task 1 config/captions, Task 2 renderer, Task 3 WAVs.
- Produces: `promo/out/amsur-promo-1080p.mp4`, copied SRT/description, final audio mix.

- [ ] **Step 1: cp-tester writes failing output test**

The test spawns `ffprobe` and asserts:

```text
codec_name=h264
width=1920
height=1080
r_frame_rate=30/1
pix_fmt=yuv420p
audio codec=aac
sample_rate=48000
duration=29.90..30.10
```

It also requires non-empty MP4, SRT and description. Run FFmpeg `ebur128=peak=true` against the final MP4 and assert integrated loudness is −16 ±1.0 LUFS. Accept the tool-reported final peak unit `dBFS` or `dBTP` (FFmpeg 9 Windows build prints `dBFS`) and assert it is no higher than −1.0.

- [ ] **Step 2: Run and verify RED**

Expected: FAIL because `out/amsur-promo-1080p.mp4` does not exist.

- [ ] **Step 3: cp-implementer writes the exact audio graph**

`audio-filter.txt` must be:

```text
[1:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay=250|250[vo1];
[2:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay=3200|3200[vo2];
[3:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay=7250|7250[vo3];
[4:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay=18200|18200[vo4];
[5:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay=27000|27000[vo5];
[vo1][vo2][vo3][vo4][vo5]amix=inputs=5:duration=longest:dropout_transition=0:normalize=0[voice];
[voice]asplit=2[voice_sc][voice_mix];
[6:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume=0.12[music];
[music][voice_sc]sidechaincompress=threshold=0.03:ratio=8:attack=20:release=350[ducked];
[voice_mix][ducked]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,loudnorm=I=-16:TP=-1.5:LRA=11,alimiter=limit=0.95[aout]
```

Input indexes must match the explicit FFmpeg order in Step 4. If VO timing changes, config, captions, test expectations and this filter must change together.

Create `promo/description.txt` with this exact factual text:

```text
АМСУР — бесплатный русскоязычный офлайн-инструмент для автоматического составления школьного расписания: нагрузка → генерация → до пяти объяснённых вариантов → ручная правка → Excel.

В ролике показан эталон типовой двухсменной школы: 40 классов, 100 учителей, 1196 уроков. На этом эталоне зафиксированы 0 окон и 0 поздних стартов у учеников, Hard=0. Окна учителей оцениваются отдельной методикой и не скрываются.

~0,1 с — время первого рабочего greedy-варианта на указанном эталоне; это не полное качественное решение. Режим «Стандарт» имеет лимит поиска 12 с. Время зависит от данных, режима и ПК.

Показывается лучшее найденное расписание, а не математически «оптимальное». СанПиН-блок программы требует сверки с НПА; нормативная корректность не заявляется.
```

- [ ] **Step 4: cp-implementer implements `render.ps1`**

`render.ps1` must `Push-Location $PSScriptRoot`, create a unique preapproved Temp frame directory, and run these phases in order:

```powershell
node --test tests\timeline.test.mjs tests\assets.test.mjs tests\audio.test.mjs
python make_music.py
powershell -NoProfile -ExecutionPolicy Bypass -File voice.ps1
node capture.mjs --duration 30 --out $framesDir --width 1920 --height 1080 --fps 30
```

The output test is intentionally excluded from this pre-render command because the MP4 does not exist yet. Installed FFmpeg 9 does not expose `-filter_complex_script`; read the canonical graph file and pass its exact content through `-filter_complex`. Use this input order:

```powershell
$filterGraph = ((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'audio-filter.txt') -Raw) -replace '\r?\n', '').Trim()
if ([string]::IsNullOrWhiteSpace($filterGraph)) { throw 'audio-filter.txt is empty.' }
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
  '-vf', 'subtitles=captions.srt:charenc=UTF-8',
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
  (Join-Path $outDir 'amsur-promo-1080p.mp4')
)
& ffmpeg @ffmpegArgs
if ($LASTEXITCODE -ne 0) { throw "FFmpeg failed with exit code $LASTEXITCODE" }
```

Copy `captions.srt` to `out\amsur-promo-captions.srt`, copy `description.txt` to `out\amsur-promo-description.txt`, and run `ffprobe` at the end. Never delete or clean a user path; the timestamped Temp frame directory may be retained for diagnosis.

- [ ] **Step 5: cp-tester runs the output test GREEN**

```powershell
node --test tests\output.test.mjs
```

Expected: PASS with one valid 30-second MP4.

- [ ] **Step 6: Extract keyframes**

```powershell
ffmpeg -ss 1.5 -i promo\out\amsur-promo-1080p.mp4 -frames:v 1 .opencode\compose-plus\screens\promo-v1-hook.png
ffmpeg -ss 5.0 -i promo\out\amsur-promo-1080p.mp4 -frames:v 1 .opencode\compose-plus\screens\promo-v1-data.png
ffmpeg -ss 10.0 -i promo\out\amsur-promo-1080p.mp4 -frames:v 1 .opencode\compose-plus\screens\promo-v1-generate.png
ffmpeg -ss 15.5 -i promo\out\amsur-promo-1080p.mp4 -frames:v 1 .opencode\compose-plus\screens\promo-v1-quality.png
ffmpeg -ss 20.5 -i promo\out\amsur-promo-1080p.mp4 -frames:v 1 .opencode\compose-plus\screens\promo-v1-choices.png
ffmpeg -ss 25.0 -i promo\out\amsur-promo-1080p.mp4 -frames:v 1 .opencode\compose-plus\screens\promo-v1-roles.png
ffmpeg -ss 28.5 -i promo\out\amsur-promo-1080p.mp4 -frames:v 1 .opencode\compose-plus\screens\promo-v1-end.png
```

Expected: 7 non-empty 1920×1080 PNGs.

---

### Task 5: Vision QA and bounded fixes

**Files:**
- Modify through cp-implementer: `promo/index.html`, `promo/styles.css`, `promo/scene.js`, and only if timing changes require it `promo/promo.config.json`.
- Regenerate: final MP4 and 7 keyframes.

**Interfaces:**
- Consumes: v1 keyframes and spec.
- Produces: v2/v3 keyframes with no confirmed CRITICAL/HIGH visual defects.

- [ ] **Step 1: Compose+ sends absolute keyframe paths to `mimo`**

Review only: hierarchy, crop, alignment, typography, contrast, UI fidelity, safe area, footnote readability and before/after differences.

- [ ] **Step 2: Classify findings**

- confirmed CRITICAL/HIGH → fix;
- probable CRITICAL/HIGH → verify then fix;
- subjective/uncertain → do not trigger a cycle without evidence.

- [ ] **Step 3: cp-implementer applies only confirmed fixes**

Do not redesign the brand or replace UI. Preserve 8% safe area and 24/48 px text minimum.

- [ ] **Step 4: Re-render and re-test**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File render.ps1
node --test tests\assets.test.mjs tests\output.test.mjs
```

Expected: PASS; keyframes saved as `promo-v2-*` or `promo-v3-*`.

- [ ] **Step 5: Stop condition**

Maximum 2–3 rounds. Stop when no confirmed/probable CRITICAL/HIGH remains; record unresolved probable/subjective issues.

---

### Task 6: Red team, regression gate and final acceptance

**Files:**
- Create after verification: `.opencode/compose-plus/acceptance-promo.md`
- Do not modify product code.

- [ ] **Step 1: cp-critic reviews implementation against `promo-design.md`**

Check factual drift, hidden 140/139, misleading 0,1 s, fake UI, privacy, incomplete outputs, subtitle/audio drift and missing acceptance criteria.

- [ ] **Step 2: reviewer reviews source and final render**

Read-only check of HTML/CSS/JS, Playwright, PowerShell, Python, FFmpeg, security of local paths, robustness and test coverage.

- [ ] **Step 3: cp-tester runs the full promo suite**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File render.ps1
npm test
ffprobe -v error -show_streams -show_format -of json promo\out\amsur-promo-1080p.mp4
```

Expected: all tests PASS; technical metadata matches Task 4.

- [ ] **Step 4: mimo performs final keyframe regression**

Compare v1 and final. No unintended crop, missing text, broken logo, unreadable claim or screenshot distortion.

- [ ] **Step 5: Verify dirty-tree isolation**

```powershell
git diff --binary | git hash-object --stdin
git status --short -- promo .opencode\compose-plus
```

Expected tracked diff hash remains `2eb4b7ad92090a2658ec898e60d84834c279c5b6`. The scoped status may also list pre-existing untracked Compose+ artifacts from the user's earlier work; the new items must be limited to `promo/**` and the promo-specific Compose+ files/artifacts. No new tracked changes may appear under `src/**` or `данные/**`.

- [ ] **Step 6: Write Final Acceptance Report**

Record for every criterion: done, verified command/evidence, unresolved. State explicitly whether audio intelligibility and subjective pacing were only technically checked or also reviewed by the user.

- [ ] **Step 7: Hand off deliverables**

Provide absolute paths to:

```text
D:\LuMon1x\AMSUR\promo\out\amsur-promo-1080p.mp4
D:\LuMon1x\AMSUR\promo\out\amsur-promo-captions.srt
D:\LuMon1x\AMSUR\promo\out\amsur-promo-description.txt
D:\LuMon1x\AMSUR\promo\render.ps1
```

Do not commit or push.
