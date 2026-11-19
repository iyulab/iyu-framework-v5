// How one part of a board becomes the small board its tile draws, and how a board's grid reads as a list of tiles. Plain functions with no page in
// them, so the tile the viewer draws and the check of what it drew (render-check.js) start from the same board.
import { tileReads } from './tile-data.js';

const readName = reference => (typeof reference === 'string' ? /^([^[\]]+)/.exec(reference)?.[1] : undefined);

/** The board's grid as a list of `{ id, span }`; undefined for a layout this page does not tile (it is then drawn whole). */
export function gridItems(document) {
  if (document?.layout?.kind !== 'grid') return undefined;
  const items = [];
  for (const item of document.layout.items ?? []) {
    if (typeof item === 'string') items.push({ id: item, span: 12 });
    else if (Array.isArray(item) && typeof item[0] === 'string') items.push({ id: item[0], span: Number(item[1]) || 12 });
    else return undefined;
  }
  return items.filter(({ id }) => document.parts?.[id]);
}

/** The reads of a part, by name. */
export const readsOfPart = part => [readName(part.of), readName(part.each)].filter(Boolean);

/** The board that draws one part: its reads, its variables, its look — and no title and no frame of its own (the tile has them). */
export function tileBoard(document, id) {
  const part = document.parts[id];
  const reads = readsOfPart(part);
  const keepTitle = part.as === 'label';
  const { title, ...rest } = part;
  return {
    ...(document.variables && { variables: document.variables }),
    ...(document.statuses && { statuses: document.statuses }),
    ...(document.presets && { presets: document.presets }),
    // The tile is the card: the viewer's own paper is left clear so the card shows, not a card in a card.
    skin: { ...document.skin, palette: { paper: 'transparent', backdrop: 'transparent', ...document.skin?.palette } },
    ...(document.board && { board: document.board }),
    data: tileReads(document, part, reads),
    parts: { [id]: { ...(keepTitle ? part : rest), frame: 'none' } },
    layout: { kind: 'grid', items: [[id, 12]] },
    ...(title !== undefined && { _title: title }),
  };
}
