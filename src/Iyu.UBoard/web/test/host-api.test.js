import test from 'node:test';
import assert from 'node:assert/strict';
import { boardPage, endpointsFor, makeApi, newBoardId, pageConfig } from '../src/host-api.js';
import { buildSystemPrompt, workflowRules } from '../src/prompt.js';
import { DATA_ROOT, buildODataUrl, setDataRoot } from '../src/odata.js';
import { json } from '../testkit/world.js';

test('the page learns where the routes are, and which board it is for, from what the server wrote into it', () => {
  const written = { 'uboard-prefix': '/app/uboard/', 'uboard-board': 'q4 plan' };
  assert.deepEqual(pageConfig(name => written[name]), { prefix: '/app/uboard', board: 'q4 plan' });
  assert.deepEqual(pageConfig(() => undefined), { prefix: '/uboard', board: undefined }, 'the default prefix, and no board on the list');
  assert.equal(pageConfig(name => (name === 'uboard-board' ? '' : undefined)).board, undefined);
});

test('a board\'s page address holds its id as one path segment', () => {
  const endpoints = endpointsFor('/uboard');
  assert.equal(boardPage(endpoints, 'revenue'), '/uboard/ui/boards/revenue');
  assert.equal(boardPage(endpoints, 'a b/c'), '/uboard/ui/boards/a%20b%2Fc');
});

test('a new board\'s id is made of the words of its name and a short tail, and is never empty', () => {
  assert.equal(newBoardId('Q4 sales review', () => 0.5), 'q4-sales-review-8000');
  assert.equal(newBoardId('  Café  Ünïcode!! ', () => 0), 'cafe-unicode-0000');
  assert.equal(newBoardId('분기 보고', () => 1 / 0x10000), 'board-0001', 'a name with no letters of the id alphabet still gets an id');
  assert.match(newBoardId('x'), /^x-[0-9a-f]{4}$/);
});

test('what the host says a person may do is read once, and a host that does not answer offers nothing', async () => {
  const endpoints = endpointsFor('/uboard');
  const open = makeApi(async url => (url === endpoints.access ? json(200, { create: true, assistant: true, catalog: false, dataRoot: '/odata' }) : json(404, {})), endpoints);
  assert.deepEqual(await open.access(), { create: true, assistant: true, catalog: false, dataRoot: '/odata' });
  const shut = makeApi(async () => json(500, {}), endpoints);
  assert.deepEqual(await shut.access(), { create: false, assistant: false, catalog: false, dataRoot: undefined });
});

test('a board that is not there is null, and a refused publish is told with its status', async () => {
  const endpoints = endpointsFor('/uboard');
  const api = makeApi(async (url, init) => (init?.method === 'POST' ? json(403, {}) : json(404, {})), endpoints);
  assert.equal(await api.board('missing'), null);
  assert.deepEqual(await api.publish('b', 2), { status: 403, line: null });
});

test('the data root is the host\'s to name, and every query is written under it', () => {
  try {
    setDataRoot('/odata/');
    assert.equal(DATA_ROOT, '/odata');
    assert.equal(buildODataUrl({ entity: 'Orders', top: 5 }), '/odata/Orders?$top=5');
    assert.match(workflowRules(), /"path": "\/odata\/<Entity>/);
    setDataRoot('not a path');
    assert.equal(DATA_ROOT, '/$data', 'a value that is not an absolute path is not taken');
  } finally {
    setDataRoot('/$data');
  }
});

test('a person who may only read is told to answer and is given no steps that change the board', () => {
  const rules = workflowRules({ canEdit: false });
  assert.match(rules, /not change it/);
  assert.doesNotMatch(rules, /patch_board/);
  assert.match(workflowRules({ canEdit: true }), /patch_board/);
  assert.match(workflowRules({ canEdit: true }), /A question is not a change/);
  assert.match(buildSystemPrompt({ canEdit: false, board: { revision: 2, document: {} } }), /Revision 2\./);
});
