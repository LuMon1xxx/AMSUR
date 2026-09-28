/* AMSUR promo deterministic capture.
 *
 * ESM module. Exports async captureFrames(options) and provides a CLI that
 * runs only when the file is executed directly.
 *
 * options: { indexPath, configPath, outDir, duration, fps, width, height, edgePath }
 * returns: { count, outDir }
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { chromium } from 'playwright';

const SYSTEM_EDGE_X86 = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const SYSTEM_EDGE_X64 = 'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe';

function resolveBrowser(edgePath) {
  const candidates = [edgePath, SYSTEM_EDGE_X86, SYSTEM_EDGE_X64].filter(Boolean);
  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) return candidate;
  }
  try {
    const bundled = chromium.executablePath();
    if (bundled && fs.existsSync(bundled)) return bundled;
  } catch {
    // bundled Chromium path unavailable — handled below with an explicit error
  }
  return undefined;
}

export async function captureFrames(options) {
  const { indexPath, configPath, outDir, duration, fps, width, height, edgePath } = options || {};
  if (!indexPath) throw new Error('captureFrames: indexPath is required');
  if (!configPath) throw new Error('captureFrames: configPath is required');
  if (!outDir) throw new Error('captureFrames: outDir is required');

  const totalDuration = Number(duration);
  const rate = Number(fps);
  if (!isFinite(totalDuration) || totalDuration <= 0) throw new Error('captureFrames: duration must be > 0');
  if (!isFinite(rate) || rate <= 0) throw new Error('captureFrames: fps must be > 0');
  const w = Number(width) || 1920;
  const h = Number(height) || 1080;

  const configRaw = await fs.promises.readFile(configPath, 'utf8');
  const config = JSON.parse(configRaw);

  // Create (never clean) the output directory; the caller owns cleanup.
  await fs.promises.mkdir(outDir, { recursive: true });

  const executablePath = resolveBrowser(edgePath);
  if (!executablePath) {
    throw new Error(
      'captureFrames: no usable browser found — system Edge is missing and Playwright bundled Chromium is not installed. ' +
        'Install Edge or run `npx playwright install chromium`, or pass a valid edgePath.'
    );
  }
  process.stdout.write(`using browser: ${executablePath}\n`);
  const browser = await chromium.launch({ executablePath });
  try {
    const page = await browser.newPage({ viewport: { width: w, height: h } });
    await page.setViewportSize({ width: w, height: h });
    await page.goto(pathToFileURL(indexPath).href, { waitUntil: 'load' });
    // Determinism: kill CSS transitions/animations so every screenshot lands
    // on the final renderFrame style, not mid-transition.
    await page.addStyleTag({
      content: '*,*::before,*::after{transition:none!important;animation:none!important;caret-color:transparent!important}'
    });
    await page.evaluate(async () => {
      await document.fonts.ready;
    });

    const count = Math.max(1, Math.round(totalDuration * rate));
    for (let frame = 0; frame < count; frame++) {
      const t = frame / rate;
      await page.evaluate(({ cfg, tt }) => window.renderFrame(tt, cfg), { cfg: config, tt: t });
      // Let two animation frames settle so opacity/transform apply before shot.
      await page.evaluate(
        () => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)))
      );
      await page.screenshot({ path: path.join(outDir, `frame_${String(frame).padStart(4, '0')}.png`) });
    }
    return { count, outDir };
  } finally {
    await browser.close();
  }
}

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i++) {
    const token = argv[i];
    if (!token.startsWith('--')) continue;
    const eq = token.indexOf('=');
    if (eq !== -1) {
      args[token.slice(2, eq)] = token.slice(eq + 1);
    } else {
      const key = token.slice(2);
      const next = argv[i + 1];
      if (next !== undefined && !next.startsWith('--')) {
        args[key] = next;
        i++;
      } else {
        args[key] = true;
      }
    }
  }
  return args;
}

async function runCli() {
  const here = path.dirname(fileURLToPath(import.meta.url));
  const args = parseArgs(process.argv.slice(2));
  const result = await captureFrames({
    indexPath: args.index || path.join(here, 'index.html'),
    configPath: args.config || path.join(here, 'promo.config.json'),
    outDir: args.out || path.join(here, 'out', 'frames'),
    duration: args.duration !== undefined ? Number(args.duration) : 30,
    fps: args.fps !== undefined ? Number(args.fps) : 30,
    width: args.width !== undefined ? Number(args.width) : 1920,
    height: args.height !== undefined ? Number(args.height) : 1080,
    edgePath: args.edge || SYSTEM_EDGE_X86
  });
  process.stdout.write(`captured ${result.count} frames to ${result.outDir}\n`);
}

const invokedAsScript =
  process.argv.length > 1 &&
  (() => {
    try {
      return import.meta.url === pathToFileURL(process.argv[1]).href;
    } catch {
      return false;
    }
  })();

if (invokedAsScript) {
  runCli().catch((err) => {
    process.stderr.write(String((err && err.stack) || err) + '\n');
    process.exitCode = 1;
  });
}
