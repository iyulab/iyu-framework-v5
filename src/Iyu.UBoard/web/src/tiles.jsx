// The canvas: the board drawn as tiles. Each part of the board is drawn by U-Board's own viewer (one small board per tile, so a
// tile can wear its «edited in r3» mark, its loading skeleton and its empty-result explanation); the tile's place and width
// are the board layout's own.
import { useMemo, useState } from 'react';
import { BoardView } from '@iyulab/u-board/viewer';
import { gridItems, readsOfPart, tileBoard } from './tile-board.js';
import { choosable, fillVariables, pageNote, variableDefaults } from './variables.js';

function Tile({ id, span, part, board, mark, revision, loading, emptyNote, count, rows, askedPath, adapters, variables, flat, selected, onSelect }) {
  const hasSkip = /[?&]\$skip=/.test(askedPath ?? '');
  const stable = useMemo(() => {
    const { _title, ...clean } = board;
    return clean;
  }, [JSON.stringify(board)]); // eslint-disable-line react-hooks/exhaustive-deps
  const edited = Boolean(mark) && !loading;
  const badge = loading ? <span className="pill new">updating…</span> : edited ? <span className="pill new">{mark} in r{revision}</span> : null;
  const isLabel = part.as === 'label';
  const isStat = part.as === 'stat';
  return (
    <section className={`tile ${edited ? 'edited' : ''} ${selected ? 'selected' : ''} ${onSelect ? 'pointable' : ''}`} data-selected={selected ? 'true' : undefined} tabIndex={onSelect ? 0 : undefined} role={onSelect ? 'button' : undefined} aria-pressed={onSelect ? Boolean(selected) : undefined} onClick={onSelect ? () => onSelect(id) : undefined} onKeyDown={onSelect ? event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onSelect(id); } } : undefined} style={{ gridColumn: `span ${span}` }} data-id={id} data-kind={part.as} data-mark={mark ?? ''} data-loading={loading ? 'true' : 'false'}>
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
        <div className="foot" data-testid="page-note"><span>{pageNote(askedPath, rows, count) ?? `${rows ?? '?'} of ${Number(count).toLocaleString('en-US')}`}</span><span>{hasSkip ? 'One server page · the page is chosen above the board' : 'One server page · rows are not paged further'}</span></div>
      )}
    </section>
  );
}

/** The controls for the board's variables: a choice per variable that lists options (a filter for the whole board, a page of a table). */
function VariableBar({ variables, values, onChoose }) {
  const offered = choosable(variables);
  if (offered.length === 0) return null;
  return (
    <div className="vars" data-testid="variables">
      {offered.map(({ name, label, options }) => (
        <label key={name} className="var">
          <span>{label}</span>
          <select data-var={name} value={values[name] ?? options[0]} onChange={event => onChoose(name, event.target.value)}>
            {options.map(option => <option key={option} value={option}>{option}</option>)}
          </select>
        </label>
      ))}
    </div>
  );
}

export function Tiles({ document, adapters, changed = {}, revision, loadIds = [], answers = {}, variables, flat, selected = [], onSelect }) {
  const [chosen, setChosen] = useState({});
  const defaults = useMemo(() => variableDefaults(document?.variables), [JSON.stringify(document?.variables)]); // eslint-disable-line react-hooks/exhaustive-deps
  // A choice the board no longer offers (its options changed) falls back to the default.
  const values = useMemo(() => {
    const kept = {};
    for (const { name, options } of choosable(document?.variables)) if (options.includes(chosen[name])) kept[name] = chosen[name];
    return { ...defaults, ...kept, ...variables };
  }, [defaults, chosen, variables, JSON.stringify(document?.variables)]); // eslint-disable-line react-hooks/exhaustive-deps
  const items = gridItems(document);
  if (!document || !items) return document ? <BoardView board={document} adapters={adapters} variables={values} /> : null;
  return (
    <>
      <VariableBar variables={document.variables} values={values} onChoose={(name, value) => setChosen(previous => ({ ...previous, [name]: value }))} />
      <div className="grid" data-testid="grid">
        {items.map(({ id, span }) => {
          const part = document.parts[id];
          const paths = readsOfPart(part).map(name => document.data?.[name]?.path).filter(Boolean).map(path => fillVariables(path, values));
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
              askedPath={paths.find(path => answers[path])}
              adapters={adapters}
              variables={values}
              flat={flat}
              selected={selected.includes(id)}
              onSelect={onSelect}
            />
          );
        })}
      </div>
    </>
  );
}
