import { useEffect, useState } from 'react';
import '@iyulab/u-board/styles.css';
import { BoardEditor } from './board-editor.jsx';
import { BoardList } from './board-list.jsx';
import { setDataRoot } from './odata.js';

/**
 * The screen: a list of the boards the person may open, or — when the server wrote a board's id into the page — that board's editor.
 * What the person may do, and what the host has switched on, is asked once, first.
 */
export function App({ config, endpoints, fetcher, api }) {
  const [access, setAccess] = useState(null);
  useEffect(() => {
    let live = true;
    api.access().then(answer => {
      if (!live) return;
      setDataRoot(answer.dataRoot);
      setAccess(answer);
    });
    return () => { live = false; };
  }, [api]);

  if (!access) return <p className="loading" role="status">loading…</p>;
  return config.board
    ? <BoardEditor id={config.board} access={access} api={api} endpoints={endpoints} fetcher={fetcher} />
    : <BoardList access={access} api={api} endpoints={endpoints} />;
}
