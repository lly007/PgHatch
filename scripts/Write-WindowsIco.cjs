// Development-only Windows ICO writer shared by brand and functional assets.
const fs = require('node:fs/promises');
const sharp = require('sharp');

// DIB-backed 32-bit frames below 256px give Win32/GDI consumers both BGRA and
// a traditional AND mask. 256px uses PNG RGBA, as supported by Windows 10/11.
async function writeWindowsIco(frames, output) {
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

module.exports = writeWindowsIco;
