// The page's way to the host: every request carries the signed-in person (the sample signs in with a header; a real host puts
// its own authentication in front of the same routes).
export const USERS = ['analyst', 'finance'];
export const identity = { user: USERS[0] };
export const BOARD = { id: 'revenue', name: 'Revenue overview' };
const base = `/uboard/boards/${BOARD.id}`;

export const hostFetch = (url, init = {}) => fetch(url, { ...init, headers: { 'X-Sample-User': identity.user, ...init.headers } });

const read = async response => (response.ok && response.status !== 204 ? response.json() : null);
const pick = (object, name) => object?.[name] ?? object?.[name[0].toUpperCase() + name.slice(1)];

export const api = {
  catalog: async () => read(await hostFetch('/uboard/catalog')),
  board: async () => read(await hostFetch(base)),
  revisions: async () => (await read(await hostFetch(`${base}/revisions`))) ?? [],
  revision: async number => read(await hostFetch(`${base}/revisions/${number}`)),
  published: async () => read(await hostFetch(`${base}/published`)),
  fallback: async () => (await read(await hostFetch(`${base}/fallback`))) ?? [],
  publish: async number => read(await hostFetch(`${base}/publish?revision=${number}`, { method: 'POST' })),
  // A save that goes through no store: what an import or another client does. Used to restore an old revision as the new draft.
  save: async (document, expected) => {
    const response = await hostFetch(`${base}?expected=${expected}`, { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(document) });
    return { status: response.status, board: await response.json() };
  },
};
export const revisionNumber = row => pick(row, 'revision');
export const revisionTime = row => pick(row, 'createdAt');
export const revisionAuthor = row => pick(row, 'createdBy');
