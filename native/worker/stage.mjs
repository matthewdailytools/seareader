import { access, cp, mkdir, readFile } from 'node:fs/promises';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const workspace = fileURLToPath(new URL('../../', import.meta.url));
const destination = resolve(process.argv[2]);
const { packages } = JSON.parse(await readFile(join(workspace, 'package-lock.json'), 'utf8'));
const seen = new Set();

function compatible(entry) {
  return (!entry.os || entry.os.includes('win32')) && (!entry.cpu || entry.cpu.includes('x64'));
}

function locate(name, parent = '') {
  while (true) {
    const candidate = parent ? parent + '/node_modules/' + name : 'node_modules/' + name;
    if (packages[candidate]) return candidate;
    if (!parent) return null;
    const index = parent.lastIndexOf('/node_modules/');
    parent = index < 0 ? '' : parent.slice(0, index);
  }
}

async function stage(name, parent, optional = false) {
  const location = locate(name, parent);
  if (!location || !compatible(packages[location])) {
    if (optional) return;
    throw new Error('Missing required package: ' + name);
  }
  if (seen.has(location)) return;
  seen.add(location);
  const source = join(workspace, location);
  await access(join(source, 'package.json'));
  const target = join(destination, location);
  await mkdir(dirname(target), { recursive: true });
  await cp(source, target, {
    recursive: true,
    filter(file) {
      const parts = relative(source, file).split(sep);
      if (parts[0] === 'node_modules' || file.endsWith('.map')) return false;
      if (name === 'onnxruntime-node' && parts[0] === 'bin') {
        return !parts[2] || parts[2] === 'win32' && (!parts[3] || parts[3] === 'x64');
      }
      return true;
    }
  });
  const entry = packages[location];
  for (const dependency of Object.keys(entry.dependencies ?? {})) {
    // The Transformers Node entry point only loads the native ONNX backend.
    if (name === '@huggingface/transformers' && dependency === 'onnxruntime-web') continue;
    if (!Object.hasOwn(entry.optionalDependencies ?? {}, dependency)) await stage(dependency, location);
  }
  for (const dependency of Object.keys(entry.optionalDependencies ?? {})) await stage(dependency, location, true);
}

await stage('@huggingface/transformers');
await cp(new URL('./licenses/', import.meta.url), join(destination, 'licenses'), { recursive: true });
await mkdir(join(destination, 'native', 'worker'), { recursive: true });
await mkdir(join(destination, 'desktop'), { recursive: true });
await cp(join(workspace, 'native', 'worker', 'translate.mjs'), join(destination, 'native', 'worker', 'translate.mjs'));
for (const name of ['translation.mjs', 'translation-models.mjs', 'translation-worker.mjs']) {
  await cp(join(workspace, 'desktop', name), join(destination, 'desktop', name));
}
