// What changed between two revisions of a board, in the words the screen uses: a part is `new` or `edited` (or gone).
// The marks come from the two saved documents themselves — nothing the assistant says about its own edit is trusted.

export function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null) return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  const keys = Object.keys(a);
  if (keys.length !== Object.keys(b).length) return false;
  return keys.every(key => Object.hasOwn(b, key) && deepEqual(a[key], b[key]));
}

const dataName = reference => (typeof reference === 'string' ? /^([^[\]]+)/.exec(reference)?.[1] : undefined);

/** The reads a part draws from: its `of`, its `each`, and the reads of the parts inside a group. */
function readsOf(part) {
  return [dataName(part.of), dataName(part.each)].filter(Boolean);
}

/** Where a part is placed: its entry in a grid (with the span) or on a canvas. Other parts do not matter to this one. */
function placementOf(layout, id) {
  if (!layout) return undefined;
  if (layout.kind === 'canvas') return layout.place?.[id];
  const found = [];
  const walk = items => {
    for (const item of items ?? []) {
      if (item === id) found.push(item);
      else if (Array.isArray(item) && item[0] === id) found.push(item);
      else if (item && typeof item === 'object' && !Array.isArray(item)) {
        if (Array.isArray(item.grid)) walk(item.grid);
      }
    }
  };
  walk(layout.items);
  return found;
}

/** The variables a piece of the board mentions: `{{$name}}` in a text, `{ "variable": "name" }` in a `where`, `[$name]` in an `of`. */
function variablesIn(value) {
  const text = JSON.stringify(value ?? null);
  const names = new Set();
  for (const match of text.matchAll(/\$([A-Za-z_][A-Za-z0-9_]*)/g)) names.add(match[1]);
  for (const match of text.matchAll(/"variable":"([^"]+)"/g)) names.add(match[1]);
  return [...names];
}

function partChanged(before, after, id) {
  const was = before.parts[id];
  const now = after.parts[id];
  if (!deepEqual(was, now)) return true;
  if (!deepEqual(placementOf(before.layout, id), placementOf(after.layout, id))) return true;
  for (const name of readsOf(now)) {
    if (!deepEqual(before.data?.[name], after.data?.[name])) return true;
    for (const variable of variablesIn(after.data?.[name]))
      if (!deepEqual(before.variables?.[variable], after.variables?.[variable])) return true;
  }
  for (const variable of variablesIn(now))
    if (!deepEqual(before.variables?.[variable], after.variables?.[variable])) return true;
  if (typeof now.use === 'string' && !deepEqual(before.presets?.[now.use], after.presets?.[now.use])) return true;
  return false;
}

/** Which parts of `after` are new or edited compared with `before`: `{ [partId]: 'new' | 'edited' }`. A part counts as edited
 *  when the part itself, its place in the layout, the data read it draws from (or a variable that read uses) differs. */
export function changedParts(before, after) {
  const marks = {};
  for (const id of Object.keys(after?.parts ?? {})) {
    if (!before?.parts || !Object.hasOwn(before.parts, id)) marks[id] = 'new';
    else if (partChanged(before, after, id)) marks[id] = 'edited';
  }
  return marks;
}

/** The ids of parts `before` had that `after` no longer has. */
export function removedParts(before, after) {
  return Object.keys(before?.parts ?? {}).filter(id => !Object.hasOwn(after?.parts ?? {}, id));
}

const unescape = key => key.replace(/~1/g, '/').replace(/~0/g, '~');

/** The parts a JSON Patch touches (`/parts/<id>…`, and layout entries that name a part), for drawing only those as loading
 *  while the edit is made. Operations that touch no part by name (`/data/…`, `/variables/…`) are not part ids and are left out. */
export function partsTouched(patch, document) {
  const ids = new Set();
  for (const operation of patch ?? []) {
    const named = /^\/parts\/([^/]+)/.exec(operation.path ?? '');
    if (named) ids.add(unescape(named[1]));
    if (/^\/layout\/items\/(-|\d+)$/.test(operation.path ?? '') && typeof operation.value === 'string') ids.add(operation.value);
    if (/^\/layout\/items\/(-|\d+)$/.test(operation.path ?? '') && Array.isArray(operation.value) && typeof operation.value[0] === 'string') ids.add(operation.value[0]);
  }
  // A change to a read (or a variable) loads every part that draws from it.
  for (const operation of patch ?? []) {
    const read = /^\/data\/([^/]+)/.exec(operation.path ?? '')?.[1];
    if (read === undefined || !document?.parts) continue;
    for (const [id, part] of Object.entries(document.parts)) if (readsOf(part).includes(unescape(read))) ids.add(id);
  }
  return [...ids];
}
