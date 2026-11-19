import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { licenseAllowed, licenseViolations, shippedPackages } from '../scripts/licenses.mjs';

const ALLOWED = ['MIT', 'ISC', 'BSD-3-Clause', 'Apache-2.0'];

test('an expression is allowed when every AND side is and one OR side is', () => {
  assert.equal(licenseAllowed('MIT', ALLOWED), true);
  assert.equal(licenseAllowed('(MIT OR GPL-3.0-only)', ALLOWED), true, 'one permissive side is enough for an OR');
  assert.equal(licenseAllowed('MIT AND GPL-3.0-only', ALLOWED), false, 'every side of an AND must be allowed');
  assert.equal(licenseAllowed('(MIT AND ISC) OR AGPL-3.0-only', ALLOWED), true);
  assert.equal(licenseAllowed('GPL-3.0-only', ALLOWED), false);
});

test('a package with no licence, or an empty one, is not allowed', () => {
  assert.equal(licenseAllowed(undefined, ALLOWED), false);
  assert.equal(licenseAllowed('', ALLOWED), false);
  assert.equal(licenseAllowed('SEE LICENSE IN LICENSE.txt', ALLOWED), false);
});

test('the packages that ship are the lockfile\'s, without the root and the ones only the build tools need', () => {
  const lock = {
    packages: {
      '': { name: 'root', license: 'GPL-3.0-only' },
      'node_modules/a': { version: '1.0.0', license: 'MIT' },
      'node_modules/a/node_modules/b': { version: '2.0.0', license: 'ISC' },
      'node_modules/tool': { version: '3.0.0', license: 'GPL-3.0-only', dev: true },
    },
  };
  assert.deepEqual(shippedPackages(lock).map(pkg => `${pkg.name}@${pkg.version}`), ['a@1.0.0', 'b@2.0.0']);
  assert.deepEqual(licenseViolations(lock, ALLOWED), []);
  lock.packages['node_modules/c'] = { version: '1.0.0', license: 'GPL-3.0-only' };
  lock.packages['node_modules/d'] = { version: '1.0.0' };
  assert.deepEqual(licenseViolations(lock, ALLOWED).map(pkg => pkg.name), ['c', 'd']);
});

test('what the screen ships today is all on the allow-list', () => {
  const read = name => JSON.parse(readFileSync(fileURLToPath(new URL(`../${name}`, import.meta.url)), 'utf8'));
  const rules = read('licenses.json');
  assert.deepEqual(licenseViolations(read('package-lock.json'), rules.allowed), []);
});

// ---- the notices file: every shipped package comes with a licence text, or the build stops
import { buildNotices } from '../scripts/notices.mjs';

const lockOf = packages => ({ packages: Object.fromEntries(Object.entries(packages).map(([name, license]) => [`node_modules/${name}`, { version: '1.0.0', license }])) });
const RULES = { allowed: ['MIT', 'BSD-3-Clause'], licenseTextFrom: { shim: 'lit' } };
const texts = { 'node_modules/a': 'MIT text of a', 'node_modules/lit': 'BSD text of lit' };
const installed = path => ({ text: texts[path], repository: `https://example.test/${path}` });

test('every shipped package has a section with its licence and the text its package carries', () => {
  const built = buildNotices(lockOf({ a: 'MIT', lit: 'BSD-3-Clause' }), RULES, installed);
  assert.deepEqual(built.problems, []);
  assert.equal(built.count, 2);
  assert.match(built.text, /a 1\.0\.0\nLicense: MIT\nSource: https:\/\/example\.test\/node_modules\/a\n\nMIT text of a/);
  assert.match(built.text, /lit 1\.0\.0\nLicense: BSD-3-Clause[^]*BSD text of lit/);
});

test('a package with no licence text stops the notices, whatever its licence', () => {
  const missing = buildNotices(lockOf({ a: 'MIT', b: 'BSD-3-Clause' }), RULES, installed);
  assert.equal(missing.text, undefined);
  assert.match(missing.problems.join('\n'), /b@1\.0\.0/);
  assert.doesNotMatch(missing.problems.join('\n'), /a@1\.0\.0/);
});

test('a package that carries no text borrows the one of the sibling named for it, and says so', () => {
  const built = buildNotices(lockOf({ lit: 'BSD-3-Clause', shim: 'BSD-3-Clause' }), RULES, installed);
  assert.deepEqual(built.problems, []);
  assert.match(built.text, /shim 1\.0\.0\nLicense: BSD-3-Clause[^]*the text below is the one of lit[^]*BSD text of lit/);
  const absent = buildNotices(lockOf({ shim: 'BSD-3-Clause' }), RULES, installed);
  assert.match(absent.problems.join('\n'), /shim@1\.0\.0/, 'a named sibling that is not shipped gives no text');
});

test('a licence off the list stops the notices before any text is looked at', () => {
  const built = buildNotices(lockOf({ a: 'MIT', g: 'GPL-3.0-only' }), RULES, installed);
  assert.match(built.problems[0], /g@1\.0\.0: GPL-3\.0-only/);
});
