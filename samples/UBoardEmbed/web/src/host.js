// The glue between the viewer's document store and the host that keeps the boards.
//
// A store holds one board in memory and calls `save(document)` with the document only. The host wants to know which revision
// the save worked from, so the glue carries it: it starts at the revision the page loaded and moves when a save is accepted.
// When the host says the board has moved on (409), `save` rejects and the store puts its board back as it was.
import { boardTools, DocumentBoardStore } from '@iyulab/u-board-mcp/tools';

export const BOARD_ID = 'plant';

async function json(response) {
  return response.json();
}

export const host = {
  async get(id = BOARD_ID) {
    const response = await fetch(`/api/boards/${id}`);
    return response.ok ? json(response) : null;
  },

  async create(document, id = BOARD_ID) {
    const response = await fetch(`/api/boards/${id}?name=${encodeURIComponent('Plant overview')}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(document),
    });
    return { status: response.status, board: await json(response) };
  },

  // A save that does not go through a store: whatever else writes to the host (an import, another client) can write what
  // this viewer's check would have refused.
  async putRaw(document, expected, id = BOARD_ID) {
    const response = await fetch(`/api/boards/${id}?expected=${expected}`, {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(document),
    });
    return { status: response.status, board: await json(response) };
  },

  async revisions(id = BOARD_ID) {
    return json(await fetch(`/api/boards/${id}/revisions`));
  },

  async publish(revision, id = BOARD_ID) {
    return json(await fetch(`/api/boards/${id}/publish?revision=${revision}`, { method: 'POST' }));
  },

  async withdraw(id = BOARD_ID) {
    return json(await fetch(`/api/boards/${id}/withdraw`, { method: 'POST' }));
  },

  async publications(id = BOARD_ID) {
    return json(await fetch(`/api/boards/${id}/publications`));
  },

  async published(id = BOARD_ID) {
    const response = await fetch(`/api/boards/${id}/published`);
    return response.status === 200 ? json(response) : null;
  },

  async fallback(id = BOARD_ID) {
    return json(await fetch(`/api/boards/${id}/fallback`));
  },
};

/** Thrown from `save` when the host says the board has moved on since this session loaded it. */
export class BoardMovedOn extends Error {
  constructor(current) {
    super(`the host's board is at revision ${current}; this session worked from an earlier one — reload it`);
    this.current = current;
  }
}

/** One person's editing session on a board: a store, the tools an assistant would be given, and the revision it knows. */
export function openSession(loaded, id = BOARD_ID) {
  const known = { revision: loaded.revision };
  const store = new DocumentBoardStore(structuredClone(loaded.document), {
    name: loaded.name,
    revision: loaded.revision,
    save: async document => {
      const response = await fetch(`/api/boards/${id}?expected=${known.revision}`, {
        method: 'PUT',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(document),
      });
      if (response.status === 409) throw new BoardMovedOn((await json(response)).revision);
      if (!response.ok) throw new Error(`the host refused the save (${response.status})`);
      known.revision = (await json(response)).revision;
    },
  });
  const tools = Object.fromEntries(boardTools(store).map(tool => [tool.name, tool]));
  return { store, tools, known };
}

/** Runs a tool the way a model's tool call would, without a model: arguments in, the tool's JSON answer out. */
export async function callTool(session, name, args) {
  const result = await session.tools[name].run(args);
  const text = result.content[0].text;
  return { isError: Boolean(result.isError), answer: JSON.parse(text) };
}
