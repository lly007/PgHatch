// Run with Node.js and sharp. No network access; never rewrites the supplied original.
// Raster editing: built-in imagegen made the optical small variant. A deterministic
// inset silhouette removes extraction fringe during final icon rasterization.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');
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

// DIB-backed 32-bit frames below 256px give Win32/GDI consumers both BGRA and
// a traditional AND mask. 256px uses PNG RGBA, as supported by Windows 10/11.
async function ico(frames, output) {
  const header = Buffer.alloc(6 + 16 * frames.length);
  header.writeUInt16LE(1, 2); header.writeUInt16LE(frames.length, 4);
  const payloads = []; let offset = header.length;
  for (let i = 0; i < frames.length; i++) {
    const { size, file } = frames[i]; let payload;
    if (size === 256) payload = await fs.readFile(file);
    else {
      const rgba = await sharp(file).ensureAlpha().raw().toBuffer();
      const maskStride = Math.ceil(size / 32) * 4;
      payload = Buffer.alloc(40 + size * size * 4 + maskStride * size);
      payload.writeUInt32LE(40, 0); payload.writeInt32LE(size, 4);
      payload.writeInt32LE(size * 2, 8); payload.writeUInt16LE(1, 12);
      payload.writeUInt16LE(32, 14); payload.writeUInt32LE(size * size * 4, 20);
      for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
        const p = (y * size + x) * 4, q = 40 + ((size - 1 - y) * size + x) * 4;
        payload[q] = rgba[p + 2]; payload[q + 1] = rgba[p + 1];
        payload[q + 2] = rgba[p]; payload[q + 3] = rgba[p + 3];
        if (rgba[p + 3] === 0) payload[40 + size * size * 4 + (size - 1 - y) * maskStride + (x >> 3)] |= 0x80 >> (x & 7);
      }
    }
    const entry = 6 + i * 16;
    header[entry] = size === 256 ? 0 : size; header[entry + 1] = header[entry];
    header.writeUInt16LE(1, entry + 4); header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(payload.length, entry + 8); header.writeUInt32LE(offset, entry + 12);
    offset += payload.length; payloads.push(payload);
  }
  await fs.writeFile(output, Buffer.concat([header, ...payloads]));
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
