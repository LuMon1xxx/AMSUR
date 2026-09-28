import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';
import config from '../promo.config.json' with { type: 'json' };
import { captureFrames } from '../capture.mjs';
import { chromium } from 'playwright';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const promoDir = path.resolve(__dirname, '..');
const indexPath = path.join(promoDir, 'index.html');
const configPath = path.join(promoDir, 'promo.config.json');
const edgePath = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

test('every non-null scene asset resolves relative to promo/ and is non-empty', () => {
  const withAssets = config.scenes.filter((scene) => scene.asset !== null && scene.asset !== undefined);
  assert.ok(withAssets.length > 0, 'expected at least one scene with a non-null asset');
  for (const scene of withAssets) {
    const abs = path.resolve(promoDir, scene.asset);
    const stat = fs.statSync(abs);
    assert.ok(stat.isFile(), `asset for scene '${scene.id}' is not a file: ${abs}`);
    assert.ok(stat.size > 0, `asset for scene '${scene.id}' is empty: ${abs}`);
  }
});

test('captureFrames({indexPath,configPath,outDir,duration:1,fps:1}) returns count 1 and a valid PNG', async () => {
  const outDir = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'amsur-promo-test-'));
  try {
    const result = await captureFrames({ indexPath, configPath, outDir, duration: 1, fps: 1, width: 1920, height: 1080, edgePath });
    assert.equal(result.count, 1);
    assert.equal(result.outDir, outDir);
    const framePath = path.join(outDir, 'frame_0000.png');
    const stat = await fs.promises.stat(framePath);
    assert.ok(stat.size > 0, `expected non-empty PNG at ${framePath}`);
    const handle = await fs.promises.open(framePath, 'r');
    try {
      const head = Buffer.alloc(8);
      await handle.read(head, 0, 8, 0);
      assert.deepEqual(head, PNG_SIGNATURE, `expected PNG signature at ${framePath}`);
      // MEDIUM-1: IHDR width/height are big-endian uint32 at bytes 16..23.
      const ihdr = Buffer.alloc(8);
      await handle.read(ihdr, 0, 8, 16);
      assert.equal(ihdr.readUInt32BE(0), 1920, `expected frame width 1920 at ${framePath}`);
      assert.equal(ihdr.readUInt32BE(4), 1080, `expected frame height 1080 at ${framePath}`);
    } finally {
      await handle.close();
    }
  } finally {
    await fs.promises.rm(outDir, { recursive: true, force: true });
  }
});

async function openAtTime(t) {
  const browser = await chromium.launch({ executablePath: fs.existsSync(edgePath) ? edgePath : undefined });
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
  await page.goto(pathToFileURL(indexPath).href, { waitUntil: 'load' });
  const shown = await page.evaluate(({ cfg, tt }) => window.renderFrame(tt, cfg), { cfg: config, tt: t });
  return { browser, page, shown };
}

async function rectsOf(page, ids) {
  return page.evaluate((list) => {
    const out = {};
    for (const id of list) {
      const el = document.getElementById(id);
      if (!el) { out[id] = null; continue; }
      const b = el.getBoundingClientRect();
      out[id] = { top: b.top, bottom: b.bottom, left: b.left, right: b.right, width: b.width, height: b.height };
    }
    return out;
  }, ids);
}

test('layout: generate scene keeps footnote above the UI card', async () => {
  const { browser, page, shown } = await openAtTime(10);
  try {
    assert.equal(shown.sceneId, 'generate');
    const rects = await rectsOf(page, ['footnote', 'asset-card']);
    assert.ok(rects.footnote, 'expected #footnote element');
    assert.ok(rects['asset-card'], 'expected #asset-card element');
    const footnote = rects.footnote;
    const card = rects['asset-card'];
    assert.ok(footnote.height > 0, 'expected visible #footnote (non-zero height, otherwise the check is vacuous)');
    assert.ok(card.height > 0, 'expected visible #asset-card (non-zero height, otherwise the check is vacuous)');
    assert.ok(
      footnote.bottom <= card.top + 2,
      `expected footnote before UI card, not in bottom subtitle zone: footnote.bottom=${footnote.bottom} asset-card.top=${card.top}`
    );
  } finally {
    await browser.close();
  }
});

test('layout: end scene frames the logo compactly inside its card', async () => {
  const { browser, page, shown } = await openAtTime(28.5);
  try {
    assert.equal(shown.sceneId, 'end');
    const rects = await rectsOf(page, ['asset-card', 'asset-card-img']);
    const card = rects['asset-card'];
    const img = rects['asset-card-img'];
    assert.ok(card, 'expected #asset-card element');
    assert.ok(img, 'expected #asset-card-img element');
    assert.ok(img.width > 0 && img.height > 0, 'expected rendered logo image (non-zero size, otherwise the check is vacuous)');
    assert.ok(img.width <= 260, `expected compact logo width <=260, got: ${img.width}`);
    assert.ok(img.height <= 240, `expected compact logo height <=240, got: ${img.height}`);
    assert.ok(
      img.top >= card.top - 2 && img.left >= card.left - 2 &&
      img.bottom <= card.bottom + 2 && img.right <= card.right + 2,
      `expected logo fully inside its card ±2px: img=${JSON.stringify(img)} card=${JSON.stringify(card)}`
    );
  } finally {
    await browser.close();
  }
});

test('layout: progress bar stays clear of the headline subtitle zone', async () => {
  const { browser, page, shown } = await openAtTime(10);
  try {
    assert.equal(shown.sceneId, 'generate');
    const rects = await rectsOf(page, ['progress', 'headline']);
    assert.ok(rects.progress, 'expected #progress element');
    assert.ok(rects.headline, 'expected #headline element');
    assert.ok(rects.progress.height > 0, 'expected visible #progress (non-zero height, otherwise the check is vacuous)');
    assert.ok(rects.headline.height > 0, 'expected visible #headline (non-zero height, otherwise the check is vacuous)');
    assert.ok(
      rects.progress.bottom <= rects.headline.top - 20,
      `expected subtitle-safe progress, not at bottom: progress.bottom=${rects.progress.bottom} headline.top=${rects.headline.top}`
    );
  } finally {
    await browser.close();
  }
});

test('window.renderFrame first frame is visible and t=10 selects generate scene', async () => {
  const browser = await chromium.launch({ executablePath: fs.existsSync(edgePath) ? edgePath : undefined });
  try {
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
    await page.goto(pathToFileURL(indexPath).href, { waitUntil: 'load' });
    const headlineState = () => {
      const el = document.getElementById('headline');
      if (!el) return { opacity: null, text: null };
      return { opacity: getComputedStyle(el).opacity, text: el.textContent };
    };
    const before = await page.evaluate(headlineState);
    assert.equal(before.opacity, '1', `expected #headline opacity '1' before renderFrame(0), got: ${before.opacity}`);
    const first = await page.evaluate(({ cfg }) => window.renderFrame(0, cfg), { cfg: config });
    assert.equal(first.sceneId, 'hook');
    const after = await page.evaluate(headlineState);
    assert.equal(after.opacity, '1', `expected #headline opacity '1' after renderFrame(0), got: ${after.opacity}`);
    assert.ok(after.text !== null && after.text.trim().length > 0, 'expected non-empty headline on first frame');
    const result = await page.evaluate(({ cfg, t }) => window.renderFrame(t, cfg), { cfg: config, t: 10 });
    assert.equal(result.sceneId, 'generate');
    assert.ok(typeof result.progress === 'number', `expected numeric progress, got: ${String(result.progress)}`);
    assert.ok(result.progress >= 0 && result.progress <= 1, `expected progress in [0,1], got: ${result.progress}`);
  } finally {
    await browser.close();
  }
});
