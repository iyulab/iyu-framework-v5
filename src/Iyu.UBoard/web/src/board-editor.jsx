import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { checkBoard } from '@iyulab/u-board/check';
import { changedParts, createAssistant, newBoard } from './agent-loop.js';
import { Chat, STARTERS, chipsAfter } from './chat.jsx';
import { History, Reader } from './history.jsx';
import { revisionNumber } from './host-api.js';
import { makeODataAdapter } from './odata-adapter.js';
import { Tiles } from './tiles.jsx';

const hello = assistant => ({
  role: 'hello',
  text: assistant ? 'Hi — I can build a board from your data, change it as you go, or just answer a question about it.' : '',
  chips: STARTERS,
});

/**
 * One board: the viewer, the assistant's panel (only when the host has one), the revision history, publishing and rolling back. What is
 * offered follows what the host says the person may do (`can`): the host enforces every one of them, so a button left out here is a
 * courtesy and one left in would be refused there.
 */
export function BoardEditor({ id, access, api, endpoints, fetcher }) {
  const [boot, setBoot] = useState(false);
  const [missing, setMissing] = useState(false);
  const [name, setName] = useState(id);
  const [can, setCan] = useState({ edit: false, publish: false, delete: false });
  const [live, setLive] = useState({ revision: 0, document: newBoard(id).document });
  const liveRef = useRef(live);
  liveRef.current = live;
  const [changed, setChanged] = useState({});
  const [loadIds, setLoadIds] = useState([]);
  const [answers, setAnswers] = useState({});
  const [thread, setThread] = useState([hello(access.assistant)]);
  const [busy, setBusy] = useState(false);
  const [selected, setSelected] = useState([]);
  const [view, setView] = useState('editor');
  const [drawer, setDrawer] = useState(false);
  const [modal, setModal] = useState(null);
  const [banner, setBanner] = useState(null);
  const [revisions, setRevisions] = useState([]);
  const [published, setPublished] = useState(null);
  const [tick, setTick] = useState(0);
  const [compare, setCompare] = useState(null);
  const assistant = useRef(null);
  const stopping = useRef(null);
  const adaptersRef = useRef(null);
  if (!adaptersRef.current) adaptersRef.current = [makeODataAdapter((path, rows, count) => setAnswers(prev => ({ ...prev, [path]: { rows, count } })), fetcher)];

  const refresh = useCallback(async () => {
    setRevisions(await api.revisions(id));
    const pub = await api.published(id);
    setPublished(pub ? pub.revision : null);
    const envelope = await api.board(id);
    if (envelope?.can) setCan(envelope.can);
    setTick(value => value + 1);
  }, [api, id]);

  const emit = useCallback(event => {
    if (event.type === 'turn-start') {
      setBusy(true);
      setBanner(null);
      const on = (event.selection ?? []).map(part => ({ id: part, title: liveRef.current?.document?.parts?.[part]?.title }));
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
    else if (event.type === 'board') {
      setLive({ revision: event.revision, document: event.document });
      setChanged(event.changed);
    } else if (event.type === 'failure') {
      if (event.failure.kind === 'rejected') setBanner({ kind: 'rejected', revision: event.failure.revision });
      if (event.failure.kind === 'conflict') setBanner({ kind: 'conflict', failure: event.failure });
    }
  }, []);

  // The assistant's board store takes only a board the viewer's check accepts, so a draft it refuses gets no assistant (the page says so, and a revision
  // restored from the history makes one).
  const startAssistant = useCallback((loaded, rights) => {
    assistant.current = null;
    if (!access.assistant || checkBoard(loaded.document).issues.length > 0) return;
    assistant.current = createAssistant({ fetch: fetcher, endpoints, board: { id, loaded }, emit, canEdit: rights.edit, catalog: access.catalog });
  }, [access, endpoints, fetcher, emit, id]);

  useEffect(() => {
    let alive = true;
    (async () => {
      const envelope = await api.board(id);
      if (!alive) return;
      // A board is made before it is opened (the list's dialog makes it), so one the host does not hold is not here, or not the person's.
      if (!envelope) return setMissing(true);
      const loaded = { name: envelope.name, revision: envelope.revision, document: envelope.document };
      const rights = envelope.can ?? { edit: false, publish: false, delete: false };
      setName(envelope.name);
      setCan(rights);
      setLive({ revision: loaded.revision, document: loaded.document });
      startAssistant(loaded, rights);
      await refresh();
      if (alive) setBoot(true);
    })();
    return () => { alive = false; };
  }, [id, access, api, refresh, startAssistant]);

  const send = useCallback(async message => {
    if (!message.trim()) return;
    if (!assistant.current) {
      setThread(t => [...t.map(m => ({ ...m, chips: [] })), { role: 'u', text: message }, { role: 'a', steps: [], text: 'This draft does not pass the board check, so I cannot work on it. Open the history and go back to an earlier revision.', chips: [] }]);
      return;
    }
    const selection = selected;
    setSelected([]);
    stopping.current = new AbortController();
    const result = await assistant.current.send(message, { selection, signal: stopping.current.signal });
    setLoadIds([]);
    const unchanged = result.revision.to === result.revision.from;
    setThread(t => {
      const next = [...t];
      const at = next.length - 1;
      next[at] = { ...next[at], text: result.reply, chips: chipsAfter(result), outcome: result.outcome, failure: result.failure, answer: result.outcome === 'done' && unchanged };
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
    await send(chip);
  }, [banner, send]);

  const reloadFromBanner = async () => {
    const failure = banner?.failure;
    if (!failure) return;
    await onChip(`Reload r${failure.serverRevision} and retry`);
  };

  // What a publish came to: said above the board (there may be no chat), and in the chat when there is one.
  const say = text => {
    setBanner({ kind: 'notice', text });
    if (access.assistant) setThread(t => [...t, { role: 'a', steps: [], text, chips: [] }]);
  };

  const doPublish = async () => {
    const n = modal;
    setModal(null);
    setDrawer(false);
    const previous = published;
    const { status } = await api.publish(id, n);
    say(status === 403 ? `Publishing r${n} is not yours to do here. Nothing changed.` : `Published r${n}. Readers see it now${previous ? `; r${previous} is one click away if you need to roll back` : ''}.`);
    await refresh();
  };

  const doCompare = async n => {
    const [now, before] = await Promise.all([api.revision(id, n), n > 1 ? api.revision(id, n - 1) : null]);
    const marks = changedParts(before?.document, now.document);
    const words = Object.entries(marks).map(([part, mark]) => `${part} (${mark})`);
    setCompare({ revision: n, text: words.length ? `Compared with r${n - 1}: ${words.join(', ')}` : n > 1 ? `Same parts as r${n - 1}.` : 'The first revision.' });
  };

  const doRestore = async n => {
    const old = await api.revision(id, n);
    const saved = await api.save(id, old.document, live.revision);
    if (saved.status === 403 || saved.status === 409) return;
    if (assistant.current) assistant.current.reload(saved.board);
    else startAssistant({ name: saved.board.name, revision: saved.board.revision, document: saved.board.document }, can);
    setLive({ revision: saved.board.revision, document: saved.board.document });
    setChanged({});
    setDrawer(false);
    await refresh();
  };

  // A draft the viewer's check refuses (saved by an import or another client) is not drawn: the viewer would stop on it. History still opens, so an earlier revision can be restored.
  const draftIssues = useMemo(() => checkBoard(live.document).issues, [live.document]);
  const reader = view === 'reader';
  const empty = Object.keys(live.document.parts ?? {}).length === 0;
  const chat = access.assistant;
  if (missing) return <div className="empty" data-testid="missing"><h2>This board is not here</h2><p>It does not exist, or it is not one you may open.</p><a className="btn" href={endpoints.screen}>Back to the boards</a></div>;
  if (!boot) return <p className="loading" role="status">loading…</p>;

  return (
    <div className={`${reader ? 'reader' : ''} ${chat ? '' : 'nochat'}`}>
      <div className="app">
        <header className="top">
          <div className="logo">B</div>
          <a className="crumb hide-s" href={endpoints.screen} data-act="tolist">Boards /</a>
          <span className="title" data-testid="board-title">{name}</span>
          {!can.edit && !reader ? <span className="pill" data-testid="pill-readonly" data-tip="Editing this board is up to its owner — your application decides">🔒 Read only</span> : null}
          {reader ? (
            <span className="pill pub" data-testid="pill-published">Published r{published}</span>
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
              <button className="btn hide-s" data-act="history" onClick={() => setDrawer(true)}>History</button>
              {can.publish || can.edit ? <button className="btn hide-s" data-act="reader" onClick={() => { setView('reader'); setDrawer(false); }}>Preview as reader</button> : null}
              {can.publish ? <button className="btn primary" data-act="publish" data-rev={live.revision} disabled={!live.revision} onClick={() => setModal(live.revision)}>Publish…</button> : null}
            </>
          )}
        </header>
        <div className="main">
          {reader ? (
            <div className="canvas"><Reader id={id} api={api} tick={tick} adapters={adaptersRef.current} answers={answers} /></div>
          ) : (
            <div className="canvas">
              <div className="cbody">
                {banner?.kind === 'conflict' ? (
                  <div className="banner bad" data-testid="banner-conflict"><b>Not saved.</b><span className="grow">Another session saved r{banner.failure.serverRevision} while you were on r{banner.failure.workedFrom}. Your change was discarded, nothing was overwritten.</span><button className="btn sm" data-act="reload" onClick={reloadFromBanner}>Reload r{banner.failure.serverRevision}</button><button className="btn sm" data-act="history" onClick={() => setDrawer(true)}>Compare</button></div>
                ) : null}
                {draftIssues.length > 0 ? (
                  <div className="banner bad" data-testid="banner-invalid"><b>This draft does not pass the board check.</b><span className="grow">{draftIssues[0].path ? `${draftIssues[0].path} — ` : ''}{draftIssues[0].message}. It is not drawn; open the history to go back to an earlier revision{can.edit && chat ? ', or ask the assistant to fix it' : ''}.</span></div>
                ) : null}
                {banner?.kind === 'notice' ? (
                  <div className="banner info" data-testid="banner-notice"><span className="grow">{banner.text}</span><button className="btn sm" data-act="dismiss" onClick={() => setBanner(null)}>Dismiss</button></div>
                ) : null}
                {banner?.kind === 'rejected' ? (
                  <div className="banner warn" data-testid="banner-rejected"><b>Edit rejected.</b><span className="grow">The board check refused the assistant's edit. The board is unchanged at r{banner.revision}.</span></div>
                ) : null}
                {draftIssues.length > 0 ? null : empty ? (
                  <div className="tile empty" style={{ gridColumn: '1/-1', minHeight: 420 }} data-testid="empty-board"><div>
                    <svg width="96" height="72" viewBox="0 0 96 72" fill="none"><rect x="2" y="2" width="28" height="20" rx="5" stroke="var(--line)" strokeWidth="2" /><rect x="34" y="2" width="28" height="20" rx="5" stroke="var(--line)" strokeWidth="2" /><rect x="66" y="2" width="28" height="20" rx="5" stroke="var(--line)" strokeWidth="2" /><rect x="2" y="28" width="92" height="42" rx="6" stroke="var(--accent)" strokeWidth="2" strokeDasharray="5 4" /></svg>
                    <h2>This board is empty</h2>
                    <p>{chat && can.edit ? 'Describe what you want to see and the assistant builds it from your data. Every change is a revision you can undo.' : chat ? 'Nothing has been put on it yet. You can ask the assistant about it.' : 'Nothing has been put on it yet.'}</p>
                  </div></div>
                ) : (
                  <Tiles document={live.document} adapters={adaptersRef.current} answers={answers} changed={changed} revision={live.revision} loadIds={loadIds} selected={selected} onSelect={chat ? part => !busy && setSelected(current => (current.includes(part) ? [] : [part])) : undefined} />
                )}
                {drawer ? <History revisions={revisions} current={live.revision} published={published} can={can} onClose={() => setDrawer(false)} onPublish={setModal} onRestore={doRestore} onCompare={doCompare} compare={compare} /> : null}
              </div>
              <div className="revs">
                <span className="note">Revisions</span>
                {revisions.length === 0 ? <span className="note">none yet</span> : null}
                {revisions.map(row => { const n = revisionNumber(row); return <button key={n} className={`rv ${n === live.revision ? 'cur' : ''} ${n === published ? 'pub' : ''}`} data-act="history" data-rev={n} data-tip={`r${n}${n === published ? ' · published' : ''}${n === live.revision ? ' · current draft' : ''}`} onClick={() => setDrawer(true)}>r{n}</button>; })}
                <span className="grow" />
                <span className="note" data-testid="readers-note">{published && live.revision > published ? `Readers see r${published} · you are ${can.edit ? 'editing' : 'reading'} r${live.revision}` : live.revision ? (published ? `Readers see r${published}` : 'Not published yet') : ''}</span>
                <button className="btn sm" data-act="history" onClick={() => setDrawer(true)}>History</button>
              </div>
            </div>
          )}
          {reader || !chat ? null : <Chat thread={thread} revision={live.revision} busy={busy} canEdit={can.edit} onSend={send} onStop={() => stopping.current?.abort()} onChip={onChip} pointing={selected.filter(part => live.document.parts?.[part]).map(part => ({ id: part, title: live.document.parts[part].title }))} onClearPointing={() => setSelected([])} />}
        </div>
      </div>
      {modal && can.publish ? (
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
