// The page's way to the host. Where the board routes are is the server's to say: it writes the routes' prefix (and, on a board's own
// page, the board's id) into the page it serves. Every request is the same origin's, carrying whatever sign-in the host uses (a cookie),
// so the page holds no identity of its own.

const meta = name => document.querySelector(`meta[name="${name}"]`)?.getAttribute('content') ?? undefined;

/** What the server wrote into the page: the routes' prefix, and the board this page is for (none on the list). */
export function pageConfig(read = meta) {
  return {
    prefix: (read('uboard-prefix') ?? '/uboard').replace(/\/+$/, ''),
    board: read('uboard-board') || undefined,
  };
}

/** Where the host serves what the screen asks of it. */
export const endpointsFor = prefix => ({ catalog: `${prefix}/catalog`, boards: `${prefix}/boards`, access: `${prefix}/access`, screen: `${prefix}/ui` });

/** The address of a board's page. An id is one path segment, whatever characters it holds. */
export const boardPage = (endpoints, id, name) => `${endpoints.screen}/boards/${encodeURIComponent(id)}${name ? `?name=${encodeURIComponent(name)}` : ''}`;

/** A new board's id from the name the person typed: words and digits joined by dashes, with a short random tail so two boards of one name do not meet. */
export function newBoardId(name, random = Math.random) {
  const words = name.toLowerCase().normalize('NFKD').replace(/[̀-ͯ]/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40);
  return `${words || 'board'}-${Math.floor(random() * 0x10000).toString(16).padStart(4, '0')}`;
}

const read = async response => (response.ok && response.status !== 204 ? response.json() : null);
const pick = (object, name) => object?.[name] ?? object?.[name[0].toUpperCase() + name.slice(1)];

/** `fetcher(url, init)` is the page's fetch (a test hands in its own). */
export function makeApi(fetcher, endpoints) {
  const board = id => `${endpoints.boards}/${encodeURIComponent(id)}`;
  return {
    /** `{ create, assistant, catalog, dataRoot }`: what the caller may do at all and what the host has switched on. Closed (nothing offered) when the host does not answer. */
    access: async () => {
      const answer = await read(await fetcher(endpoints.access));
      return { create: Boolean(pick(answer, 'create')), assistant: Boolean(pick(answer, 'assistant')), catalog: Boolean(pick(answer, 'catalog')), dataRoot: pick(answer, 'dataRoot') };
    },
    list: async () => (await read(await fetcher(endpoints.boards))) ?? [],
    board: async id => {
      const response = await fetcher(board(id));
      return response.status === 404 ? null : read(response);
    },
    revisions: async id => (await read(await fetcher(`${board(id)}/revisions`))) ?? [],
    revision: async (id, number) => read(await fetcher(`${board(id)}/revisions/${number}`)),
    published: async id => read(await fetcher(`${board(id)}/published`)),
    fallback: async id => (await read(await fetcher(`${board(id)}/fallback`))) ?? [],
    /** Resolves to the host's answer — `{ status, line }`; a refusal (403) is told, not thrown. */
    publish: async (id, number) => {
      const response = await fetcher(`${board(id)}/publish?revision=${number}`, { method: 'POST' });
      return { status: response.status, line: response.ok ? await response.json() : null };
    },
    // A save that goes through no store: what an import or another client does. Used to restore an old revision as the new draft.
    save: async (id, document, expected) => {
      const response = await fetcher(`${board(id)}?expected=${expected}`, { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(document) });
      return { status: response.status, board: response.status === 403 ? null : await response.json() };
    },
  };
}

export const revisionNumber = row => pick(row, 'revision');
export const revisionTime = row => pick(row, 'createdAt');
export const revisionAuthor = row => pick(row, 'createdBy');
