import { dirname, join } from 'node:path';
import { createRequire } from 'node:module';
import { existsSync } from 'node:fs';

const require = createRequire(import.meta.url);
const electronDirectory = join(dirname(require.resolve('electron/package.json')), 'dist');

export default {
  appId: 'com.matthewdailytools.seareader',
  productName: 'SeaReader',
  directories: { output: 'release' },
  electronDist: existsSync(join(electronDirectory, 'electron.exe')) ? electronDirectory : undefined,
  asar: true,
  asarUnpack: [
    'node_modules/onnxruntime-node/bin/**',
    'node_modules/@img/**',
    'node_modules/@napi-rs/**'
  ],
  files: ['dist/**', 'desktop/**', 'package.json'],
  extraResources: [{ from: 'voice-assets', to: 'japanese-voice' }],
  win: {
    target: [{ target: 'nsis', arch: ['x64'] }]
  },
  nsis: {
    include: 'installer.nsh',
    oneClick: false,
    perMachine: false,
    allowToChangeInstallationDirectory: true
  }
};
