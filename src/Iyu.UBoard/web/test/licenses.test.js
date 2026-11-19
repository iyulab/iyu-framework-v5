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
