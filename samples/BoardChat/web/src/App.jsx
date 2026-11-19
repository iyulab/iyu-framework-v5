import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import '@iyulab/u-board/styles.css';
import { checkBoard } from '@iyulab/u-board/check';
import { changedParts, createAssistant, newBoard } from './agent-loop.js';
import { BOARD, USERS, api, hostFetch, identity, revisionAuthor, revisionNumber, revisionTime } from './host-api.js';
import { Tiles, makeODataAdapter } from './tiles.jsx';
import { usedFields } from './used-fields.js';

// What the sample's chips say. A chip is a prepared message: pressing one sends it like any typed message.
const PROMPTS = {
  'Monthly revenue with KPIs': 'Show monthly revenue for 2026, with total revenue, order count and average order value up top.',
  'Top products this year': 'Show the top 5 products by revenue this year.',
  'Customers by signup month': 'Show customers by signup month.',
  'Orders needing attention': 'Which orders need attention?',
  'Split by region and add top products': 'Split revenue by region as stacked bars, and add the top 5 products.',
  'Only paid orders, mark months under target': 'Only paid orders — and mark the months under our 30k target.',
};
const AFTER = [
  ['Split by region and add top products'],
  ['Only paid orders, mark months under target'],
  ['Show margin by product', 'List the latest orders', 'Show refunded orders in October', 'Add a cumulative line'],
];

const iso = value => (value ? new Date(value).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '');

function chipsAfter(result, successes) {
  const failure = result.failure;
  if (!failure) return AFTER[Math.min(successes, AFTER.length) - 1] ?? [];
  if (failure.kind === 'permission') return failure.alternatives.slice(0, 2).map(field => `Use ${field}`);
  if (failure.kind === 'rejected') {
    const near = /Did you mean (.+?)\?/.exec(failure.suggestion)?.[1];
    return [...(near ? [`Use ${near}`] : []), 'Cancel'];
  }
  if (failure.kind === 'conflict') return [`Reload r${failure.serverRevision} and retry`];
  return [];
}

function Step({ step }) {
  const fail = step.state === 'error' || step.outcome === 'rejected';
  const cls = step.state === 'running' ? 'run' : step.state === 'pending' ? 'wait' : fail ? 'fail' : 'done';
  const icon = step.state === 'running' ? <span className="spin" /> : step.state === 'pending' ? '○' : fail ? '✕' : '✓';
  return (
    <div className={`step ${cls}`} data-step={step.kind} data-state={step.state} data-outcome={step.outcome ?? ''}>
      <span className="ic">{icon}</span>
      <div>
        <b>{step.label}</b>
        {step.detail ? <span style={{ color: 'var(--muted)' }}> — {step.detail}</span> : null}
        {step.raw && (step.state === 'done' || step.state === 'error') ? (
          <details>
            <summary>Show the call</summary>
            <pre className="mono" data-testid="raw-call">{step.raw}</pre>
          </details>
        ) : null}
      </div>
    </div>
  );
}

function Chat({ thread, revision, busy, onSend, onChip, text, setText, pointing = [], onClearPointing }) {
  const ref = useRef(null);
  useEffect(() => {
    if (ref.current) ref.current.scrollTop = ref.current.scrollHeight;
  }, [thread]);
  return (
    <aside className="chat">
      <div className="chead">
        <span className="tt">Assistant</span>
        <span className="pill hide-s" data-tip="Model calls go through your server; keys never reach the browser">via your server</span>
        <span className="grow" />
        <span className="pill">{revision ? `Draft r${revision}` : 'No revisions yet'}</span>
      </div>
      <div className="msgs" id="msgs" ref={ref}>
        {thread.map((m, i) => {
          if (m.role === 'u') return <div key={i} className="m u" data-role="user">{m.text}{m.on?.length ? <span className="on" data-testid="user-on">on {m.on.map(o => o.title ?? o.id).join(', ')}</span> : null}</div>;
          const last = i === thread.length - 1;
          return (
            <div key={i} className="m a" data-role="assistant">
              <div className="av">✦</div>
              <div className="ab">
                {m.steps?.length ? <div className="steps">{m.steps.map(step => <Step key={step.id} step={step} />)}</div> : null}
                {m.text ? <div data-testid="reply">{m.text}</div> : null}
                {last && m.chips?.length ? (
                  <div className="chips">{m.chips.map(chip => <button key={chip} className="chip" data-chip={chip} disabled={busy} onClick={() => onChip(chip)}>{chip}</button>)}</div>
                ) : null}
              </div>
            </div>
          );
        })}
      </div>
      <div className="compose">
        <div className="ctx"><span>📌</span>The open board is sent with every message<span className="grow" /><span data-testid="ctx-rev">{revision ? `r${revision}` : ''}</span></div>
        {pointing.length ? (
          <div className="pointing" data-testid="selection-chip"><span>Pointing at</span><b>{pointing.map(p => p.title ?? p.id).join(', ')}</b><button aria-label="Clear selection" data-act="clear-selection" onClick={onClearPointing}>×</button></div>
        ) : null}
        <div className="box">
          <textarea id="inp" rows={1} placeholder={pointing.length ? 'Say what to change about this…' : 'Ask for a chart, a filter, a change…'} aria-label="Message the assistant" value={text} disabled={busy}
            onChange={event => setText(event.target.value)}
            onKeyDown={event => { if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); onSend(); } }} />
          <button className="btn primary" data-act="send" aria-label="Send" disabled={busy || !text.trim()} onClick={onSend}>Send</button>
        </div>
      </div>
    </aside>
  );
}

function Catalog({ catalog, used, flash }) {
  const [open, setOpen] = useState({ Orders: true });
  return (
    <aside className="cat">
      <h3>Your data</h3>
      <div className="conn"><span className="dot" /><div>Read from the app's data service <span className="mono">/$data</span> <b>as you</b> — the assistant sees only what your role can read.</div></div>
      {(catalog?.entities ?? []).filter(entity => entity.readable !== false).map(entity => (
        <div className="set" key={entity.name}>
          <button data-open={entity.name} onClick={() => setOpen(o => ({ ...o, [entity.name]: !o[entity.name] }))}>
            <span>{open[entity.name] ? '▾' : '▸'}</span><span className="n">{entity.name}</span><span className="c">{entity.properties.length}</span>
          </button>
          {open[entity.name] ? (
            <div className="fields">
              {entity.properties.map(property => {
                const locked = property.readable === false;
                const isUsed = used.has(`${entity.name}.${property.name}`);
                return (
                  <div key={property.name} className={`f ${isUsed ? 'used' : ''} ${locked ? 'locked' : ''} ${locked && flash === `${entity.name}.${property.name}` ? 'flash' : ''}`} data-field={`${entity.name}.${property.name}`} data-locked={locked ? 'true' : 'false'} {...(locked ? { 'data-tip': 'Hidden by a read policy for your role' } : {})}>
                    <span className={`u ${isUsed ? '' : 'off'}`} />{property.name}{locked ? ' 🔒' : ''}<span className="t">{property.type}</span>
                  </div>
                );
              })}
              {(entity.links ?? []).map(link => (
                <div key={link.name} className={`f ${used.has(`${entity.name}.${link.name}`) ? 'used' : ''}`} data-field={`${entity.name}.${link.name}`}>
                  <span className={`u ${used.has(`${entity.name}.${link.name}`) ? '' : 'off'}`} />{link.name}<span className="t">→ {link.target}</span>
                </div>
              ))}
            </div>
          ) : null}
        </div>
      ))}
      <div className="catfoot">Catalog built from <span className="mono">$metadata</span> · {(catalog?.entities ?? []).length} sets · dots mark fields this board uses</div>
    </aside>
  );
}

function History({ revisions, current, published, onClose, onPublish, onRestore, onCompare, compare }) {
  return (
    <div className="drawer" data-testid="drawer">
      <div style={{ display: 'flex', alignItems: 'center', marginBottom: 12 }}><b style={{ fontSize: 15 }}>History</b><span className="grow" /><button className="btn sm" data-act="closehist" onClick={onClose}>Close</button></div>
      <p style={{ color: 'var(--muted)', margin: '0 0 12px', fontSize: 13 }}>Each save is a revision that never changes. <b>Publishing</b> points readers at one; publishing an older revision is the rollback.</p>
      {[...revisions].reverse().map(row => {
        const n = revisionNumber(row);
        return (
          <div className="dr" key={n} data-rev={n}>
            <div className="h">r{n}{n === current ? <span className="pill draft">draft</span> : null}{n === published ? <span className="pill pub">published</span> : null}<span className="grow" /><span style={{ color: 'var(--faint)', fontWeight: 400, fontSize: 12 }}>{iso(revisionTime(row))}</span></div>
            <div className="s">Saved by {revisionAuthor(row) ?? 'unknown'}</div>
            {compare?.revision === n ? <div className="cmp" data-testid="compare">{compare.text}</div> : null}
            <div className="a">
              <button className="btn sm" data-act="compare" onClick={() => onCompare(n)}>Compare</button>
              {n !== published ? <button className="btn sm" data-act="publish" data-rev={n} onClick={() => onPublish(n)}>Publish r{n}</button> : null}
              {n !== current ? <button className="btn sm" data-act="restore" onClick={() => onRestore(n)}>Restore as draft</button> : null}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function Reader({ tick, adapters, answers }) {
  const [shown, setShown] = useState({ state: 'loading' });
  useEffect(() => {
    let live = true;
    (async () => {
      const published = await api.published();
      if (!published) return live && setShown({ state: 'unpublished' });
      const issues = checkBoard(published.document).issues;
      if (issues.length === 0) return live && setShown({ state: 'published', revision: published.revision, document: published.document });
      for (const candidate of await api.fallback())
        if (checkBoard(candidate.document).issues.length === 0)
          return live && setShown({ state: 'fell-back', revision: candidate.revision, refused: published.revision, document: candidate.document });
      return live && setShown({ state: 'nothing-usable', refused: published.revision });
    })();
    return () => { live = false; };
  }, [tick]);
  return (
    <div className="cbody" data-testid="reader" data-reader-state={shown.state}>
      {shown.state === 'fell-back' ? (
        <div className="banner info" data-testid="fallback-banner"><span className="grow">Showing <b>r{shown.revision}</b> — the newest publication (r{shown.refused}) did not pass this viewer's check, so you see the last one that did.</span></div>
      ) : null}
      {shown.state === 'unpublished' ? <div className="empty"><h2>Nothing is published</h2><p>Publish a revision to share this board with readers.</p></div> : null}
      {shown.state === 'nothing-usable' ? <div className="banner bad"><span className="grow">Revision r{shown.refused} is published but this viewer refuses it, and nothing earlier is usable.</span></div> : null}
      {shown.document ? <Tiles document={shown.document} adapters={adapters} answers={answers} revision={shown.revision} /> : null}
    </div>
  );
}

export function App() {
  const [user, setUser] = useState(identity.user);
  const [boot, setBoot] = useState(false);
  const [catalog, setCatalog] = useState(null);
  const [live, setLive] = useState({ revision: 0, document: newBoard(BOARD.name).document });
  const liveRef = useRef(live);
  liveRef.current = live;
  const [changed, setChanged] = useState({});
  const [loadIds, setLoadIds] = useState([]);
  const [answers, setAnswers] = useState({});
  const [thread, setThread] = useState([{ role: 'hello', text: 'Hi — I can build a board from your data. Ask for what you want to see, and refine it as you go.', chips: ['Monthly revenue with KPIs', 'Top products this year', 'Customers by signup month', 'Orders needing attention'] }]);
  const [busy, setBusy] = useState(false);
  const [text, setText] = useState('');
  const [selected, setSelected] = useState([]);
  const [view, setView] = useState('editor');
  const [drawer, setDrawer] = useState(false);
  const [modal, setModal] = useState(null);
  const [banner, setBanner] = useState(null);
  const [revisions, setRevisions] = useState([]);
  const [published, setPublished] = useState(null);
  const [tick, setTick] = useState(0);
  const [flash, setFlash] = useState('');
  const [compare, setCompare] = useState(null);
  const assistant = useRef(null);
  const successes = useRef(0);
  const adaptersRef = useRef(null);
  if (!adaptersRef.current) adaptersRef.current = [makeODataAdapter((path, rows, count) => setAnswers(prev => ({ ...prev, [path]: { rows, count } })))];

  const refresh = useCallback(async () => {
    const rows = await api.revisions();
    setRevisions(rows);
    const pub = await api.published();
    setPublished(pub ? pub.revision : null);
    setTick(value => value + 1);
  }, []);

  const emit = useCallback(event => {
    if (event.type === 'turn-start') {
      setBusy(true);
      setBanner(null);
      const on = (event.selection ?? []).map(id => ({ id, title: liveRef.current?.document?.parts?.[id]?.title }));
      setThread(t => [...t.map(m => ({ ...m, chips: [] })), { role: 'u', text: event.text, on }, { role: 'a', steps: [], text: null, chips: [] }]);
    } else if (event.type === 'step') {
      setThread(t => {
        const next = [...t];
        const at = next.length - 1;
        const steps = [...(next[at].steps ?? [])];
        const found = steps.findIndex(s => s.id === event.step.id);
        if (found >= 0) steps[found] = event.step; else steps.push(event.step);
        next[at] = { ...next[at], steps };
        return next;
      });
    } else if (event.type === 'tiles-loading') setLoadIds(event.ids);
    else if (event.type === 'catalog') setCatalog(event.catalog);
    else if (event.type === 'board') {
      setLive({ revision: event.revision, document: event.document });
      setChanged(event.changed);
    } else if (event.type === 'failure') {
      if (event.failure.kind === 'rejected') setBanner({ kind: 'rejected', revision: event.failure.revision });
      if (event.failure.kind === 'conflict') setBanner({ kind: 'conflict', failure: event.failure });
      if (event.failure.kind === 'permission') setFlash(event.failure.field);
    }
  }, []);

  const open = useCallback(async name => {
    identity.user = name;
    setUser(name);
    const cat = await api.catalog();
    setCatalog(cat);
    const envelope = await api.board();
    const loaded = envelope ? { name: envelope.name, revision: envelope.revision, document: envelope.document } : newBoard(BOARD.name);
    setLive({ revision: loaded.revision, document: loaded.document });
    setChanged({});
    assistant.current = createAssistant({ fetch: (url, init) => fetch(url, init), userName: name, board: { id: BOARD.id, loaded }, emit });
    await refresh();
    setBoot(true);
  }, [emit, refresh]);

  useEffect(() => { open(identity.user); }, [open]);

  const send = useCallback(async message => {
    if (!message.trim() || !assistant.current) return;
    setText('');
    const selection = selected;
    setSelected([]);
    const result = await assistant.current.send(message, { selection });
    if (result.revision.to !== result.revision.from) successes.current += 1;
    setLoadIds([]);
    setThread(t => {
      const next = [...t];
      const at = next.length - 1;
      next[at] = { ...next[at], text: result.reply, chips: chipsAfter(result, successes.current), outcome: result.outcome, failure: result.failure };
      return next;
    });
    setBusy(false);
    await refresh();
  }, [refresh, selected]);

  const onChip = useCallback(async chip => {
    if (chip === 'Cancel') {
      setBanner(null);
      setThread(t => t.map(m => ({ ...m, chips: [] })));
      return;
    }
    const reload = /^Reload r(\d+) and retry$/.exec(chip);
    if (reload) {
      const failure = banner?.failure;
      if (!failure) return;
      assistant.current.reload(failure.board);
      setLive({ revision: failure.board.revision, document: failure.board.document });
      setChanged({});
      setBanner(null);
      setThread(t => [...t.map(m => ({ ...m, chips: [] })), { role: 'a', steps: [], text: `Reloaded r${failure.board.revision}. Retrying your edit on top of it…`, chips: [] }]);
      await send(failure.retryPrompt);
      return;
    }
    await send(PROMPTS[chip] ?? chip);
  }, [banner, send]);

  const reloadFromBanner = async () => {
    const failure = banner?.failure;
    if (!failure) return;
    await onChip(`Reload r${failure.serverRevision} and retry`);
  };

  const doPublish = async () => {
    const n = modal;
    setModal(null);
    setDrawer(false);
    const previous = published;
    await api.publish(n);
    setThread(t => [...t, { role: 'a', steps: [], text: `Published r${n}. Readers see it now${previous ? `; r${previous} is one click away if you need to roll back` : ''}.`, chips: [] }]);
    await refresh();
  };

  const doCompare = async n => {
    const [now, before] = await Promise.all([api.revision(n), n > 1 ? api.revision(n - 1) : null]);
    const marks = changedParts(before?.document, now.document);
    const words = Object.entries(marks).map(([id, mark]) => `${id} (${mark})`);
    setCompare({ revision: n, text: words.length ? `Compared with r${n - 1}: ${words.join(', ')}` : n > 1 ? `Same parts as r${n - 1}.` : 'The first revision.' });
  };

  const doRestore = async n => {
    const old = await api.revision(n);
    const saved = await api.save(old.document, live.revision);
    if (saved.status === 409) return;
    assistant.current.reload(saved.board);
    setLive({ revision: saved.board.revision, document: saved.board.document });
    setChanged({});
    setDrawer(false);
    await refresh();
  };

  const used = useMemo(() => usedFields(live.document, catalog), [live.document, catalog]);
  const reader = view === 'reader';
  const empty = Object.keys(live.document.parts ?? {}).length === 0;
  if (!boot) return <p style={{ padding: 16 }}>loading…</p>;

  return (
    <div className={reader ? 'reader' : ''}>
      <div className="app">
        <header className="top">
          <div className="logo">B</div>
          <span className="crumb hide-s">Boards /</span>
          <span className="title">{BOARD.name}</span>
          {reader ? (
            <><span className="pill pub" data-testid="pill-published">Published r{published}</span></>
          ) : (
            <>
              <span className="pill draft" data-testid="pill-draft">{live.revision ? `Draft r${live.revision}` : 'New board'}</span>
              {published ? <span className="pill pub hide-s" data-testid="pill-published">Published r{published}</span> : null}
            </>
          )}
          <span className="grow" />
          {reader ? (
            <button className="btn" data-act="editor" onClick={() => setView('editor')}>← Back to editing</button>
          ) : (
            <>
              <label className="note hide-s" htmlFor="who">Signed in as</label>
              <select id="who" className="btn" value={user} onChange={event => open(event.target.value)} aria-label="Signed-in person">
                {USERS.map(name => <option key={name} value={name}>{name}</option>)}
              </select>
              <button className="btn hide-s" data-act="history" onClick={() => setDrawer(true)}>History</button>
              <button className="btn hide-s" data-act="reader" onClick={() => { setView('reader'); setDrawer(false); }}>Preview as reader</button>
              <button className="btn primary" data-act="publish" data-rev={live.revision} disabled={!live.revision} onClick={() => setModal(live.revision)}>Publish…</button>
            </>
          )}
        </header>
        <div className="main">
          {reader ? null : <Catalog catalog={catalog} used={used} flash={flash} />}
          {reader ? (
            <div className="canvas"><Reader tick={tick} adapters={adaptersRef.current} answers={answers} /></div>
          ) : (
            <div className="canvas">
              <div className="cbody">
                {banner?.kind === 'conflict' ? (
                  <div className="banner bad" data-testid="banner-conflict"><b>Not saved.</b><span className="grow">Another session saved r{banner.failure.serverRevision} while you were on r{banner.failure.workedFrom}. Your change was discarded, nothing was overwritten.</span><button className="btn sm" data-act="reload" onClick={reloadFromBanner}>Reload r{banner.failure.serverRevision}</button><button className="btn sm" data-act="history" onClick={() => setDrawer(true)}>Compare</button></div>
                ) : null}
                {banner?.kind === 'rejected' ? (
                  <div className="banner warn" data-testid="banner-rejected"><b>Edit rejected.</b><span className="grow">The board check refused the assistant's edit. The board is unchanged at r{banner.revision}.</span></div>
                ) : null}
                {empty ? (
                  <div className="tile empty" style={{ gridColumn: '1/-1', minHeight: 420 }} data-testid="empty-board"><div>
                    <svg width="96" height="72" viewBox="0 0 96 72" fill="none"><rect x="2" y="2" width="28" height="20" rx="5" stroke="var(--line)" strokeWidth="2" /><rect x="34" y="2" width="28" height="20" rx="5" stroke="var(--line)" strokeWidth="2" /><rect x="66" y="2" width="28" height="20" rx="5" stroke="var(--line)" strokeWidth="2" /><rect x="2" y="28" width="92" height="42" rx="6" stroke="var(--accent)" strokeWidth="2" strokeDasharray="5 4" /></svg>
                    <h2>This board is empty</h2>
                    <p>Describe what you want to see and the assistant builds it from your data. Every change is a revision you can undo.</p>
                    <button className="btn primary" data-send="0" disabled={busy} onClick={() => onChip('Monthly revenue with KPIs')}>Try: monthly revenue with KPIs</button>
                  </div></div>
                ) : (
                  <Tiles document={live.document} adapters={adaptersRef.current} answers={answers} changed={changed} revision={live.revision} loadIds={loadIds} selected={selected} onSelect={id => !busy && setSelected(current => (current.includes(id) ? [] : [id]))} />
                )}
                {drawer ? <History revisions={revisions} current={live.revision} published={published} onClose={() => setDrawer(false)} onPublish={setModal} onRestore={doRestore} onCompare={doCompare} compare={compare} /> : null}
              </div>
              <div className="revs">
                <span className="note">Revisions</span>
                {revisions.length === 0 ? <span className="note">none yet</span> : null}
                {revisions.map(row => { const n = revisionNumber(row); return <button key={n} className={`rv ${n === live.revision ? 'cur' : ''} ${n === published ? 'pub' : ''}`} data-act="history" data-rev={n} data-tip={`r${n}${n === published ? ' · published' : ''}${n === live.revision ? ' · current draft' : ''}`} onClick={() => setDrawer(true)}>r{n}</button>; })}
                <span className="grow" />
                <span className="note" data-testid="readers-note">{published && live.revision > published ? `Readers see r${published} · you are editing r${live.revision}` : live.revision ? (published ? `Readers see r${published}` : 'Not published yet') : ''}</span>
                <button className="btn sm" data-act="history" onClick={() => setDrawer(true)}>History</button>
              </div>
            </div>
          )}
          {reader ? null : <Chat thread={thread} revision={live.revision} busy={busy} onSend={() => send(text)} onChip={onChip} text={text} setText={setText} pointing={selected.filter(id => live.document.parts?.[id]).map(id => ({ id, title: live.document.parts[id].title }))} onClearPointing={() => setSelected([])} />}
        </div>
      </div>
      {modal ? (
        <div className="modal" data-act="closemodal" onClick={() => setModal(null)}>
          <div className="card" onClick={event => event.stopPropagation()} data-testid="publish-modal">
            <h3 style={{ margin: '0 0 6px' }}>Publish r{modal}?</h3>
            <p style={{ color: 'var(--muted)', margin: '0 0 14px' }}>Readers will see <b>r{modal}</b>{published ? <> instead of r{published}. r{published} stays in the history — publishing it again is the rollback</> : null}. Nothing else changes.</p>
            <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}><button className="btn" data-act="closemodal" onClick={() => setModal(null)}>Cancel</button><button className="btn primary" data-act="confirmpub" onClick={doPublish}>Publish r{modal}</button></div>
          </div>
        </div>
      ) : null}
    </div>
  );
}
