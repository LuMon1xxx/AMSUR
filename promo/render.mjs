// Usage: node render.mjs [absolute-or-relative-promo.html] [frames-dir]
// Requires Playwright (npm install playwright) and installed Chromium.
import { chromium } from 'playwright';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const html = path.resolve(process.argv[2] ?? 'amsur-promo.html');
const outDir = path.resolve(process.argv[3] ?? 'frames');
const fps = 30, frames = 1200;
await mkdir(outDir, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
  await page.goto(pathToFileURL(html).href + '?t=0', { waitUntil: 'load' });
  await page.evaluate(async () => {
    await Promise.all([...document.images].map(img => img.decode().catch(() => {})));
    await document.fonts.ready;
  });
  const canvas = page.locator('#frame');
  for (let i = 0; i < frames; i++) {
    // Absolute frame time: no wall-clock playback, sleeps, or accumulated delta.
    await page.evaluate(t => window.drawFrame(t), i / fps);
    await canvas.screenshot({ path: path.join(outDir, `frame-${String(i).padStart(4, '0')}.png`) });
    if (i % 120 === 0) console.log(`Rendered ${i}/${frames} (t=${(i / fps).toFixed(3)}s)`);
  }
  console.log(`Rendered ${frames} frames to ${outDir}`);
} finally {
  await browser.close();
}
