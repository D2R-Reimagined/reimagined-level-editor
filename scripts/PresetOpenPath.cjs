'use strict';

// Current-build dump: preset loading cut a 64-byte DS1 open path to 59 bytes.
// Count the native prefix as well as the File1 value; do not silently rename assets.
function validatePresetOpenPath(fullPath) {
  const normalized = fullPath.replaceAll('\\', '/');
  if (!normalized.startsWith('data/global/tiles/') || /[^\x20-\x7e]/.test(normalized) ||
      Buffer.byteLength(normalized, 'ascii') > 59) {
    throw new Error(`Preset map path must be ASCII and at most 59 bytes including data/global/tiles/: ${normalized}. Shorten the map folder or filename before export.`);
  }
}

module.exports = {validatePresetOpenPath};
