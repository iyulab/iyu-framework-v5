// One person's editing session on one board: the store the board tools work on, and the host that keeps what they save.
//
// The store holds the board in memory and calls `save(document)` with the document only. The host wants the revision a save
// worked from, so this session carries it. When the host says the board has moved on (409) the save rejects and the store puts
// its board back as it was — but the tools can only tell the model that as a string, so the session keeps the structured answer
// (the host's board) for the loop to read.
import { boardTools, DocumentBoardStore, functionTool } from '@iyulab/u-board-mcp/tools';
import { emptyBoard } from '@iyulab/u-board/check';

/** Thrown from `save` when the host says the board has moved on since this session loaded it. */
export class BoardMovedOn extends Error {
  constructor(current) {
    super(`the host's board is at revision ${current}; this session worked from an earlier one — reload it`);
    this.current = current;
  }
}

/** A board that does not exist on the host yet: the first save makes it, as revision 1. */
export function newBoard(name) {
  return { name, revision: 0, document: emptyBoard('grid') };
}

/**
 * `loaded` is the host's board (`{ name, revision, document }`) or `newBoard(name)`. `host(url, init)` is the page's fetch. `boards`
 * is where the host keeps boards (`/uboard/boards`).
 */
export function openBoardSession({ id, loaded, host, boards }) {
  const known = { revision: loaded.revision };
  const session = {
    id,
    known,
    conflict: undefined, // { serverRevision, board } once a save was refused because the board moved on
    saveFailure: undefined, // { status } once the host refused a save for another reason
    lastSave: undefined, // { method, url, status } — the request that kept (or refused) the last save
    takeConflict() {
      const conflict = session.conflict;
      session.conflict = undefined;
      return conflict;
    },
    takeSaveFailure() {
      const failure = session.saveFailure;
      session.saveFailure = undefined;
      return failure;
    },
  };

  const store = new DocumentBoardStore(structuredClone(loaded.document), {
    name: loaded.name,
    revision: loaded.revision,
    save: async document => {
      const creating = known.revision === 0;
      const method = creating ? 'POST' : 'PUT';
      const at = `${boards}/${encodeURIComponent(id)}`;
      const url = creating ? `${at}?name=${encodeURIComponent(loaded.name)}` : `${at}?expected=${known.revision}`;
      const response = await host(url, { method, headers: { 'content-type': 'application/json' }, body: JSON.stringify(document) });
      session.lastSave = { method, url, status: response.status };
      if (response.status === 409) {
        const board = await response.json();
        session.conflict = { serverRevision: board.revision, board };
        throw new BoardMovedOn(board.revision);
      }
      if (!response.ok) {
        session.saveFailure = { status: response.status };
        throw new Error(`the host refused the save (${response.status})`);
      }
      known.revision = (await response.json()).revision;
    },
  });

  // edit_text is left out: it changes a string of the board (a read's path among them) in a way the loop's read rules do not see.
  const tools = boardTools(store, { editText: false });
  session.store = store;
  session.tools = Object.fromEntries(tools.map(tool => [tool.name, tool]));
  session.functionTools = tools.map(functionTool);
  session.document = async () => (await store.getBoard('board')).document;
  return session;
}
