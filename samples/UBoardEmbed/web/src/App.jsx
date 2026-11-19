import { useCallback, useEffect, useMemo, useState } from 'react';
import '@iyulab/u-board/styles.css';
import { BoardView } from '@iyulab/u-board/viewer';
import { checkBoard } from '@iyulab/u-board/check';
import { isHttpRef, readHttpRef, resolvedReading } from '@iyulab/u-board/picture';
import { BOARD_ID, BoardMovedOn, callTool, host, openSession } from './host.js';

// Where the board's values come from. This sample answers from a table in the page; a real adapter asks the host's own system.
const ANSWERS = {
  '/lines/L1/pumps': { value: [{ id: 'P1', state: 'run' }, { id: 'P2', state: 'trip' }] },
  '/lines/L2/pumps': { value: [{ id: 'P3', state: 'run' }] },
};

const plant = {
  id: 'plant',
  async resolve(ref) {
    const answer = isHttpRef(ref) ? ANSWERS[ref.path] : undefined;
    const reading = isHttpRef(ref) && answer !== undefined ? readHttpRef(answer, ref, Date.now()) : undefined;
    if (!reading?.ok) return { value: undefined, quality: 'disconnected', reason: 'address' };
    return { ...resolvedReading(reading), meta: { source: 'sample table' } };
  },
};
const adapters = [plant];

const STARTER = {
  variables: { line: { options: ['L1', 'L2'], default: 'L1' } },
  statuses: { running: { tone: 'ok', label: 'Running' }, tripped: { tone: 'alarm', label: 'Tripped' } },
  data: {
    pumps: {
      from: 'plant',
      path: '/lines/{{$line}}/pumps',
      list: '/value',
      key: 'id',
      fields: { state: { state: { run: 'running', trip: 'tripped' } } },
    },
  },
  parts: {
    header: { as: 'header', title: 'Plant overview', subtitle: 'Line status' },
    pump: { as: 'status-card', each: 'pumps', title: 'Pump {{key}}', show: ['state'] },
  },
  layout: { kind: 'grid', items: ['header', 'pump'] },
};

// A document this viewer's check refuses: an option the header does not have. A document like this can only reach the host
// from something that does not go through the viewer's store.
function withUnknownOption(document) {
  return { ...document, parts: { ...document.parts, header: { ...document.parts.header, colour: 'red' } } };
}

const box = { border: '1px solid #8884', borderRadius: 8, padding: 12, minWidth: 0 };
const mono = { fontFamily: 'ui-monospace, monospace', fontSize: 12, whiteSpace: 'pre-wrap', margin: 0 };

// A session keeps what it loaded until its own person reloads: another session saving does not refresh it, which is how two
// people on one board come to work from different revisions.
function Session({ name, initial, onSaved }) {
  const [loaded, setLoaded] = useState(initial);
  const session = useMemo(() => openSession(loaded), [loaded]);
  const [view, setView] = useState({ document: loaded.document, revision: loaded.revision });
  useEffect(() => setView({ document: loaded.document, revision: loaded.revision }), [loaded]);
  const reloaded = async () => setLoaded(await host.get());
  const [text, setText] = useState('');
  const [log, setLog] = useState([]);

  const refresh = useCallback(async () => {
    const { document, revision } = await session.store.getBoard('board');
    setView({ document, revision });
  }, [session]);

  const rename = async () => {
    const result = await callTool(session, 'patch_board', {
      patch: [{ op: 'replace', path: '/parts/header/subtitle', value: text }],
    });
    setLog(lines => [`${name} patch_board "${text}" → ${JSON.stringify(result.answer)}`, ...lines].slice(0, 6));
    await refresh();
    if (!result.isError && !result.answer.error) onSaved();
  };

  return (
    <div style={box}>
      <h3 style={{ margin: '0 0 4px' }}>Session {name}</h3>
      <p style={{ margin: '0 0 8px', fontSize: 13 }}>
        loaded at revision <b>{loaded.revision}</b> · this session is at revision <b>{view.revision}</b> · host knows{' '}
        <b>{session.known.revision}</b>
      </p>
      <div style={{ height: 220, border: '1px solid #8883', borderRadius: 6, overflow: 'auto' }}>
        <BoardView board={view.document} adapters={adapters} variables={{ line: 'L1' }} />
      </div>
      <div style={{ display: 'flex', gap: 6, margin: '8px 0' }}>
        <input
          aria-label={`subtitle in session ${name}`}
          value={text}
          onChange={event => setText(event.target.value)}
          placeholder="new subtitle"
          style={{ flex: 1 }}
        />
        <button onClick={rename} disabled={!text}>Rename (patch_board)</button>
        <button onClick={reloaded}>Reload from host</button>
      </div>
      <pre style={mono}>{log.join('\n') || 'nothing done in this session yet'}</pre>
    </div>
  );
}

function Reader({ tick }) {
  const [shown, setShown] = useState({ state: 'loading' });

  useEffect(() => {
    let current = true;
    (async () => {
      const published = await host.published();
      if (!published) return current && setShown({ state: 'unpublished' });
      const issues = checkBoard(published.document).issues;
      if (issues.length === 0) return current && setShown({ state: 'published', revision: published.revision, document: published.document });

      // The publication in force is one the viewer refuses. Fall back through what was published before it.
      for (const candidate of await host.fallback()) {
        if (checkBoard(candidate.document).issues.length === 0)
          return current && setShown({
            state: 'fell-back',
            revision: candidate.revision,
            refused: published.revision,
            why: `${issues[0].path} — ${issues[0].message}`,
            document: candidate.document,
          });
      }
      return current && setShown({ state: 'nothing-usable', refused: published.revision, why: `${issues[0].path} — ${issues[0].message}` });
    })();
    return () => { current = false; };
  }, [tick]);

  return (
    <div style={box}>
      <h3 style={{ margin: '0 0 4px' }}>What readers see</h3>
      <p style={{ margin: '0 0 8px', fontSize: 13 }} data-testid="reader-state">
        {shown.state === 'loading' && 'loading…'}
        {shown.state === 'unpublished' && 'Nothing is published.'}
        {shown.state === 'published' && <>Published revision <b>{shown.revision}</b>.</>}
        {shown.state === 'fell-back' && <>Revision <b>{shown.refused}</b> is published but the viewer refuses it ({shown.why}) — showing revision <b>{shown.revision}</b>, the last one it accepts.</>}
        {shown.state === 'nothing-usable' && <>Revision <b>{shown.refused}</b> is published but the viewer refuses it ({shown.why}) and nothing earlier is usable.</>}
      </p>
      <div style={{ height: 260, border: '1px solid #8883', borderRadius: 6, overflow: 'auto' }}>
        {shown.document ? <BoardView board={shown.document} adapters={adapters} variables={{ line: 'L1' }} /> : null}
      </div>
    </div>
  );
}

export function App() {
  const [loaded, setLoaded] = useState(undefined); // the host's board now; undefined: not asked yet; null: there is none
  const [initial, setInitial] = useState(undefined); // what the sessions start from, taken once
  const [tick, setTick] = useState(0);
  const [revisions, setRevisions] = useState([]);
  const [publications, setPublications] = useState([]);
  const [note, setNote] = useState('');

  const load = useCallback(async () => {
    const board = await host.get();
    setLoaded(board);
    setInitial(first => first ?? board ?? undefined);
    setRevisions(await host.revisions());
    setPublications(await host.publications());
    setTick(value => value + 1);
  }, []);

  useEffect(() => { load(); }, [load]);

  const create = async () => {
    const made = await host.create(STARTER);
    setNote(`create → ${made.status}`);
    await load();
  };

  const publish = async revision => {
    await host.publish(revision);
    await load();
  };

  const withdraw = async () => {
    await host.withdraw();
    await load();
  };

  const breakIt = async () => {
    // What a second writer that skips the viewer's check can do: save a document the viewer refuses.
    const board = await host.get();
    const written = await host.putRaw(withUnknownOption(board.document), board.revision);
    setNote(`raw save of an invalid document → ${written.status}, host is now at revision ${written.board.revision}`);
    await load();
  };

  if (loaded === undefined) return <p>loading…</p>;
  if (loaded === null)
    return (
      <div style={{ padding: 16 }}>
        <h2>Board embedding sample</h2>
        <p>The host holds no board called “{BOARD_ID}” yet.</p>
        <button onClick={create}>Create it</button>
      </div>
    );

  return (
    <div style={{ padding: 16, fontFamily: 'system-ui, sans-serif', display: 'grid', gap: 12 }}>
      <h2 style={{ margin: 0 }}>Board embedding sample</h2>
      <p style={{ margin: 0, fontSize: 13 }}>
        Host: board “{loaded.id}” at revision <b>{loaded.revision}</b>, publishing{' '}
        <b>{loaded.publishedRevision ?? 'nothing'}</b>. {note}
      </p>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(360px, 1fr))', gap: 12 }}>
        <Session name="A" initial={initial} onSaved={load} />
        <Session name="B" initial={initial} onSaved={load} />
        <Reader tick={tick} />
      </div>

      <div style={box}>
        <h3 style={{ margin: '0 0 8px' }}>Revisions and publication</h3>
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 8 }}>
          {revisions.map(r => (
            <button key={r.revision} onClick={() => publish(r.revision)}>Publish revision {r.revision}</button>
          ))}
          <button onClick={withdraw}>Withdraw</button>
          <button onClick={breakIt}>Save an invalid document behind the viewer’s back</button>
        </div>
        <pre style={mono}>
          {publications.length === 0
            ? 'no publication history'
            : publications.map(p => `#${p.Sequence ?? p.sequence}: ${(p.Revision ?? p.revision) === null ? 'withdrawn' : `revision ${p.Revision ?? p.revision}`}`).join('\n')}
        </pre>
      </div>
    </div>
  );
}
