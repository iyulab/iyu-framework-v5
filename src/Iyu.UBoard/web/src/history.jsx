import { useEffect, useState } from 'react';
import { checkBoard } from '@iyulab/u-board/check';
import { revisionAuthor, revisionNumber, revisionTime } from './host-api.js';
import { Tiles } from './tiles.jsx';

const iso = value => (value ? new Date(value).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '');

/**
 * The revisions of the board, newest first. What a person may do with one follows `can`: publishing (and so rolling back, which is publishing
 * an older revision) needs the right to publish; restoring one as the new draft needs the right to edit. Compare is reading.
 */
export function History({ revisions, current, published, can, onClose, onPublish, onRestore, onCompare, compare }) {
  return (
    <div className="drawer" data-testid="drawer">
      <div style={{ display: 'flex', alignItems: 'center', marginBottom: 12 }}><b style={{ fontSize: 15 }}>History</b><span className="grow" /><button className="btn sm" data-act="closehist" onClick={onClose}>Close</button></div>
      <p style={{ color: 'var(--muted)', margin: '0 0 12px', fontSize: 13 }}>
        Each save is a revision that never changes. {can.publish ? <><b>Publishing</b> points readers at one; publishing an older revision is the rollback.</> : 'Readers see the one that is published.'}
      </p>
      {[...revisions].reverse().map(row => {
        const n = revisionNumber(row);
        return (
          <div className="dr" key={n} data-rev={n}>
            <div className="h">r{n}{n === current ? <span className="pill draft">draft</span> : null}{n === published ? <span className="pill pub">published</span> : null}<span className="grow" /><span style={{ color: 'var(--faint)', fontWeight: 400, fontSize: 12 }}>{iso(revisionTime(row))}</span></div>
            <div className="s">Saved by {revisionAuthor(row) ?? 'unknown'}</div>
            {compare?.revision === n ? <div className="cmp" data-testid="compare">{compare.text}</div> : null}
            <div className="a">
              <button className="btn sm" data-act="compare" onClick={() => onCompare(n)}>Compare</button>
              {can.publish && n !== published ? <button className="btn sm" data-act="publish" data-rev={n} onClick={() => onPublish(n)}>Publish r{n}</button> : null}
              {can.edit && n !== current ? <button className="btn sm" data-act="restore" onClick={() => onRestore(n)}>Restore as draft</button> : null}
            </div>
          </div>
        );
      })}
    </div>
  );
}

/** What readers see: the published revision, or the newest earlier publication this viewer accepts, with a word that it is not the latest. */
export function Reader({ id, api, tick, adapters, answers }) {
  const [shown, setShown] = useState({ state: 'loading' });
  useEffect(() => {
    let live = true;
    (async () => {
      const published = await api.published(id);
      if (!published) return live && setShown({ state: 'unpublished' });
      const issues = checkBoard(published.document).issues;
      if (issues.length === 0) return live && setShown({ state: 'published', revision: published.revision, document: published.document });
      for (const candidate of await api.fallback(id))
        if (checkBoard(candidate.document).issues.length === 0)
          return live && setShown({ state: 'fell-back', revision: candidate.revision, refused: published.revision, document: candidate.document });
      return live && setShown({ state: 'nothing-usable', refused: published.revision });
    })();
    return () => { live = false; };
  }, [api, id, tick]);
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
