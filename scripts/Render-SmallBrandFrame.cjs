// Confirmed size-only adjustment. Development dependency: sharp; no runtime dependency.
const sharp = require('sharp');

module.exports = async function renderSmallBrandFrame(source, size) {
  if (![16, 20, 24, 32].includes(size)) throw new Error('Only confirmed small sizes are supported');
  const { data: master, info } = await sharp(source).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  if (info.width !== 1254 || info.height !== 1254) throw new Error('Unexpected approved master dimensions');
  const scale = size === 16 ? 1.09 : 1.10;
  const viewport = Math.round((info.width * size / ((size - 2) * scale)) / 2) * 2;
  const delta = (info.width - viewport) / 2;
  let image = sharp(source);
  if (delta > 0) {
    for (let y = 0; y < info.height; y++) for (let x = 0; x < info.width; x++)
      if ((x < delta || y < delta || x >= info.width - delta || y >= info.height - delta) && master[(y * info.width + x) * 4 + 3])
        throw new Error('Viewport would crop nontransparent artwork');
    image = image.extract({ left: delta, top: delta, width: viewport, height: viewport });
  } else if (delta < 0) {
    image = image.extend({ left: -delta, right: -delta, top: -delta, bottom: -delta, background: '#00000000' });
  }
  // Materialize the high-resolution viewport before resize (sharp applies
  // extend after resize inside a shared pipeline). No low-resolution upscale.
  const viewportBytes = await image.png().toBuffer();
  const { data } = await sharp(viewportBytes).resize(size, size, { kernel: 'lanczos3' }).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  for (let p = 0; p < data.length; p += 4) if (data[p + 3] <= 2) data.fill(0, p, p + 4);
  return sharp(data, { raw: { width: size, height: size, channels: 4 } }).png().toBuffer();
};
