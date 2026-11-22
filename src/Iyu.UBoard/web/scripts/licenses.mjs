// What the screen's licences are judged by. Pure functions over the npm lockfile and the allow-list in licenses.json, so the notices
// script (which stops the build) and a test (which stops a change) read the same rules.

/** The packages the screen ships: every package in the lockfile except the root and the ones only the build tools need (`dev`). */
export function shippedPackages(lock) {
  return Object.entries(lock.packages ?? {})
    .filter(([path, entry]) => path !== '' && !entry.dev)
    .map(([path, entry]) => ({
      path,
      name: path.slice(path.lastIndexOf('node_modules/') + 'node_modules/'.length),
      version: entry.version,
      license: typeof entry.license === 'string' ? entry.license : undefined,
    }))
    .sort((a, b) => a.name.localeCompare(b.name) || a.path.localeCompare(b.path));
}

/**
 * Whether an SPDX expression is covered by the allow-list: every `AND` side must be allowed and one `OR` side must be. A package with
 * no licence, or one the list does not name, is not allowed — adding a family is a decision made by editing the list.
 */
export function licenseAllowed(expression, allowed) {
  if (typeof expression !== 'string' || expression.trim() === '') return false;
  const clean = expression.replace(/[()]/g, ' ').trim();
  return clean.split(/\s+OR\s+/i).some(side => side.split(/\s+AND\s+/i).every(id => allowed.includes(id.trim())));
}

/** The shipped packages that break the rules: no licence, or one outside the allow-list. */
export function licenseViolations(lock, allowed) {
  return shippedPackages(lock).filter(pkg => !licenseAllowed(pkg.license, allowed));
}
