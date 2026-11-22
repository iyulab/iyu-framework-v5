// Writes THIRD-PARTY-NOTICES.txt next to the built screen: every package the screen ships, with its version, its licence and the
// licence text the package carries. Run by the build after `vite build`, from the lockfile and the installed packages (no network).
// It stops the build when a shipped package's licence is outside the allow-list, or when a package has no licence text to ship:
// a package that carries none borrows the text of a named sibling (`licenseTextFrom` in licenses.json) — never goes without.
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { licenseViolations, shippedPackages } from './licenses.mjs';

const LICENSE_FILE = /^(licen[cs]e|copying|notice|unlicense)/i;

/** The licence file of an installed package: at its top, else under `dist` (a font package keeps it there). */
export function readLicenseText(directory) {
  for (const folder of [directory, join(directory, 'dist')]) {
    if (!existsSync(folder)) continue;
    const file = readdirSync(folder).find(name => LICENSE_FILE.test(name));
    if (file) return readFileSync(join(folder, file), 'utf8').replace(/\r\n/g, '\n').trim();
  }
  return undefined;
}

/** The repository a package names for itself, for the notice to point at; empty when its manifest does not say. */
export function readRepository(directory) {
  try {
    const meta = JSON.parse(readFileSync(join(directory, 'package.json'), 'utf8'));
    return typeof meta.repository === 'string' ? meta.repository : meta.repository?.url ?? meta.homepage ?? '';
  } catch {
    return '';
  }
}

/**
 * The text of the notices file, or the problems that stop it. `installed(path)` answers `{ text, repository }` for a package's
 * folder in the lockfile (`node_modules/…`): `text` is its licence file's content or undefined.
 * @returns {{ problems: string[], text?: string, count?: number }}
 */
export function buildNotices(lock, rules, installed) {
  const problems = [];
  const violations = licenseViolations(lock, rules.allowed);
  if (violations.length > 0)
    problems.push(`The screen would ship ${violations.length} package(s) whose licence is not on the allow-list (web/licenses.json):\n  ${violations.map(pkg => `${pkg.name}@${pkg.version}: ${pkg.license ?? '(no licence)'}`).join('\n  ')}`);

  const packages = shippedPackages(lock);
  const byName = new Map(packages.map(pkg => [pkg.name, pkg]));
  const sections = [];
  const missing = [];
  for (const pkg of packages) {
    const own = installed(pkg.path);
    let { text } = own;
    let note = '';
    const donor = rules.licenseTextFrom?.[pkg.name];
    if (text === undefined && donor !== undefined) {
      const from = byName.get(donor);
      text = from ? installed(from.path).text : undefined;
      note = `The package ships no licence file; the text below is the one of ${donor}, released with it by the same project under the licence named above.\n\n`;
    }
    if (text === undefined) {
      missing.push(`${pkg.name}@${pkg.version}`);
      continue;
    }
    sections.push([`${pkg.name} ${pkg.version}`, `License: ${pkg.license}`, ...(own.repository ? [`Source: ${own.repository}`] : []), '', `${note}${text}`].join('\n'));
  }
  if (missing.length > 0)
    problems.push(`These shipped packages have no licence text to ship — add the text, or name a sibling that carries it under licenseTextFrom in web/licenses.json:\n  ${missing.join('\n  ')}`);
  if (problems.length > 0) return { problems };

  const header = [
    'Third-party notices',
    '',
    'The board screen served by Iyu.UBoard is built from the packages below (the production packages of its lockfile). Each is used under',
    'the licence named for it; the licence text is the one the package itself carries. The licences are permissive ones: the notice and',
    'the licence text are the condition of use, and this file is how they travel with the screen.',
  ].join('\n');
  const rule = `\n\n${'-'.repeat(78)}\n\n`;
  return { problems, text: `${header}${rule}${sections.join(rule)}\n`, count: sections.length };
}

// Run as `node scripts/notices.mjs [outDir]`.
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const web = resolve(dirname(fileURLToPath(import.meta.url)), '..');
  const out = resolve(web, process.argv[2] ?? '../obj/uboard-web');
  const lock = JSON.parse(readFileSync(join(web, 'package-lock.json'), 'utf8'));
  const rules = JSON.parse(readFileSync(join(web, 'licenses.json'), 'utf8'));
  const built = buildNotices(lock, rules, path => ({ text: readLicenseText(join(web, path)), repository: readRepository(join(web, path)) }));
  if (built.problems.length > 0) {
    for (const problem of built.problems) console.error(problem);
    process.exit(1);
  }
  writeFileSync(join(out, 'THIRD-PARTY-NOTICES.txt'), built.text, 'utf8');
  console.log(`THIRD-PARTY-NOTICES.txt: ${built.count} packages`);
}
