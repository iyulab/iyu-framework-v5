import { useEffect, useRef, useState } from 'react';
import { boardPage, newBoardId } from './host-api.js';

/** The boards the person may open, each with what they may do to it; a button for a new one only when the host lets them make one. */
export function BoardList({ access, api, endpoints }) {
  const [boards, setBoards] = useState(null);
  const [dialog, setDialog] = useState(false);
  useEffect(() => {
    let live = true;
    api.list().then(rows => live && setBoards(rows));
    return () => { live = false; };
  }, [api]);

  const create = access.create;
  const open = id => { location.assign(boardPage(endpoints, id)); };
  const start = name => {
    const trimmed = name.trim();
    if (trimmed) location.assign(boardPage(endpoints, newBoardId(trimmed), trimmed));
  };

  return (
    <div className="listpage" data-testid="board-list">
      <header className="top">
        <div className="logo">B</div>
        <span className="title">Boards</span>
        <span className="grow" />
        {create ? <button className="btn primary" data-act="newboard" onClick={() => setDialog(true)}>＋ New board</button> : null}
      </header>
      <div className="list">
        <div className="sub" data-testid="list-sub">
          {create
            ? access.assistant ? 'Describe what you want to see and the assistant builds it from your data.' : 'Open a board to read it, or make a new one.'
            : 'You can open and ask about boards here. Creating boards is not enabled for your account.'}
        </div>
        {boards === null ? <p role="status">loading…</p> : boards.length === 0 ? (
          <div className="tile empty" style={{ minHeight: 300 }} data-testid="no-boards">
            <div>
              <h2 style={{ fontSize: 17 }}>No boards yet</h2>
              <p style={{ margin: '0 0 12px', color: 'var(--muted)' }}>{create ? 'Make the first one.' : 'Nothing has been shared with you yet.'}</p>
              {create ? <button className="btn primary" data-act="newboard" onClick={() => setDialog(true)}>＋ New board</button> : null}
            </div>
          </div>
        ) : (
          <table className="btbl">
            <thead><tr><th>Name</th><th>Latest</th><th>Published</th><th>Your access</th></tr></thead>
            <tbody>
              {boards.map(board => {
                const can = board.can ?? {};
                return (
                  <tr key={board.id} data-board={board.id} tabIndex={0} onClick={() => open(board.id)} onKeyDown={event => { if (event.key === 'Enter') open(board.id); }}>
                    <td><a href={boardPage(endpoints, board.id)} onClick={event => event.stopPropagation()}><b>{board.name}</b></a></td>
                    <td>{board.revision ? `r${board.revision}` : <span style={{ color: 'var(--faint)' }}>empty</span>}</td>
                    <td>{board.publishedRevision ? `r${board.publishedRevision}` : <span style={{ color: 'var(--faint)' }}>not published</span>}</td>
                    <td><span className={`acc ${can.edit ? 'edit' : 'read'}`}>{can.edit ? '● Can edit' : '🔒 Read only'}</span></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
        <div className="hint">Who may <b>make</b> and who may <b>edit</b> each board is decided by your application — this screen only shows the answer. A board you can't edit can still be opened and asked about.</div>
      </div>
      {dialog ? <NewBoard onCancel={() => setDialog(false)} onCreate={start} /> : null}
    </div>
  );
}

function NewBoard({ onCancel, onCreate }) {
  const [name, setName] = useState('');
  const input = useRef(null);
  useEffect(() => { input.current?.focus(); }, []);
  const submit = event => { event.preventDefault(); onCreate(name); };
  return (
    <div className="modal" data-act="closedialog" onClick={onCancel}>
      <form className="card" onClick={event => event.stopPropagation()} onSubmit={submit} data-testid="new-board-dialog" role="dialog" aria-label="New board">
        <h3 style={{ margin: '0 0 10px' }}>New board</h3>
        <label className="field">Name<input ref={input} value={name} maxLength={120} onChange={event => setName(event.target.value)} aria-label="Board name" /></label>
        <div className="note" style={{ marginBottom: 14 }}>Who can edit this board is decided by your application. For this one: <b>you</b>, as its creator, unless your application says otherwise.</div>
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <button type="button" className="btn" data-act="closedialog" onClick={onCancel}>Cancel</button>
          <button type="submit" className="btn primary" data-act="createboard" disabled={!name.trim()}>Create and start</button>
        </div>
      </form>
    </div>
  );
}
