// Run with Node.js and sharp. No network access; never rewrites the supplied original.
// Raster editing: built-in imagegen made the optical small variant. A deterministic
// inset silhouette removes extraction fringe during final icon rasterization.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');
const ico = require('./Write-WindowsIco.cjs');
const renderSmallBrandFrame = require('./Render-SmallBrandFrame.cjs');
const root = path.resolve(__dirname, '..');
const assets = path.join(root, 'src/PGTray/Assets/Brand');
const evidence = path.join(root, 'artifacts/dev/brand-icons');
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

async function cutout(file, inset, radius, output) {
  const { width, height } = await sharp(file).metadata();
  // Both supplied inputs are 1254px squares. Keep the outline safely inside
  // their blue tile. Only the outer blue rim is inset, never white shell pixels.
  if (width !== 1254 || height !== 1254) throw new Error('Unexpected master geometry');
  const mask = Buffer.from(`<svg width="${width}" height="${height}"><rect x="${inset}" y="${inset}" width="${width - 2 * inset}" height="${height - 2 * inset}" rx="${radius}" fill="white"/></svg>`);
  // Reset generated alpha within the tile: white shell must remain opaque.
  const rgb = await sharp(file).removeAlpha().ensureAlpha().png().toBuffer();
  await sharp(rgb).composite([{ input: mask, blend: 'dest-in' }]).png().toFile(output);
}


(async () => {
  await fs.mkdir(evidence, { recursive: true });
  const master = path.join(assets, 'Masters');
  await cutout(path.join(master, 'PgHatch.original.png'), 8, 330, path.join(master, 'PgHatch.transparent.png'));
  await cutout(path.join(master, 'PgHatch.small.generated.png'), 40, 320, path.join(master, 'PgHatch.small.transparent.png'));
  const frameDir = path.join(assets, 'Frames'); await fs.mkdir(frameDir, { recursive: true });
  const appFrames = [], trayFrames = [];
  for (const size of sizes) {
    for (const tray of [false, true]) {
      const useSmall = tray || size <= 32;
      const source = path.join(master, useSmall ? 'PgHatch.small.transparent.png' : 'PgHatch.transparent.png');
      // 1px safe margin at small sizes; proportional margin in Explorer frames.
      const margin = Math.max(1, Math.round(size * .025));
      const file = path.join(frameDir, `${tray ? 'tray' : 'app'}-${size}.png`);
      if (size <= 32) {
        await fs.writeFile(file, await renderSmallBrandFrame(await fs.readFile(source), size));
        (tray ? trayFrames : appFrames).push({ size, file });
        continue;
      }
      const { data, info } = await sharp(source).resize(size - margin * 2, size - margin * 2, { kernel: 'lanczos3' })
        .extend({ top: margin, bottom: margin, left: margin, right: margin, background: '#00000000' })
        .ensureAlpha().raw().toBuffer({ resolveWithObject: true });
      // Lanczos can leave isolated alpha=1/2 RGB overshoots outside the
      // silhouette. They carry no useful coverage; normalize to clear black.
      for (let p = 0; p < data.length; p += 4) if (data[p + 3] <= 2) data.fill(0, p, p + 4);
      await sharp(data, { raw: { width: info.width, height: info.height, channels: 4 } }).png({ palette: false }).toFile(file);
      (tray ? trayFrames : appFrames).push({ size, file });
    }
  }
  await ico(appFrames, path.join(assets, 'PgHatch.ico'));
  await ico(trayFrames, path.join(assets, 'PgHatch.Tray.ico'));
  // Contact sheet: actual size in each column plus nearest-neighbor magnified
  // small frames, so blur and edge defects cannot hide behind the preview scale.
  const overlays = [];
  const svg = [`<svg width="1380" height="880"><style>text{font-family:Segoe UI;font-size:17px}</style><rect width="1380" height="880" fill="#edf0f4"/><text x="20" y="30">PgHatch · application / tray · native pixel sizes</text>`];
  for (let row = 0; row < 2; row++) {
    const top = 50 + row * 380;
    svg.push(`<rect x="12" y="${top}" width="1356" height="360" fill="${row ? '#20242c' : '#fff'}"/><text fill="${row ? '#fff' : '#20242c'}" x="24" y="${top + 25}">${row ? 'Tray · dark background' : 'Application · light background'}</text>`);
    for (let col = 0; col < sizes.length; col++) {
      const size = sizes[col], x = 24 + col * 132;
      svg.push(`<text fill="${row ? '#ddd' : '#333'}" x="${x}" y="${top + 52}">${size}px</text>`);
      const file = (row ? trayFrames : appFrames)[col].file;
      overlays.push({ input: await fs.readFile(file), left: x, top: top + 65 });
      if (size <= 32) overlays.push({ input: await sharp(file).resize(size * 4, size * 4, { kernel: 'nearest' }).png().toBuffer(), left: x, top: top + 115 });
    }
  }
  svg.push('<text x="24" y="850">16–32px: two database tiers, thicker shell, no tiny burst strokes. Larger application frames preserve the supplied artwork.</text></svg>');
  await sharp(Buffer.from(svg.join(''))).composite(overlays).png().toFile(path.join(evidence, 'brand-icon-preview.png'));
  // Large edge review on both contrasting backgrounds.
  const edge = [];
  for (let col = 0; col < 2; col++) for (let row = 0; row < 2; row++) {
    const file = path.join(master, row ? 'PgHatch.small.transparent.png' : 'PgHatch.transparent.png');
    edge.push({ input: await sharp(file).resize(340, 340).png().toBuffer(), left: col * 360 + 10, top: row * 360 + 10 });
  }
  await sharp(Buffer.from('<svg width="720" height="720"><rect width="360" height="720" fill="white"/><rect x="360" width="360" height="720" fill="#181c24"/></svg>'))
    .composite(edge).png().toFile(path.join(evidence, 'brand-edge-preview.png'));
  console.log('Generated 9 RGBA frames for each ICO, transparent masters and light/dark previews.');
})().catch(error => { console.error(error); process.exitCode = 1; });
