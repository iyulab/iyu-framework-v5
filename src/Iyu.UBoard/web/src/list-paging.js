// How the board list reads the host a page at a time. The host walks its boards past the ones the person may not open, so a page can
// hold fewer boards than asked for — even none — while the host still has more to look at. The only thing that says there is no more is
// the host not naming where the next page starts; the length of a page says nothing.

/** How many empty pages in a row one reading follows on its own before it stops and leaves the rest to the person's «Show more». */
export const FOLLOW_EMPTY_PAGES = 3;

/**
 * Asks the host for the page that starts at `skip`. Resolves to `{ items, next }`: `next` is where to ask for the following page, or
 * `null` when the host has nothing more to look at.
 */
export async function readPage(api, { skip, take }) {
  const page = await api.list({ skip, take });
  return { items: page.items, next: page.next };
}

/**
 * Reads from `skip` until a page holds a board, following at most `follow` empty pages the host says go on. A page with boards ends the
 * reading — this is not a scroll that reads everything — and so does the host's word that there is nothing more. Resolves to `{ items, next }`
 * like `readPage`; empty items with a `next` means the bound was reached and the person decides whether to look further.
 */
export async function readUntilSome(api, { skip, take, follow = FOLLOW_EMPTY_PAGES }) {
  let page = await readPage(api, { skip, take });
  for (let followed = 0; page.items.length === 0 && page.next !== null && followed < follow; followed++) {
    page = await readPage(api, { skip: page.next, take });
  }
  return page;
}

/** The boards read so far with a page added to them; a board that is already there is not added twice. */
export const addPage = (boards, items) => [...boards, ...items.filter(row => !boards.some(board => board.id === row.id))];
