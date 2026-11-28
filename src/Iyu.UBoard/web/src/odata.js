// OData reads the assistant makes: how a read is written down, and the two rules it is held to before anything is sent.
//
//   1. Permission: every entity and property the read names must be one the catalog says the signed-in person can read.
//      The host enforces the same thing on its side; this is the early stop, so a request for a hidden field never leaves
//      the page and the person is told which field and what can be used instead.
//   2. Bulk: aggregate first. A read that returns rows must page (`$top` at most MAX_ROWS), so a million rows never reach
//      the browser. A table reads one page of TABLE_PAGE rows with `$count=true`.
//
// A read is held as a plain `query` object. The URL is built from it once, and that same string is what is shown to the person
// and what is sent — nothing rewrites it in between.

/** Where the host serves its OData, as the browser asks for it. The host says (`access.dataRoot`); `/$data` until it has. */
export let DATA_ROOT = '/$data';
export function setDataRoot(root) {
  DATA_ROOT = typeof root === 'string' && root.startsWith('/') ? root.replace(/\/+$/, '') : '/$data';
}
export const MAX_ROWS = 100;
export const TABLE_PAGE = 50;

const NAME = /^[A-Za-z_][A-Za-z0-9_]*$/;

// Only what would change the meaning of a query string is escaped, so the text shown reads like the query that was written.
// (A space is left as it is: the browser writes it as %20 itself.)
const encodeValue = value => String(value).replace(/%/g, '%25').replace(/&/g, '%26').replace(/#/g, '%23').replace(/\+/g, '%2B');

const expandText = ({ link, select }) => (select && select.length > 0 ? `${link}($select=${select.join(',')})` : link);

/** The query string a read is sent with — options in a fixed order. */
export function buildODataUrl(query, root = DATA_ROOT) {
  const options = [];
  if (query.apply) options.push(['$apply', query.apply]);
  if (query.filter) options.push(['$filter', query.filter]);
  if (query.select?.length) options.push(['$select', query.select.join(',')]);
  if (query.expand?.length) options.push(['$expand', query.expand.map(expandText).join(',')]);
  if (query.orderby) options.push(['$orderby', query.orderby]);
  if (query.top !== undefined) options.push(['$top', String(query.top)]);
  if (query.skip !== undefined) options.push(['$skip', String(query.skip)]);
  if (query.count) options.push(['$count', 'true']);
  const text = options.map(([name, value]) => `${name}=${encodeValue(value)}`).join('&');
  return `${root}/${query.entity}${text ? `?${text}` : ''}`;
}

/** Splits `a,b(c,d),e` at the commas outside parentheses. */
function splitTop(text) {
  const parts = [];
  let depth = 0;
  let from = 0;
  for (let i = 0; i < text.length; i++) {
    if (text[i] === '(') depth++;
    else if (text[i] === ')') depth--;
    else if (text[i] === ',' && depth === 0) {
      parts.push(text.slice(from, i));
      from = i + 1;
    }
  }
  parts.push(text.slice(from));
  return parts.map(part => part.trim()).filter(Boolean);
}

function parseExpand(text) {
  return splitTop(text).map(item => {
    const open = item.indexOf('(');
    if (open < 0) return { link: item };
    const inner = item.slice(open + 1, item.lastIndexOf(')'));
    const select = /\$select=([^;]*)/.exec(inner)?.[1];
    return { link: item.slice(0, open), ...(select ? { select: splitTop(select) } : {}) };
  });
}

/** A path as a board's data read holds it (`/$data/Orders?$top=50&$count=true`) back into a query. Undefined for a path that
 *  is not a read of the host's data. */
export function parseODataUrl(path, root = DATA_ROOT) {
  if (typeof path !== 'string' || !path.startsWith(`${root}/`)) return undefined;
  const [head, ...rest] = path.slice(root.length + 1).split('?');
  const entity = head.replace(/\{\{[^}]*\}\}/g, '');
  const options = {};
  for (const pair of rest.join('?').split('&')) {
    if (!pair) continue;
    const at = pair.indexOf('=');
    const name = at < 0 ? pair : pair.slice(0, at);
    options[name] = at < 0 ? '' : pair.slice(at + 1);
  }
  const number = name => (options[name] === undefined || /\{\{/.test(options[name]) ? undefined : Number(options[name]));
  return {
    entity,
    ...(options.$apply !== undefined && { apply: options.$apply }),
    ...(options.$filter !== undefined && { filter: options.$filter }),
    ...(options.$select !== undefined && { select: splitTop(options.$select) }),
    ...(options.$expand !== undefined && { expand: parseExpand(options.$expand) }),
    ...(options.$orderby !== undefined && { orderby: options.$orderby }),
    ...(options.$top !== undefined && { top: number('$top'), topText: options.$top }),
    ...(options.$skip !== undefined && { skip: number('$skip') }),
    ...(options.$count !== undefined && { count: options.$count === 'true' }),
  };
}

// ---------------------------------------------------------------- the bulk rule

const AGGREGATES = /\b(groupby|aggregate)\s*\(/;

/** Undefined when the read is fine, otherwise what to tell the model. The rule: aggregate on the server, or page. */
export function bulkProblem(query) {
  const aggregated = query.apply !== undefined && AGGREGATES.test(query.apply);
  if (query.top !== undefined && !(Number.isInteger(query.top) && query.top >= 1)) {
    if (typeof query.topText === 'string' && /\{\{/.test(query.topText)) return undefined; // a board variable: its options are the board's own
    return { rule: 'top', message: '$top must be a whole number from 1 to ' + MAX_ROWS + '.' };
  }
  if (query.top !== undefined && query.top > MAX_ROWS)
    return {
      rule: 'top',
      message: `$top=${query.top} asks for more than ${MAX_ROWS} rows. Aggregate on the server ($apply=groupby(...)/aggregate(...)) and read the result, or read a page: $top=${TABLE_PAGE}&$count=true (then $skip for the next page).`,
    };
  if (!aggregated && query.top === undefined)
    return {
      rule: 'aggregate-first',
      message: `A read of ${query.entity} without $apply (groupby/aggregate) or $top would return every row. Aggregate on the server first: $apply=groupby((Field),aggregate(Amount with sum as Total)). For a table read one page: $top=${TABLE_PAGE}&$count=true.`,
    };
  return undefined;
}

// ---------------------------------------------------------------- the permission rule

// Words an OData expression uses that are not names of fields. (Compared as written: functions and operators are lower case.)
const WORDS = new Set(
  `and or not eq ne gt ge lt le in has add sub mul div divby mod contains startswith endswith length indexof substring tolower
   toupper trim concat matchesPattern year month day hour minute second fractionalseconds date time totaloffsetminutes
   mindatetime maxdatetime now round floor ceiling cast isof any all null true false asc desc filter groupby aggregate compute
   top skip orderby topcount bottomcount toppercent bottompercent topsum bottomsum search expand identity rollup from with as
   sum min max average countdistinct count custom it this`.split(/\s+/),
);

function stripLiterals(text) {
  return text
    .replace(/'(?:[^']|'')*'/g, "''")
    .replace(/\{\{[^}]*\}\}/g, '')
    .replace(/[A-Za-z_][A-Za-z0-9_.]*''/g, "''") // an enum literal written with its type: Ns.Status'Paid'
    .replace(/\b\d{4}-\d{2}-\d{2}(?:T[0-9:.]+(?:Z|[+-]\d{2}:\d{2})?)?/g, '0')
    .replace(/\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b/g, '0')
    .replace(/\$[A-Za-z]+/g, '')
    .replace(/\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?[A-Za-z]?\b/g, '0');
}

/** The field paths an expression names (`Region/Name`, `Total`), leaving out literals, keywords and functions, the names the
 *  expression gives its own results (`as Revenue`) and the variables of a lambda. */
export function fieldPaths(expression) {
  const text = stripLiterals(expression);
  const given = new Set([...text.matchAll(/\bas\s+([A-Za-z_][A-Za-z0-9_]*)/g)].map(match => match[1]));
  const lambda = new Set([...text.matchAll(/\b([A-Za-z_][A-Za-z0-9_]*)\s*:/g)].map(match => match[1]));
  const found = [];
  for (const [path] of text.matchAll(/[A-Za-z_][A-Za-z0-9_]*(?:\/[A-Za-z_][A-Za-z0-9_]*)*/g)) {
    const first = path.split('/')[0];
    if (WORDS.has(first) || given.has(first) || lambda.has(first)) continue;
    if (!found.includes(path)) found.push(path);
  }
  return found;
}

// An entity the host lists as not readable is not one the person can use (the host lists it only when asked to show what is locked).
const readableNames = catalog => catalog.entities.filter(entity => entity.readable !== false).map(entity => entity.name).slice(0, 8);
const entityOf = (catalog, name) => catalog.entities.find(entity => entity.name === name && entity.readable !== false);

function readableFields(entity) {
  return entity.properties.filter(property => property.readable !== false).map(property => `${entity.name}.${property.name}`);
}

// One path from `entity`: through links, ending on a property (or a link). Undefined when the person can read it.
function unreadable(catalog, entity, path) {
  const segments = path.split('/');
  let current = entity;
  for (let i = 0; i < segments.length; i++) {
    const segment = segments[i];
    const last = i === segments.length - 1;
    const property = current.properties.find(candidate => candidate.name === segment);
    if (property) {
      if (property.readable === false) return { field: `${entity.name}.${path}`, reason: 'locked', in: current };
      return undefined; // a property ends a path; anything after it (a function of it) is the host's to judge
    }
    const link = current.links?.find(candidate => candidate.name === segment);
    if (!link) return { field: `${entity.name}.${path}`, reason: 'absent', in: current };
    const target = entityOf(catalog, link.target);
    if (!target) return { field: `${entity.name}.${segments.slice(0, i + 1).join('/')}`, reason: 'entity-absent', in: current };
    if (last) return undefined;
    current = target;
  }
  return undefined;
}

/** Undefined when everything the read names is readable. Otherwise the first thing that is not, and what could be used. */
export function permissionProblem(catalog, query) {
  const entity = entityOf(catalog, query.entity);
  if (!entity)
    return {
      field: query.entity,
      reason: 'entity-absent',
      alternatives: readableNames(catalog),
      message: `${query.entity} is not a data set you can read.`,
    };
  const paths = [
    ...(query.apply ? fieldPaths(query.apply) : []),
    ...(query.filter ? fieldPaths(query.filter) : []),
    ...(query.orderby ? fieldPaths(query.orderby) : []),
    ...(query.select ?? []),
  ];
  const aliases = new Set([...(query.apply ?? '').matchAll(/\bas\s+([A-Za-z_][A-Za-z0-9_]*)/g)].map(match => match[1]));
  for (const path of paths) {
    if (aliases.has(path)) continue;
    const problem = unreadable(catalog, entity, path);
    if (problem) return withAlternatives(problem, catalog);
  }
  for (const expand of query.expand ?? []) {
    const link = entity.links?.find(candidate => candidate.name === expand.link);
    if (!link) return withAlternatives({ field: `${entity.name}.${expand.link}`, reason: 'absent', in: entity }, catalog);
    const target = entityOf(catalog, link.target);
    if (!target) return withAlternatives({ field: `${entity.name}.${expand.link}`, reason: 'entity-absent', in: entity }, catalog);
    for (const path of expand.select ?? []) {
      const problem = unreadable(catalog, target, path);
      if (problem) return withAlternatives({ ...problem, field: `${entity.name}.${expand.link}/${path}` }, catalog);
    }
  }
  return undefined;
}

function withAlternatives(problem, catalog) {
  const where = problem.in;
  const asked = problem.field.split('/').at(-1).split('.').at(-1);
  let alternatives;
  let hint;
  if (problem.reason === 'entity-absent') {
    // The data sets are few: all of them, the ones named like the guess first.
    const names = catalog.entities.filter(entity => entity.readable !== false).map(entity => entity.name);
    const near = nearNames(asked, names);
    alternatives = [...near, ...names.filter(name => !near.includes(name))].slice(0, 8);
  } else {
    const readable = where.properties.filter(property => property.readable !== false);
    const wanted = where.properties.find(property => property.name === asked);
    if (wanted) {
      // A field that exists but is hidden: the ones of the same kind first (a number for a number).
      const same = readable.filter(property => property.type === wanted.type && property.name !== 'Id');
      const rest = readable.filter(property => !same.includes(property));
      alternatives = [...same, ...rest].map(property => `${where.name}.${property.name}`).slice(0, 8);
    } else {
      // A guessed name: only the fields whose names are near it — the first fields of a wide entity tell the model nothing.
      alternatives = nearNames(asked, readable.map(property => property.name)).map(name => `${where.name}.${name}`).slice(0, 8);
      if (alternatives.length === 0) hint = `No field of ${where.name} has a name near ${asked} — the catalog lists the fields ${where.name} has.`;
    }
  }
  const why = problem.reason === 'locked' ? 'is hidden by a read policy for your role' : problem.reason === 'absent' ? 'is not a field you can read' : 'is not a data set you can read';
  return { field: problem.field, reason: problem.reason, alternatives, message: `${problem.field} ${why}.`, ...(hint && { hint }) };
}

/**
 * The names near `asked`, nearest first: ones sharing a word of it (`EnterpriseName` → `CustomerName`), a run of at least
 * three letters covering half of it (`Status` → `ProductionState`), or a few edits away. Case is ignored.
 */
function nearNames(asked, names) {
  const words = splitWords(asked);
  const lower = asked.toLowerCase();
  const scored = [];
  for (const name of names) {
    const candidate = name.toLowerCase();
    const run = longestRun(lower, candidate);
    const byRun = run >= 3 ? run / lower.length : 0;
    const shared = splitWords(name).filter(word => words.includes(word)).length;
    const byWord = words.length ? shared / words.length : 0;
    const distance = editDistance(lower, candidate);
    const byEdit = distance <= 3 && distance / lower.length <= 0.4 ? 1 - distance / lower.length : 0;
    const score = Math.max(byRun, byWord, byEdit);
    if (score >= 0.5) scored.push({ name, score, distance });
  }
  return scored
    .sort((a, b) => b.score - a.score || a.distance - b.distance || a.name.localeCompare(b.name))
    .map(entry => entry.name);
}

const splitWords = name => name.split(/(?<=[a-z0-9])(?=[A-Z])|[^A-Za-z0-9]+/).filter(Boolean).map(word => word.toLowerCase());

function longestRun(a, b) {
  let best = 0;
  const row = new Array(b.length + 1).fill(0);
  for (let i = 1; i <= a.length; i++) {
    let previous = 0;
    for (let j = 1; j <= b.length; j++) {
      const keep = row[j];
      row[j] = a[i - 1] === b[j - 1] ? previous + 1 : 0;
      if (row[j] > best) best = row[j];
      previous = keep;
    }
  }
  return best;
}

// ---------------------------------------------------------------- what the model is told of the catalog

/** The catalog as the model reads it: what can be used, and — when the host shows them — what is locked. */
export function catalogForModel(catalog) {
  return {
    entities: catalog.entities.filter(entity => entity.readable !== false).map(entity => ({
      name: entity.name,
      fields: entity.properties.filter(property => property.readable !== false).map(property => `${property.name}:${property.type}${property.nullable ? '?' : ''}`),
      ...(entity.properties.some(property => property.readable === false) && {
        locked: entity.properties.filter(property => property.readable === false).map(property => property.name),
      }),
      links: (entity.links ?? []).map(link => `${link.name} -> ${link.target}${link.collection ? ' (many)' : ''}`),
    })),
    note: 'Use only these names. A field under "locked" is hidden from this person: never read it or put it on a board.',
  };
}

/** The candidate closest to `word` (edit distance), for «did you mean». Undefined when nothing is near. */
export function closest(word, candidates) {
  let best;
  let bestDistance = Infinity;
  const lower = word.toLowerCase();
  for (const candidate of candidates) {
    const distance = editDistance(lower, candidate.toLowerCase());
    if (distance < bestDistance) {
      best = candidate;
      bestDistance = distance;
    }
  }
  return best !== undefined && bestDistance <= 3 && bestDistance / word.length <= 0.4 ? best : undefined;
}

function editDistance(a, b) {
  const row = Array.from({ length: b.length + 1 }, (_, i) => i);
  for (let i = 1; i <= a.length; i++) {
    let previous = row[0];
    row[0] = i;
    for (let j = 1; j <= b.length; j++) {
      const keep = row[j];
      row[j] = Math.min(row[j] + 1, row[j - 1] + 1, previous + (a[i - 1] === b[j - 1] ? 0 : 1));
      previous = keep;
    }
  }
  return row[b.length];
}

export const isName = value => typeof value === 'string' && NAME.test(value);
