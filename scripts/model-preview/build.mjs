import { build } from 'esbuild';
import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const output = new URL('../../src/WinTools/Assets/ModelPreview/', import.meta.url);
await mkdir(output, { recursive: true });
await build({ entryPoints: [fileURLToPath(new URL('viewer.js', import.meta.url))], bundle: true,
  minify: true, format: 'esm', target: 'es2022', legalComments: 'eof',
  outfile: fileURLToPath(new URL('viewer.js', output)) });
await copyFile(new URL('index.html', import.meta.url), new URL('index.html', output));
const threeLicense = await readFile(new URL('node_modules/three/LICENSE', import.meta.url), 'utf8');
const fflateSource = await readFile(new URL('node_modules/three/examples/jsm/libs/fflate.module.js', import.meta.url), 'utf8');
if (!fflateSource.includes('Licensed under MIT') || !fflateSource.includes('version 0.8.3')) throw new Error('fflate license header changed');
const fflateLicense = await readFile(new URL('fflate-LICENSE.txt', import.meta.url), 'utf8');
await writeFile(new URL('LICENSE.txt', output), 'three.js r187\n' + threeLicense + '\nfflate v0.8.3 (vendored by three.js)\n' + fflateLicense);
