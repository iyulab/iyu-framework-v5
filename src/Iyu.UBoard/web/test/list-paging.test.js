import test from 'node:test';
import assert from 'node:assert/strict';
import { NEXT_SKIP_HEADER, endpointsFor, makeApi } from '../src/host-api.js';
import { FOLLOW_EMPTY_PAGES, addPage, readPage, readUntilSome } from '../src/list-paging.js';
import { json } from '../testkit/world.js';

const endpoints = endpointsFor('/uboard');

// A host that holds `total` boards in id order, lets the person open those `visible` accepts, and looks at no more than `bound` boards
// for one request — the way the board routes do. It answers the offset to go on from in the header, and leaves it out at the end.
function hostOf({ total, visible, bound }) {
  const asked = [];
  const fetcher = async url => {
    const query = new URL(url, 'http://host').searchParams;
    const skip = Number(query.get('skip'));
    const take = Number(query.get('take'));
    asked.push({ skip, take });
    const items = [];
    let examined = 0;
    let offset = skip;
    while (offset < total && examined < bound) {
      examined++;
      if (visible(offset)) {
        if (items.length === take) return json(200, items, { [NEXT_SKIP_HEADER]: offset });
        items.push({ id: `b${String(offset).padStart(4, '0')}`, name: `Board ${offset}` });
      }
      offset++;
    }
    return json(200, items, offset < total ? { [NEXT_SKIP_HEADER]: offset } : {});
  };
  return { api: makeApi(fetcher, endpoints), asked };
}

async function readAll(api, take) {
  let boards = [];
  let next = 0;
  const pages = [];
  while (next !== null) {
    const page = await readPage(api, { skip: next, take });
    pages.push(page);
    boards = addPage(boards, page.items);
    next = page.next;
    assert.ok(pages.length < 100, 'the reading does not end');
  }
  return { boards, pages };
}

test('the list answers the offset to go on from, and null when the host leaves the header out', async () => {
  const some = makeApi(async () => json(200, [{ id: 'a' }], { 'X-UBoard-Next-Skip': '7' }), endpoints);
  const last = makeApi(async () => json(200, [{ id: 'a' }]), endpoints);
  const odd = makeApi(async () => json(200, [], { 'X-UBoard-Next-Skip': 'soon' }), endpoints);
  assert.deepEqual(await some.list({ skip: 0, take: 1 }), { items: [{ id: 'a' }], next: 7 });
  assert.deepEqual(await last.list(), { items: [{ id: 'a' }], next: null });
  assert.deepEqual(await odd.list(), { items: [], next: null }, 'a value that is not an offset is not followed');
  assert.deepEqual(await makeApi(async () => json(500, {}), endpoints).list(), { items: [], next: null });
});

test('a host that hides most boards: following the offset it names reads every visible board once, with no skip', async () => {
  const { api, asked } = hostOf({ total: 120, visible: n => n % 7 === 0, bound: 1000 });
  const { boards, pages } = await readAll(api, 5);
  assert.deepEqual(boards.map(b => b.id), Array.from({ length: 18 }, (_, i) => `b${String(i * 7).padStart(4, '0')}`));
  assert.deepEqual(pages.map(p => p.items.length), [5, 5, 5, 3]);
  assert.deepEqual(asked.map(a => a.skip), [0, 35, 70, 105], 'each page was asked for where the page before said to go on');
});

test('a short page that names where to go on is not the end: an empty page is followed, and only a missing offset ends the reading', async () => {
  const { api } = hostOf({ total: 250, visible: n => n === 0 || n === 220, bound: 100 });
  const { boards, pages } = await readAll(api, 50);
  assert.deepEqual(boards.map(b => b.id), ['b0000', 'b0220']);
  assert.deepEqual(pages.map(p => [p.items.length, p.next]), [[1, 100], [0, 200], [1, null]]);
});

test('a person who may open nothing is read through and ends on the host\'s word, not on an empty page', async () => {
  const { api, asked } = hostOf({ total: 250, visible: () => false, bound: 100 });
  const { boards, pages } = await readAll(api, 50);
  assert.deepEqual(boards, []);
  assert.deepEqual(pages.map(p => p.next), [100, 200, null]);
  assert.deepEqual(asked.map(a => a.skip), [0, 100, 200]);
});

test('a reading follows empty pages by itself until a board turns up, and no further', async () => {
  const { api, asked } = hostOf({ total: 250, visible: n => n === 220, bound: 100 });
  const page = await readUntilSome(api, { skip: 0, take: 50 });
  assert.deepEqual(page.items.map(b => b.id), ['b0220']);
  assert.equal(page.next, null);
  assert.deepEqual(asked.map(a => a.skip), [0, 100, 200], 'two empty pages were followed to reach the board');
});

test('a page with boards ends the reading: it is not a scroll that reads everything', async () => {
  const { api, asked } = hostOf({ total: 250, visible: n => n === 0 || n === 220, bound: 100 });
  const page = await readUntilSome(api, { skip: 0, take: 50 });
  assert.deepEqual(page.items.map(b => b.id), ['b0000']);
  assert.equal(page.next, 100, 'the rest is left to ask for');
  assert.deepEqual(asked.map(a => a.skip), [0]);
});

test('a reading gives up after the bound and says where it stopped, so the person decides whether to look further', async () => {
  const { api, asked } = hostOf({ total: 2000, visible: n => n === 1900, bound: 100 });
  const page = await readUntilSome(api, { skip: 0, take: 50 });
  assert.deepEqual(page.items, []);
  assert.equal(page.next, 100 * (FOLLOW_EMPTY_PAGES + 1));
  assert.equal(asked.length, FOLLOW_EMPTY_PAGES + 1, 'the first request and no more than the bound of follow-ups');
  const further = await readUntilSome(api, { skip: page.next, take: 50, follow: 0 });
  assert.deepEqual(further.items, [], 'with no follow-ups allowed a reading is one request');
});

test('the word of the host that there is nothing more ends a reading before the bound', async () => {
  const { api, asked } = hostOf({ total: 250, visible: () => false, bound: 100 });
  const page = await readUntilSome(api, { skip: 0, take: 50 });
  assert.deepEqual(page, { items: [], next: null });
  assert.deepEqual(asked.map(a => a.skip), [0, 100, 200]);
});

test('a board a page names twice is listed once', () => {
  assert.deepEqual(addPage([{ id: 'a' }, { id: 'b' }], [{ id: 'b' }, { id: 'c' }]).map(b => b.id), ['a', 'b', 'c']);
});
