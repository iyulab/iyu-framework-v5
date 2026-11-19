// What a saved edit actually drew. The model writes a read and a part and cannot see the result: a pointer that names no field, an axis whose field
// the rows do not have, a total that comes out 0 — each is a board that saves cleanly and draws wrongly. This module reads each changed part the way
// the viewer does (the part's own tile board, the same data adapter, the same resolution of its bindings into the props of the widget it draws) and
// says, per part, what came out: its value, its points, its rows — or what is wrong with them. The loop hands that to the model with the result of
// its edit, so it can correct itself within the same turn.
//
// It is a plain module (the page's fetch is handed in), and it says nothing about any domain: it looks at fields, numbers and counts.
import { drawBoard, readerOf } from '@iyulab/u-board/domain';
import { placedIn, resolveDocument } from '@iyulab/u-board/picture';
import { makeODataAdapter } from './odata-adapter.js';
import { readsOfPart, tileBoard } from './tile-board.js';
import { variableDefaults } from './variables.js';

/** The most parts one answer lists, and the most characters any one line of it may have: the answer costs the model tokens on every save. */
export const MAX_LISTED = 12;
const MAX_LINE = 180;
/** The most corrections one turn is asked for; after that the model is told to report what it sees, not to edit again. */
export const MAX_RENDER_FIXES = 2;
const SAMPLE_ROWS = 200;

const line = text => (text.length > MAX_LINE ? `${text.slice(0, MAX_LINE - 1)}…` : text);
const isNumber = value => (typeof value === 'number' && Number.isFinite(value)) || (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value)));
const asNumber = value => Number(value);
const short = value => {
  if (typeof value === 'number') return String(Number(value.toPrecision(6)));
  if (typeof value === 'string') return value.length > 24 ? `${value.slice(0, 23)}…` : value;
  return JSON.stringify(value);
};
const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
const fieldNames = rows => [...new Set(rows.slice(0, 20).flatMap(row => (row && typeof row === 'object' ? Object.keys(row) : [])))].filter(name => !name.startsWith('@')); // an annotation of the answer is not a field
const fieldsOf = value => (Array.isArray(value) ? value : [value]).filter(item => typeof item === 'string');

/** One read answered once per inspection, however many parts draw from it: the answers are kept as the page's fetch gave them. */
function cachedFetch(fetcher) {
  const answers = new Map();
  return url => {
    if (!answers.has(url)) {
      answers.set(url, (async () => {
        const response = await fetcher(url);
        if (!response.ok) return { ok: false, status: response.status };
        const body = await response.json();
        return { ok: true, status: response.status, body };
      })());
    }
    return answers.get(url).then(answer => ({ ok: answer.ok, status: answer.status, json: async () => answer.body, text: async () => JSON.stringify(answer.body ?? null) }));
  };
}

const whyDisconnected = widget => {
  const keys = Object.keys(widget.quality ?? {}).filter(key => widget.quality[key] === 'disconnected');
  if (keys.length === 0) return undefined;
  const messages = keys.map(key => widget.messages?.[key] ?? { address: 'the read does not hold what the part points at', format: 'the read holds it in a form the part cannot use', auth: 'the host says you may not read this', transport: 'the host could not be reached', source: 'the host could not answer' }[widget.reasons?.[key]] ?? 'it could not be read');
  return [...new Set(messages)].join('; ');
};

/** The first row of a read's answer: what a KPI's pointer reads from. */
const firstRow = body => (Array.isArray(body?.value) ? body.value[0] : body?.value && typeof body.value === 'object' ? body.value : undefined);

function judgeMetric(part, widget, reads, document) {
  const value = widget.props?.data?.value;
  if (value === '' || value === undefined || value === null) return { state: 'problem', problem: 'it has no value' };
  if (typeof value === 'number' && !Number.isFinite(value)) return { state: 'problem', problem: `its value is ${value}` };
  if (value === 0 || value === '0') {
    const row = firstRow(reads[0]?.body);
    const others = row ? Object.entries(row).filter(([key, item]) => !key.startsWith('@') && isNumber(item) && asNumber(item) !== 0).slice(0, 4) : [];
    if (others.length > 0) {
      const at = document.data?.[readsOfPart(part)[0]]?.fields?.[part.show]?.at;
      return { state: 'check', shows: '0', problem: `it shows 0, yet the read's row holds non-zero ${others.map(([key, item]) => `${key}=${short(item)}`).join(', ')}${at ? ` (this tile reads ${at})` : ''} — if 0 is not what it should show, point it at the right field` };
    }
  }
  return { state: 'ok', shows: short(value) };
}

// The keys of a chart's mapping that name a field of its rows, and the ones among them that must hold numbers (a category axis holds words).
const DATA_KEYS = ['x', 'y', 'label', 'value', 'axis', 'total', 'start', 'end'];
const MEASURES = {
  'chart.bar': ['y'], 'chart.line': ['y'], 'chart.area': ['y'], 'chart.scatter': ['y'], 'chart.waterfall': ['y', 'total'],
  'chart.pie': ['value'], 'chart.funnel': ['value'], 'chart.heatmap': ['value'], 'chart.histogram': ['value'], 'chart.treemap': ['value'], 'chart.radar': ['y'],
};

function judgeRows(part, widget, rows, count) {
  const kind = widget.type;
  const sample = rows.slice(0, SAMPLE_ROWS);
  const names = fieldNames(sample);
  const isChart = kind.startsWith('chart.');
  // Every field the part names (an axis, a label, a value) must be one the rows have.
  const named = isChart
    ? Object.entries(widget.props?.mapping ?? {}).filter(([key]) => DATA_KEYS.includes(key)).map(([key, value]) => [key, fieldsOf(value)])
    : kind === 'bar-list' ? ['label', 'value'].filter(key => key in part).map(key => [key, fieldsOf(part[key])]) : [];
  const missing = named.flatMap(([key, fields]) => fields.filter(field => !names.includes(field)).map(field => `${key} "${field}"`));
  if (missing.length > 0) return { state: 'problem', problem: `${missing.join(', ')} ${missing.length === 1 ? 'is' : 'are'} not in the rows (they have: ${names.join(', ')})` };

  if (isChart) {
    const mapping = widget.props?.mapping ?? {};
    const bits = [];
    const axis = fieldsOf(mapping.x)[0];
    if (axis) {
      const categories = sample.map(row => row[axis]).filter(item => item !== undefined && item !== null);
      if (categories.length > 0) bits.push(`${axis} ${short(categories[0])}${categories.length > 1 ? `…${short(categories.at(-1))}` : ''}`);
    }
    for (const field of (MEASURES[kind] ?? []).flatMap(key => fieldsOf(mapping[key]))) {
      const numbers = sample.map(row => row[field]).filter(isNumber).map(asNumber);
      if (numbers.length === 0) return { state: 'problem', problem: `${field} has no numbers in its ${plural(rows.length, 'row', 'rows')} (first values: ${sample.slice(0, 3).map(row => short(row[field])).join(', ')})` };
      if (numbers.every(item => item === 0)) return { state: 'check', shows: `${plural(rows.length, 'point', 'points')}, ${field} all 0`, problem: `every ${field} value is 0` };
      bits.push(`${field} ${short(Math.min(...numbers))}…${short(Math.max(...numbers))}`);
    }
    return { state: 'ok', shows: `${plural(rows.length, 'point', 'points')}${bits.length ? ` · ${bits.join(' · ')}` : ''}` };
  }
  const total = count !== undefined && count !== rows.length ? ` of ${Number(count).toLocaleString('en-US')}` : '';
  return { state: 'ok', shows: `${plural(rows.length, 'row', 'rows')}${total}` };
}

/** What one placed widget drew, as `{ state: 'ok' | 'check' | 'problem' | 'empty', shows?, problem? }`, or nothing when there is nothing to say about it. */
function judge(part, widget, reads, document) {
  const unread = whyDisconnected(widget);
  if (unread) return { state: 'problem', problem: `it could not be read: ${unread}` };
  if (widget.type === 'metric') return judgeMetric(part, widget, reads, document);
  const data = widget.props?.data;
  if (data === undefined) return undefined;
  if (!Array.isArray(data)) {
    // A widget that draws a list of rows (a chart, a table) cannot draw an object; the others (a card, a gauge) take one.
    return widget.type.startsWith('chart.') || widget.type === 'table' || widget.type === 'bar-list'
      ? { state: 'problem', problem: 'the read answers an object where the part needs a list of rows — check the read\'s "list" pointer' }
      : undefined;
  }
  if (data.length === 0) return { state: 'empty', shows: 'no rows' };
  return judgeRows(part, widget, data, reads[0]?.count);
}

/**
 * Draws each of `ids` as the viewer does and says what came out.
 *   document   the board as saved
 *   ids        the parts to look at (those the edit touched); parts that read nothing are left out
 *   fetcher    the page's fetch, for the board's data reads (each distinct read is made once)
 *   variables  what the board's variables take (default: each one's default, as the viewer starts)
 *   now        the clock
 *   signal     the turn's AbortSignal: a check the person stopped says nothing
 * Resolves to `{ tiles: [{ id, kind, state, shows?, problem? }] }`, or undefined when it was stopped. It never throws on a read that fails: that is a tile's problem.
 */
export async function inspectRendering({ document, ids, fetcher, variables, now = () => Date.now(), signal }) {
  const read = cachedFetch(fetcher);
  const given = variables ?? variableDefaults(document.variables);
  const tiles = [];
  for (const id of ids) {
    const part = document.parts?.[id];
    if (!part || readsOfPart(part).length === 0) continue;
    const reads = [];
    const adapters = [makeODataAdapter((path, rows, count, body) => reads.push({ path, rows, count, body }), read)];
    const { _title, ...board } = tileBoard(document, id);
    let verdict;
    try {
      const { picture } = await drawBoard(board, readerOf(adapters), { variables: given });
      const resolved = await resolveDocument(picture, adapters, now());
      for (const placed of placedIn(resolved)) {
        verdict = judge(part, placed.widget, reads, document);
        if (verdict && verdict.state !== 'ok') break; // the first thing wrong with a part is the one to say
      }
    } catch (error) {
      verdict = { state: 'problem', problem: `it could not be drawn: ${error instanceof Error ? error.message : String(error)}` };
    }
    if (signal?.aborted) return undefined;
    if (verdict) tiles.push({ id, kind: part.as, ...verdict });
  }
  return { tiles };
}

/** What the model is told: one line per part that drew as it should, the parts that did not with why, the parts whose read matched nothing, and what to do next. */
export function renderedForModel(inspection, { fixesLeft }) {
  const { tiles } = inspection;
  const wrong = tiles.filter(tile => tile.state === 'problem' || tile.state === 'check');
  const out = {
    checked: tiles.length,
    ...(tiles.some(tile => tile.state === 'ok') && { drew: tiles.filter(tile => tile.state === 'ok').slice(0, MAX_LISTED).map(tile => line(`${tile.id}: ${tile.shows}`)) }),
    ...(wrong.length > 0 && { problems: wrong.slice(0, MAX_LISTED).map(tile => ({ id: tile.id, kind: tile.kind, ...(tile.shows && { shows: tile.shows }), problem: line(tile.problem) })) }),
    ...(tiles.some(tile => tile.state === 'empty') && { empty: tiles.filter(tile => tile.state === 'empty').map(tile => tile.id) }),
  };
  if (wrong.length > 0)
    out.next = fixesLeft > 0
      ? 'These tiles do not draw what you meant. Fix the part or its read with one more patch_board, then say what the tile shows. If what a tile shows is right (a real 0), say so and do not edit.'
      : 'Do not edit again. Tell the person which tiles do not draw as intended, what they show, and what you would try next.';
  return out;
}

/** The sentence a step carries for the person: how many parts were looked at and how many are not right. */
export function renderedDetail(inspection) {
  const wrong = inspection.tiles.filter(tile => tile.state === 'problem' || tile.state === 'check').length;
  if (inspection.tiles.length === 0) return undefined;
  return wrong === 0 ? `drew as read: ${plural(inspection.tiles.length, 'tile', 'tiles')} checked` : `${plural(wrong, 'tile', 'tiles')} did not draw as intended`;
}
