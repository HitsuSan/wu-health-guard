// Renders an SVG to PNGs at the sizes Windows uses for app icons. Needs Playwright with Chromium.
// Usage: node render.js <icon.svg> <outDir>
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

(async () => {
  const [svgPath, outDir] = process.argv.slice(2);
  const svg = fs.readFileSync(svgPath, 'utf8');
  const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
  fs.mkdirSync(outDir, { recursive: true });
  const browser = await chromium.launch();
  for (const size of sizes) {
    const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
    await page.setContent(`<html><body style="margin:0;background:transparent">${svg.replace('<svg ', `<svg width="${size}" height="${size}" `)}</body></html>`);
    await page.screenshot({ path: path.join(outDir, `${size}.png`), omitBackground: true });
    await page.close();
  }
  await browser.close();
})();
