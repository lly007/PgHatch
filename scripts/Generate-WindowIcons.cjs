// Development only. Uses vendored Lucide SVGs and local sharp. No runtime parser or network.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');
const ico = require('./Write-WindowsIco.cjs');

(async () => {
  const assets = path.resolve(__dirname, '../src/PGTray/Assets/Ui');
  const output = path.join(assets, 'Window');
  const evidence = path.resolve(__dirname, '../artifacts/dev/issue3-window-icons');
  await fs.mkdir(output, { recursive: true });
  await fs.mkdir(evidence, { recursive: true });
  const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
  const overlays = [];
  const labels = [];
  for (const [row, name] of ['settings', 'database', 'file-text'].entries()) {
    const source = (await fs.readFile(path.join(assets, 'Lucide', `${name}.svg`), 'utf8'))
      .replaceAll('currentColor', '#1e3a5f');
    const frames = [];
    for (const [column, size] of sizes.entries()) {
      const margin = Math.max(1, Math.round(size / 32));
      const inner = size - 2 * margin;
      const svg = source.replace('width="24"', `width="${inner}"`).replace('height="24"', `height="${inner}"`);
      const file = path.join(evidence, `${name}-${size}.png`);
      await sharp(Buffer.from(svg)).extend({ top: margin, bottom: margin, left: margin, right: margin, background: '#00000000' })
        .png({ palette: false, compressionLevel: 9 }).toFile(file);
      frames.push({ size, file });
      const x = 120 + column * 140; const y = 50 + row * 340;
      overlays.push({ input: await fs.readFile(file), left: x, top: y });
      labels.push(`<text x="${x}" y="${y - 12}">${size}px</text>`);
      if (size <= 32) overlays.push({ input: await sharp(file).resize(size * 4, size * 4, { kernel: 'nearest' }).png().toBuffer(), left: x, top: y + 90 });
    }
    labels.push(`<text x="10" y="${70 + row * 340}">${name}</text>`);
    await ico(frames, path.join(output, `${name}.ico`));
  }
  const preview = `<svg width="1540" height="1060"><rect width="1540" height="1060" fill="#f4f6f9"/><g font-family="Segoe UI" font-size="15" fill="#1e293b">${labels.join('')}</g></svg>`;
  await sharp(Buffer.from(preview)).composite(overlays).png().toFile(path.join(evidence, 'window-icons-preview.png'));
  console.log('Generated settings/database/file-text Windows ICOs with nine RGBA frames each.');
})().catch(error => { console.error(error); process.exitCode = 1; });
