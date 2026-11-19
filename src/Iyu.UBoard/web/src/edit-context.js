// What the person is pointing at, in a shape a model can read: the selected parts with their place and source, the reads they
// draw from, and the names of their neighbours — the answer to «what is this person pointing at, on which board, backed by what?».
// The shape follows the public edit-context contract of the Vivarium runtime (selection · screen · source · untrusted), kept to
// what a board has: a part id is already a stable name, so no identity layer is needed.
//
// Words that came from the board (titles) are data, not structure: they sit under `untrusted`, apart from the ids, kinds and
// places, and the block they travel in cannot be closed from inside them.

export const EDIT_CONTEXT_VERSION = '0.1';

const readName = reference => (typeof reference === 'string' ? /^([^[\]]+)/.exec(reference)?.[1] : undefined);
const readsOf = part => [readName(part?.of), readName(part?.each)].filter(Boolean);

/** The grid's entries as `{ id, span }`; a layout that is not a grid has no spans. */
function placements(document) {
  const layout = document?.layout;
  if (layout?.kind !== 'grid') return Object.fromEntries(Object.keys(document?.parts ?? {}).map(id => [id, undefined]));
  const out = {};
  for (const item of layout.items ?? []) {
    if (typeof item === 'string') out[item] = 12;
    else if (Array.isArray(item) && typeof item[0] === 'string') out[item[0]] = Number(item[1]) || 12;
  }
  return out;
}

/**
 * The context for the parts of `document` named in `selected`; undefined when none of them is on the board. An id the board does
 * not have is dropped.
 */
export function editContext(document, selected, { name, revision } = {}) {
  const parts = document?.parts;
  if (!parts || !Array.isArray(selected)) return undefined;
  const ids = [...new Set(selected)].filter(id => typeof id === 'string' && parts[id]);
  if (ids.length === 0) return undefined;
  const place = placements(document);
  const entry = id => ({ id, as: parts[id].as, ...(place[id] !== undefined && { span: place[id] }) });

  const reads = {};
  for (const id of ids) for (const read of readsOf(parts[id])) if (document.data?.[read]) reads[read] = document.data[read];

  const neighbours = Object.keys(place).filter(id => parts[id] && !ids.includes(id)).map(entry);
  const untrusted = {};
  for (const id of [...ids, ...neighbours.map(n => n.id)]) if (typeof parts[id].title === 'string') untrusted[id] = { title: parts[id].title };

  return {
    editContextVersion: EDIT_CONTEXT_VERSION,
    ...((name !== undefined || revision !== undefined) && { board: { name, revision } }),
    selection: ids.map(entry),
    parts: Object.fromEntries(ids.map(id => [id, parts[id]])),
    reads,
    neighbours,
    untrusted,
  };
}

/** The context as the block a message ends with. `<` is written as <, so nothing inside can write the closing tag. */
export function contextBlock(context) {
  return `<edit-context>
${JSON.stringify(context).replace(/</g, '\\u003c')}
</edit-context>`;
}
