// Development only. Normal .NET builds use the checked-in PNGs and need no Node or network.
// Requires sharp on Node's module search path. Sources and complete licenses are vendored.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');

async function main() {
  const root = path.resolve(__dirname, '../src/PGTray/Assets/Ui');
  const output = path.join(root, 'Png');
  await fs.mkdir(output, { recursive: true });
  for (const file of (await fs.readdir(path.join(root, 'Lucide'))).filter(f => f.endsWith('.svg'))) {
    const source = await fs.readFile(path.join(root, 'Lucide', file), 'utf8');
    for (const [palette, stroke] of Object.entries({ ink: '#1e293b', light: '#ffffff' })) {
      for (const size of [16, 20, 24, 32, 48, 64]) {
        const svg = source.replaceAll('currentColor', stroke)
          .replace('width="24"', `width="${size}"`).replace('height="24"', `height="${size}"`);
        await sharp(Buffer.from(svg)).png({ compressionLevel: 9 })
          .toFile(path.join(output, `${path.basename(file, '.svg')}-${palette}-${size}.png`));
      }
    }
  }
  process.stdout.write('Generated Lucide UI PNGs at 16/20/24/32/48/64 pixels, ink/light.\n');
}
main().catch(error => { process.stderr.write(`${error}\n`); process.exitCode = 1; });
