// Writes THIRD-PARTY-NOTICES.txt next to the built screen: every package the screen ships, with its version, its licence and the
// licence text the package carries. Run by the build after `vite build`, from the lockfile and the installed packages (no network).
// It stops the build when a shipped package's licence is outside the allow-list, or when a package that should carry its licence
// text does not — the packages named under `withoutLicenseFile` are the ones known to ship none.
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { licenseViolations, shippedPackages } from './licenses.mjs';

const web = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const out = resolve(web, process.argv[2] ?? '../obj/uboard-web');
const lock = JSON.parse(readFileSync(join(web, 'package-lock.json'), 'utf8'));
const rules = JSON.parse(readFileSync(join(web, 'licenses.json'), 'utf8'));

const violations = licenseViolations(lock, rules.allowed);
if (violations.length > 0) {
  console.error(`The screen would ship ${violations.length} package(s) whose licence is not on the allow-list (web/licenses.json):`);
  for (const pkg of violations) console.error(`  ${pkg.name}@${pkg.version}: ${pkg.license ?? '(no licence)'}`);
  process.exit(1);
}

const LICENSE_FILE = /^(licen[cs]e|copying|notice|unlicense)/i;

/** The licence file of an installed package: at its top, else under `dist` (a font package keeps it there). */
function licenseText(directory) {
  for (const folder of [directory, join(directory, 'dist')]) {
    if (!existsSync(folder)) continue;
    const file = readdirSync(folder).find(name => LICENSE_FILE.test(name));
    if (file) return readFileSync(join(folder, file), 'utf8').replace(/\r\n/g, '\n').trim();
  }
  return undefined;
}

const sections = [];
const missing = [];
for (const pkg of shippedPackages(lock)) {
  const directory = join(web, pkg.path);
  let repository = '';
  try {
    const meta = JSON.parse(readFileSync(join(directory, 'package.json'), 'utf8'));
    repository = typeof meta.repository === 'string' ? meta.repository : meta.repository?.url ?? meta.homepage ?? '';
  } catch {
    // an installed package without a readable manifest has no repository to name
  }
  const text = licenseText(directory);
  if (text === undefined && !rules.withoutLicenseFile.includes(pkg.name)) missing.push(`${pkg.name}@${pkg.version}`);
  sections.push(
    [`${pkg.name} ${pkg.version}`, `License: ${pkg.license}`, ...(repository ? [`Source: ${repository}`] : []), '', text ?? '(The package ships no licence file; its licence is as named above.)'].join('\n'),
  );
}
if (missing.length > 0) {
  console.error(`These shipped packages carry no licence text, and web/licenses.json does not list them under withoutLicenseFile:\n  ${missing.join('\n  ')}`);
  process.exit(1);
}

const header = [
  'Third-party notices',
  '',
  'The board screen served by Iyu.UBoard is built from the packages below. Each is used under the licence named for it; the licence text',
  'is the one the package itself carries. The licences are permissive ones: the notice and the licence text are the condition of use,',
  'and this file is how they travel with the screen.',
].join('\n');
const rule = `\n\n${'-'.repeat(78)}\n\n`;
writeFileSync(join(out, 'THIRD-PARTY-NOTICES.txt'), `${header}${rule}${sections.join(rule)}\n`, 'utf8');
console.log(`THIRD-PARTY-NOTICES.txt: ${sections.length} packages`);
