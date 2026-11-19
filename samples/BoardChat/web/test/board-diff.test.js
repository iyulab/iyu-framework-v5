import test from 'node:test';
import assert from 'node:assert/strict';
import { changedParts, partsTouched, removedParts } from '../src/board-diff.js';
import { buildSystemPrompt, workflowRules, DATA_SOURCE_ID } from '../src/prompt.js';
import { openBoardSession, newBoard } from '../src/session.js';
import { makeWorld } from '../testkit/world.js';

const board = () => ({
  variables: { scope: { options: ['all', 'paid'], default: 'all' } },
  data: {
    kpi: { from: 'odata', path: '/$data/Orders?$apply=aggregate(Total with sum as Revenue)', list: '/value' },
    monthly: { from: 'odata', path: '/$data/Orders?$apply=groupby((OrderDate),aggregate(Total with sum as Revenue))&x={{$scope}}', list: '/value' },
  },
  parts: {
    revenue: { as: 'stat', of: 'kpi', show: 'Revenue', title: 'Revenue' },
    trend: { as: 'chart.line', of: 'monthly', title: 'Monthly revenue', x: 'OrderDate', y: 'Revenue' },
  },
  layout: { kind: 'grid', items: [['revenue', 4], ['trend', 8]] },
});
const edit = change => {
  const next = board();
  change(next);
  return next;
};

test('a part is new when it was not there, and edited when it, its place, its read, or a variable its read uses differs', () => {
  const before = board();
  assert.deepEqual(changedParts(before, board()), {});
  assert.deepEqual(changedParts(before, edit(b => { b.parts.extra = { as: 'label', title: 'Note' }; b.layout.items.push('extra'); })), { extra: 'new' });
  assert.deepEqual(changedParts(before, edit(b => { b.parts.trend.as = 'chart.bar'; })), { trend: 'edited' });
  assert.deepEqual(changedParts(before, edit(b => { b.layout.items[1] = ['trend', 12]; })), { trend: 'edited' });
  assert.deepEqual(changedParts(before, edit(b => { b.data.kpi.path += '&$filter=Status eq \'Paid\''; })), { revenue: 'edited' });
  assert.deepEqual(changedParts(before, edit(b => { b.variables.scope.default = 'paid'; })), { trend: 'edited' });
});

test('a change that reaches every tile through the data marks every tile, and a removed part is listed apart', () => {
  const before = board();
  const filtered = edit(b => {
    for (const read of Object.values(b.data)) read.path += "&$filter=Status eq 'Paid'";
  });
  assert.deepEqual(changedParts(before, filtered), { revenue: 'edited', trend: 'edited' });
  const smaller = edit(b => { delete b.parts.trend; b.layout.items.pop(); });
  assert.deepEqual(changedParts(before, smaller), {});
  assert.deepEqual(removedParts(before, smaller), ['trend']);
  assert.deepEqual(changedParts(undefined, before), { revenue: 'new', trend: 'new' });
});

test('the parts a patch touches are the ones to draw as loading: named parts, placed parts, and parts that read a changed read', () => {
  const document = board();
  assert.deepEqual(partsTouched([{ op: 'replace', path: '/parts/trend/as', value: 'chart.bar' }], document), ['trend']);
  assert.deepEqual(partsTouched([{ op: 'add', path: '/layout/items/-', value: ['extra', 4] }, { op: 'add', path: '/parts/extra', value: {} }], document), ['extra']);
  assert.deepEqual(partsTouched([{ op: 'replace', path: '/data/kpi/path', value: '/$data/x' }], document), ['revenue']);
  assert.deepEqual(partsTouched([{ op: 'replace', path: '/board/title', value: 'x' }], document), []);
});

test('the system prompt carries the board language of U-Board, the aggregate-first and paging rules, and the open board', () => {
  const rules = workflowRules();
  assert.match(rules, /aggregate/i);
  assert.match(rules, /top = 50 with count = true/);
  assert.match(rules, /\$top above 100/);
  assert.match(rules, /never fetch the rows/);
  assert.match(rules, /Never pass a revision/);
  assert.match(rules, /0 rows that is not an error/);
  assert.match(rules, /locked/);
  assert.ok(rules.includes(`"from": "${DATA_SOURCE_ID}"`));

  const prompt = buildSystemPrompt({ board: { revision: 3, document: board() } });
  assert.ok(prompt.startsWith('# How you work here'));
  assert.match(prompt, /# Writing a board/); // U-Board's own rulebook
  assert.match(prompt, /Revision 3\./);
  assert.ok(prompt.includes('"trend"'));
  assert.match(buildSystemPrompt({ board: { revision: 0, document: board() } }), /nothing saved yet/);
});

test('a session creates the board on its first save and keeps the host\'s count after that', async () => {
  const world = makeWorld();
  const host = (url, init) => world.fetch(url, init);
  const session = openBoardSession({ id: 'revenue', loaded: newBoard('Revenue overview'), host, boards: '/uboard/boards' });
  const patch = [{ op: 'add', path: '/parts/note', value: { as: 'label', title: 'Hello' } }, { op: 'add', path: '/layout/items/-', value: 'note' }];
  const first = JSON.parse((await session.tools.patch_board.run({ patch })).content[0].text);
  assert.equal(first.revision, 1);
  const second = JSON.parse((await session.tools.patch_board.run({ patch: [{ op: 'replace', path: '/parts/note/title', value: 'Hi' }] })).content[0].text);
  assert.equal(second.revision, 2);
  assert.deepEqual(world.boardWrites().map(r => `${r.method} ${r.url}`), ['POST /uboard/boards/revenue?name=Revenue%20overview', 'PUT /uboard/boards/revenue?expected=1']);
  assert.equal(session.known.revision, 2);
});
