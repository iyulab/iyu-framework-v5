import { useEffect, useRef, useState } from 'react';
import { emptyBoard } from '@iyulab/u-board/check';
import { boardPage, newBoardId } from './host-api.js';
import { addPage, readUntilSome } from './list-paging.js';

/** How many boards the list asks the host for at a time. */
export const LIST_PAGE = 50;

const stop = event => event.stopPropagation();

/** The boards the person may open, each with what they may do to it; a button for a new one only when the host lets them make one, and for a board a menu with what the host lets them do to it. */
export function BoardList({ access, api, endpoints }) {
  const [boards, setBoards] = useState(null);
  const [next, setNext] = useState(null); // where the host says the next page starts; null when it has nothing more to look at. A short page is not the end: only this says so.
  const [loadingMore, setLoadingMore] = useState(false);
  const [dialog, setDialog] = useState(null); // { kind: 'new' } | { kind: 'rename' | 'delete', board }
  const more = next !== null;
  useEffect(() => {
    let live = true;
    readUntilSome(api, { skip: 0, take: LIST_PAGE }).then(page => {
      if (!live) return;
      setBoards(page.items);
      setNext(page.next);
    });
    return () => { live = false; };
  }, [api]);

  const showMore = async () => {
    setLoadingMore(true);
    const page = await readUntilSome(api, { skip: next, take: LIST_PAGE });
    setBoards(current => addPage(current, page.items));
    setNext(page.next);
    setLoadingMore(false);
  };

  const create = access.create;
  const open = id => { location.assign(boardPage(endpoints, id)); };
  // A board is made when the dialog is confirmed — an empty one, which the host's rules then give to the person who made it — and then opened.
  // An id someone else took is tried again with another tail; a refusal is said in the dialog.
  const start = async name => {
    const trimmed = name.trim();
    if (!trimmed) return 'A board needs a name.';
    for (let attempt = 0; attempt < 3; attempt++) {
      const id = newBoardId(trimmed);
      const { status } = await api.create(id, trimmed, emptyBoard('grid'));
      if (status === 201) return location.assign(boardPage(endpoints, id));
      if (status === 403) return 'Your account may not make boards.';
      if (status !== 409) return `The board could not be made (${status}).`;
    }
    return 'The board could not be made: its name keeps meeting another board. Try another name.';
  };

  // The host decides; what comes back is what the list then shows. A board that is gone is gone from the list too, whatever was asked.
  const rename = async (board, name) => {
    const trimmed = name.trim();
    if (!trimmed) return 'A board needs a name.';
    const { status, board: renamed } = await api.rename(board.id, trimmed);
    if (status === 200) {
      setBoards(current => current.map(row => (row.id === board.id ? { ...row, name: renamed?.name ?? trimmed } : row)));
      return setDialog(null);
    }
    if (status === 403) return 'Your account may not rename this board.';
    if (status === 404) { setBoards(current => current.filter(row => row.id !== board.id)); return 'This board is gone.'; }
    return `The board could not be renamed (${status}).`;
  };
  const remove = async board => {
    const { status } = await api.remove(board.id);
    if (status === 204 || status === 404) {
      setBoards(current => current.filter(row => row.id !== board.id));
      return setDialog(null);
    }
    if (status === 403) return 'Your account may not delete this board.';
    return `The board could not be deleted (${status}).`;
  };

  return (
    <div className="listpage" data-testid="board-list">
      <header className="top">
        <div className="logo">B</div>
        <span className="title">Boards</span>
        <span className="grow" />
        {create ? <button className="btn primary" data-act="newboard" onClick={() => setDialog({ kind: 'new' })}>＋ New board</button> : null}
      </header>
      <div className="list">
        <div className="sub" data-testid="list-sub">
          {create
            ? access.assistant ? 'Describe what you want to see and the assistant builds it from your data.' : 'Open a board to read it, or make a new one.'
            : 'You can open and ask about boards here. Creating boards is not enabled for your account.'}
        </div>
        {boards === null ? <p role="status">loading…</p> : boards.length === 0 && more ? null : boards.length === 0 ? (
          <div className="tile empty" style={{ minHeight: 300 }} data-testid="no-boards">
            <div>
              <h2 style={{ fontSize: 17 }}>No boards yet</h2>
              <p style={{ margin: '0 0 12px', color: 'var(--muted)' }}>{create ? (access.assistant ? 'Start with a sentence — “monthly revenue with KPIs”.' : 'Make the first one.') : 'Nothing has been shared with you yet.'}</p>
              {create ? <button className="btn primary" data-act="newboard" onClick={() => setDialog({ kind: 'new' })}>＋ New board</button> : null}
            </div>
          </div>
        ) : (
          <table className="btbl">
            <thead><tr><th>Name</th><th>Created by</th><th>Updated</th><th>Latest</th><th>Published</th><th>Your access</th><th aria-label="Actions" /></tr></thead>
            <tbody>
              {boards.map(board => {
                const can = board.can ?? {};
                return (
                  <tr key={board.id} data-board={board.id} tabIndex={0} onClick={() => open(board.id)} onKeyDown={event => { if (event.key === 'Enter' && event.target === event.currentTarget) open(board.id); }}>
                    <td><a href={boardPage(endpoints, board.id)} onClick={stop}><b>{board.name}</b></a></td>
                    <td data-col="created-by">{board.createdBy ?? <span style={{ color: 'var(--faint)' }}>—</span>}</td>
                    <td data-col="updated">{board.updatedAt ? <time dateTime={board.updatedAt} title={board.updatedBy ? `by ${board.updatedBy}` : undefined}>{new Date(board.updatedAt).toLocaleString()}</time> : <span style={{ color: 'var(--faint)' }}>—</span>}</td>
                    <td>{board.revision ? `r${board.revision}` : <span style={{ color: 'var(--faint)' }}>empty</span>}</td>
                    <td>{board.publishedRevision ? `r${board.publishedRevision}` : <span style={{ color: 'var(--faint)' }}>not published</span>}</td>
                    <td><span className={`acc ${can.edit ? 'edit' : 'read'}`}>{can.edit ? '● Can edit' : '🔒 Read only'}</span></td>
                    <td className="rowact" onClick={stop}>
                      {can.edit || can.delete ? <RowMenu board={board} can={can} onRename={() => setDialog({ kind: 'rename', board })} onDelete={() => setDialog({ kind: 'delete', board })} /> : null}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
        {more ? (
          <div className="more" data-testid="list-more">
            <span>{boards.length === 0 ? 'None of the boards looked at so far is one you may open.' : `Showing the first ${boards.length} boards.`}</span>
            <button className="btn sm" data-act="moreboards" disabled={loadingMore} onClick={showMore}>{loadingMore ? 'Loading…' : 'Show more'}</button>
          </div>
        ) : null}
        <div className="hint">Who may <b>make</b> and who may <b>edit</b> each board is decided by your application — this screen only shows the answer. A board you can't edit can still be opened and asked about.</div>
      </div>
      {dialog?.kind === 'new' ? <NewBoard onCancel={() => setDialog(null)} onCreate={start} /> : null}
      {dialog?.kind === 'rename' ? <RenameBoard board={dialog.board} onCancel={() => setDialog(null)} onRename={rename} /> : null}
      {dialog?.kind === 'delete' ? <DeleteBoard board={dialog.board} onCancel={() => setDialog(null)} onDelete={remove} /> : null}
    </div>
  );
}

/** What the person may do to one board, as a menu: only the actions the host said they may. The host refuses the rest whatever this shows. */
function RowMenu({ board, can, onRename, onDelete }) {
  const [open, setOpen] = useState(false);
  const root = useRef(null);
  useEffect(() => {
    if (!open) return undefined;
    const away = event => { if (!root.current?.contains(event.target)) setOpen(false); };
    const escape = event => { if (event.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', away);
    document.addEventListener('keydown', escape);
    return () => { document.removeEventListener('mousedown', away); document.removeEventListener('keydown', escape); };
  }, [open]);
  const choose = action => () => { setOpen(false); action(); };
  return (
    <div className="rowmenu" ref={root}>
      <button className="btn sm" data-act="rowmenu" aria-haspopup="menu" aria-expanded={open} aria-label={`Actions for ${board.name}`} onClick={() => setOpen(value => !value)}>⋯</button>
      {open ? (
        <div className="menu" role="menu" data-testid="row-menu">
          {can.edit ? <button role="menuitem" data-act="rename" onClick={choose(onRename)}>Rename…</button> : null}
          {can.delete ? <button role="menuitem" data-act="delete" className="danger" onClick={choose(onDelete)}>Delete…</button> : null}
        </div>
      ) : null}
    </div>
  );
}

function NewBoard({ onCancel, onCreate }) {
  const [name, setName] = useState('');
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState('');
  const input = useRef(null);
  useEffect(() => { input.current?.focus(); }, []);
  const submit = async event => {
    event.preventDefault();
    setWorking(true);
    setProblem((await onCreate(name)) ?? '');
    setWorking(false);
  };
  return (
    <div className="modal" data-act="closedialog" onClick={onCancel}>
      <form className="card" onClick={event => event.stopPropagation()} onSubmit={submit} data-testid="new-board-dialog" role="dialog" aria-label="New board">
        <h3 style={{ margin: '0 0 10px' }}>New board</h3>
        <label className="field">Name<input ref={input} value={name} maxLength={120} onChange={event => setName(event.target.value)} aria-label="Board name" /></label>
        <div className="note" style={{ marginBottom: 14 }}>Who can edit this board is decided by your application. For this one: <b>you</b>, as its creator, unless your application says otherwise.</div>
        {problem ? <div className="banner bad" role="alert" data-testid="new-board-problem">{problem}</div> : null}
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <button type="button" className="btn" data-act="closedialog" onClick={onCancel}>Cancel</button>
          <button type="submit" className="btn primary" data-act="createboard" disabled={!name.trim() || working}>Create and start</button>
        </div>
      </form>
    </div>
  );
}

function RenameBoard({ board, onCancel, onRename }) {
  const [name, setName] = useState(board.name);
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState('');
  const input = useRef(null);
  useEffect(() => { input.current?.select(); }, []);
  const submit = async event => {
    event.preventDefault();
    setWorking(true);
    setProblem((await onRename(board, name)) ?? '');
    setWorking(false);
  };
  return (
    <div className="modal" data-act="closedialog" onClick={onCancel}>
      <form className="card" onClick={event => event.stopPropagation()} onSubmit={submit} data-testid="rename-board-dialog" role="dialog" aria-label="Rename board">
        <h3 style={{ margin: '0 0 10px' }}>Rename board</h3>
        <label className="field">Name<input ref={input} value={name} maxLength={120} onChange={event => setName(event.target.value)} aria-label="Board name" /></label>
        <div className="note" style={{ marginBottom: 14 }}>Only the name changes: its revisions, what is published and who may open it stay as they are.</div>
        {problem ? <div className="banner bad" role="alert" data-testid="rename-board-problem">{problem}</div> : null}
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <button type="button" className="btn" data-act="closedialog" onClick={onCancel}>Cancel</button>
          <button type="submit" className="btn primary" data-act="renameboard" disabled={!name.trim() || name.trim() === board.name || working}>Rename</button>
        </div>
      </form>
    </div>
  );
}

function DeleteBoard({ board, onCancel, onDelete }) {
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState('');
  const submit = async event => {
    event.preventDefault();
    setWorking(true);
    setProblem((await onDelete(board)) ?? '');
    setWorking(false);
  };
  return (
    <div className="modal" data-act="closedialog" onClick={onCancel}>
      <form className="card" onClick={event => event.stopPropagation()} onSubmit={submit} data-testid="delete-board-dialog" role="alertdialog" aria-label="Delete board">
        <h3 style={{ margin: '0 0 10px' }}>Delete “{board.name}”?</h3>
        <p style={{ margin: '0 0 14px', color: 'var(--muted)' }}>
          The board goes with all {board.revision ? `${board.revision} of its revisions` : 'of its history'} and the record of what was published{board.publishedRevision ? <>; readers who see r{board.publishedRevision} lose it too</> : null}. This cannot be undone.
        </p>
        {problem ? <div className="banner bad" role="alert" data-testid="delete-board-problem">{problem}</div> : null}
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <button type="button" className="btn" data-act="closedialog" onClick={onCancel} autoFocus>Cancel</button>
          <button type="submit" className="btn danger" data-act="deleteboard" disabled={working}>Delete board</button>
        </div>
      </form>
    </div>
  );
}
