// How the board list reads the host a page at a time. The host walks its boards past the ones the person may not open, so a page can
// hold fewer boards than asked for — even none — while the host still has more to look at. The only thing that says there is no more is
// the host not naming where the next page starts; the length of a page says nothing.

/**
 * Asks the host for the page that starts at `skip`. Resolves to `{ items, next }`: `next` is where to ask for the following page, or
 * `null` when the host has nothing more to look at.
 */
export async function readPage(api, { skip, take }) {
  const page = await api.list({ skip, take });
  return { items: page.items, next: page.next };
}

/** The boards read so far with a page added to them; a board that is already there is not added twice. */
export const addPage = (boards, items) => [...boards, ...items.filter(row => !boards.some(board => board.id === row.id))];
