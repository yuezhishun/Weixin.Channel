import { build } from 'esbuild';
import { readFile, readdir, writeFile } from 'node:fs/promises';

const bundle = await build({
  entryPoints: ['src/index.js'],
  outfile: '../wwwroot/markdown.js',
  bundle: true,
  format: 'iife',
  globalName: 'weixinMarkdown',
  legalComments: 'inline',
  minify: true,
  metafile: true
});

const directories = new Set();
const included = Object.values(bundle.metafile.outputs).flatMap(output =>
  Object.entries(output.inputs).filter(([, detail]) => detail.bytesInOutput > 0).map(([path]) => path));
for (const input of included) {
  const parts = input.split('/');
  const modules = parts.lastIndexOf('node_modules');
  if (modules < 0) continue;
  const name = parts[modules + 1];
  directories.add(parts.slice(0, modules + (name.startsWith('@') ? 3 : 2)).join('/'));
}

const notices = [];
for (const directory of [...directories].sort()) {
  const { name, version, license } = JSON.parse(await readFile(directory + '/package.json', 'utf8'));
  const files = (await readdir(directory)).filter(path => /^(licen[cs]e|copying|notice)(\..*)?$/i.test(path));
  const text = await Promise.all(files.sort().map(path => readFile(directory + '/' + path, 'utf8')));
  notices.push(name + '@' + version + ' (' + license + ')\n\n' + text.join('\n\n'));
}
await writeFile('../wwwroot/markdown.LICENSE.txt', notices.join('\n\n----------------------------------------\n\n').trimEnd() + '\n');
