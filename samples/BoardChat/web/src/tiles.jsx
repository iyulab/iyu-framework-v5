// The canvas: the board drawn as tiles. Each part of the board is drawn by U-Board's own viewer (one small board per tile, so a
// tile can wear its «edited in r3» mark, its loading skeleton and its empty-result explanation); the tile's place and width
// are the board layout's own.
import { useMemo } from 'react';
import { BoardView } from '@iyulab/u-board/viewer';
import { isHttpRef, readHttpRef, requestPath, resolvedReading } from '@iyulab/u-board/picture';
import { DATA_SOURCE_ID } from './agent-loop.js';
import { hostFetch } from './host-api.js';
import { tileReads } from './tile-data.js';

const readName = reference => (typeof reference === 'string' ? /^([^[\]]+)/.exec(reference)?.[1] : undefined);

/** One adapter for the host's OData: a read's `path` is asked of the host as the signed-in person. `onAnswer(path, rows, count)`
 *  tells the page what came back, so a tile can say «nothing matches» and how many rows a page is of. */
export function makeODataAdapter(onAnswer) {
  return {
    id: DATA_SOURCE_ID,
    async resolve(ref) {
      if (!isHttpRef(ref)) return { value: undefined, quality: 'disconnected', reason: 'address' };
      const path = requestPath(ref, Date.now());
      let response;
      try {
        response = await hostFetch(path);
      } catch {
        return { value: undefined, quality: 'disconnected', reason: 'transport' };
      }
      if (!response.ok) return { value: undefined, quality: 'disconnected', reason: response.status === 401 || response.status === 403 ? 'auth' : 'source', message: `the host answered ${response.status}` };
      const body = await response.json();
      const reading = readHttpRef(body, ref, Date.now());
      if (!reading.ok) return { value: undefined, quality: 'disconnected', reason: reading.reason, message: reading.message };
      const rows = Array.isArray(body.value) ? body.value.length : undefined;
      onAnswer?.(ref.path, rows, body['@odata.count']);
      return { ...resolvedReading(reading), meta: { source: 'the app\'s data service' } };
    },
  };
}

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

function Tile({ id, span, part, board, mark, revision, loading, emptyNote, count, rows, adapters, variables, flat }) {
  const stable = useMemo(() => {
    const { _title, ...clean } = board;
    return clean;
  }, [JSON.stringify(board)]); // eslint-disable-line react-hooks/exhaustive-deps
  const edited = Boolean(mark) && !loading;
  const badge = loading ? <span className="pill new">updating…</span> : edited ? <span className="pill new">{mark} in r{revision}</span> : null;
  const isLabel = part.as === 'label';
  const isStat = part.as === 'stat';
  return (
    <section className={`tile ${edited ? 'edited' : ''}`} style={{ gridColumn: `span ${span}` }} data-id={id} data-kind={part.as} data-mark={mark ?? ''} data-loading={loading ? 'true' : 'false'}>
      {/* A stat draws its caption itself (the part's title, see tile-data.js), so its tile has no header row — only the mark. */}
      {!isLabel && !isStat && (
        <div className="th">
          <span className="tt">{part.title ?? id}</span>
          {badge}
        </div>
      )}
      {isStat && badge && <div className="corner">{badge}</div>}
      {loading ? (
        <div className="skel" style={{ height: part.as === 'stat' ? 70 : 220 }} />
      ) : emptyNote ? (
        <div className="overlay-empty" data-empty="true">
          <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="var(--faint)" strokeWidth="1.6"><circle cx="12" cy="12" r="9" /><path d="M8 12h8" /></svg>
          <h2>No data for “{part.title ?? id}”</h2>
          <p>Nothing matches yet. The tile fills in when data appears.</p>
        </div>
      ) : (
        <div className="tbody"><BoardView board={stable} adapters={adapters} variables={variables} /></div>
      )}
      {!loading && !emptyNote && part.as === 'table' && count !== undefined && (
        <div className="foot" data-testid="page-note"><span>Showing 1–{rows ?? '?'} of {Number(count).toLocaleString('en-US')}</span><span>One server page · rows are not loaded at once</span></div>
      )}
    </section>
  );
}

export function Tiles({ document, adapters, changed = {}, revision, loadIds = [], answers = {}, variables, flat }) {
  const items = gridItems(document);
  if (!document || !items) return document ? <BoardView board={document} adapters={adapters} variables={variables} /> : null;
  return (
    <div className="grid" data-testid="grid">
      {items.map(({ id, span }) => {
        const part = document.parts[id];
        const paths = readsOfPart(part).map(name => document.data?.[name]?.path).filter(Boolean);
        const first = paths.map(path => answers[path]).find(Boolean);
        const empty = paths.length > 0 && paths.every(path => answers[path] && answers[path].rows === 0);
        return (
          <Tile
            key={id}
            id={id}
            span={span}
            part={part}
            board={tileBoard(document, id)}
            mark={changed[id]}
            revision={revision}
            loading={loadIds.includes(id)}
            emptyNote={empty}
            count={first?.count}
            rows={first?.rows}
            adapters={adapters}
            variables={variables}
            flat={flat}
          />
        );
      })}
    </div>
  );
}
