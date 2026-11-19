import test from 'node:test';
import assert from 'node:assert/strict';
import { createAssistant } from '../src/agent-loop.js';
import { MAX_RENDER_FIXES, inspectRendering, renderedForModel } from '../src/render-check.js';
import { workflowRules } from '../src/prompt.js';
import { FIRST_PATCH, calls, json, makeWorld, recorder, say } from '../testkit/world.js';

// ---- a board and the answers its reads get

const read = (name, extra = {}) => ({ from: 'odata', path: `/$data/${name}`, ...extra });
const answers = {
  total: { value: [{ Total: 1234.5, Discount: 0, Orders: 7 }] },
  months: { value: [{ Month: '2026-01', Revenue: 10, Orders: 3 }, { Month: '2026-02', Revenue: 20, Orders: 4 }, { Month: '2026-03', Revenue: 40, Orders: 5 }] },
  zeros: { value: [{ Month: '2026-01', Revenue: 0 }, { Month: '2026-02', Revenue: 0 }] },
  words: { value: [{ Month: '2026-01', Revenue: 'n/a' }] },
  none: { value: [] },
  object: { value: { Month: '2026-01' } },
  page: { value: [{ Id: 1, Name: 'a' }, { Id: 2, Name: 'b' }], '@odata.count': 640 },
};
const fetcher = async url => {
  const name = url.replace('/$data/', '');
  return name in answers ? json(200, answers[name]) : json(404, {});
};
const board = (parts, data) => ({ data, parts, layout: { kind: 'grid', items: Object.keys(parts).map(id => [id, 6]) } });
const look = async (document, ids = Object.keys(document.parts)) => Object.fromEntries((await inspectRendering({ document, ids, fetcher })).tiles.map(tile => [tile.id, tile]));

// ---- what a part drew

test('a part that drew what it was told to shows what it shows, in few words', async () => {
  const document = board(
    {
      total: { as: 'stat', of: 'total', show: 'Total', title: 'Total' },
      line: { as: 'chart.line', of: 'months', title: 'By month', x: 'Month', y: 'Revenue' },
      rows: { as: 'table', of: 'page', title: 'Rows' },
    },
    { total: read('total', { fields: { Total: { at: '/value/0/Total' } } }), months: read('months', { list: '/value', key: 'Month' }), page: read('page', { list: '/value', key: 'Id' }) },
  );
  const seen = await look(document);
  assert.deepEqual({ state: seen.total.state, shows: seen.total.shows }, { state: 'ok', shows: '1234.5' });
  assert.deepEqual({ state: seen.line.state, shows: seen.line.shows }, { state: 'ok', shows: '3 points · Month 2026-01…2026-03 · Revenue 10…40' });
  assert.deepEqual({ state: seen.rows.state, shows: seen.rows.shows }, { state: 'ok', shows: '2 rows of 640' });
});

test('a value the read does not hold, a field the rows do not have and a list that is not a list are problems with the reason', async () => {
  const document = board(
    {
      lost: { as: 'stat', of: 'total', show: 'Total', title: 'Total' },
      axis: { as: 'chart.line', of: 'months', title: 'Axis', x: 'Day', y: 'Revenue' },
      measure: { as: 'chart.bar', of: 'months', title: 'Measure', x: 'Month', y: 'Sales' },
      words: { as: 'chart.bar', of: 'words', title: 'Words', x: 'Month', y: 'Revenue' },
      notlist: { as: 'chart.bar', of: 'object', title: 'Not a list', x: 'Month', y: 'Revenue' },
      pointer: { as: 'chart.bar', of: 'wrongpointer', title: 'Pointer', x: 'Month', y: 'Revenue' },
      names: { as: 'bar-list', of: 'months', title: 'Names', label: 'Region', value: 'Revenue' },
    },
    {
      total: read('total', { fields: { Total: { at: '/value/0/Sum' } } }),
      months: read('months', { list: '/value', key: 'Month' }),
      words: read('words', { list: '/value', key: 'Month' }),
      object: read('object', { list: '/value', key: 'Month' }),
      wrongpointer: read('months', { list: '/rows', key: 'Month' }),
    },
  );
  const seen = await look(document);
  assert.equal(seen.lost.state, 'problem');
  assert.match(seen.lost.problem, /could not be read.*\/value\/0\/Sum/);
  assert.equal(seen.axis.state, 'problem');
  assert.match(seen.axis.problem, /x "Day" is not in the rows \(they have: Month, Revenue, Orders\)/);
  assert.match(seen.measure.problem, /y "Sales" is not in the rows/);
  assert.match(seen.words.problem, /Revenue has no numbers.*n\/a/);
  assert.match(seen.notlist.problem, /object where the part needs a list/);
  assert.match(seen.pointer.problem, /could not be read.*\/rows/);
  assert.match(seen.names.problem, /label "Region" is not in the rows/);
});

test('0 where the read holds other numbers, and a chart of zeros, are to be checked; a read that matched nothing is only said to be empty', async () => {
  const document = board(
    {
      discount: { as: 'stat', of: 'total', show: 'Discount', title: 'Discount' },
      zeros: { as: 'chart.bar', of: 'zeros', title: 'Zeros', x: 'Month', y: 'Revenue' },
      nothing: { as: 'table', of: 'none', title: 'Nothing' },
      noneStat: { as: 'stat', of: 'none', show: 'Total', title: 'Nothing here' },
    },
    { total: read('total', { fields: { Discount: { at: '/value/0/Discount' } } }), zeros: read('zeros', { list: '/value', key: 'Month' }), none: read('none', { list: '/value', key: 'Id', fields: { Total: { at: '/value/0/Total' } } }) },
  );
  const seen = await look(document, ['discount', 'zeros', 'nothing']);
  assert.equal(seen.discount.state, 'check');
  assert.match(seen.discount.problem, /shows 0, yet the read's row holds non-zero Total=1234\.5, Orders=7 \(this tile reads \/value\/0\/Discount\)/);
  assert.equal(seen.zeros.state, 'check');
  assert.match(seen.zeros.problem, /every Revenue value is 0/);
  assert.deepEqual({ state: seen.nothing.state, shows: seen.nothing.shows }, { state: 'empty', shows: 'no rows' });
});

test('a part that reads nothing is not looked at, a read two parts share is made once, and a stopped look says nothing', async () => {
  const document = board(
    { note: { as: 'label', title: 'A note' }, a: { as: 'chart.bar', of: 'months', title: 'A', x: 'Month', y: 'Revenue' }, b: { as: 'chart.line', of: 'months', title: 'B', x: 'Month', y: 'Orders' } },
    { months: read('months', { list: '/value', key: 'Month' }) },
  );
  const asked = [];
  const result = await inspectRendering({ document, ids: ['note', 'a', 'b'], fetcher: async url => { asked.push(url); return fetcher(url); } });
  assert.deepEqual(result.tiles.map(tile => tile.id), ['a', 'b']);
  assert.deepEqual(asked, ['/$data/months']);
  const stop = new AbortController();
  stop.abort();
  assert.equal(await inspectRendering({ document, ids: ['a'], fetcher, signal: stop.signal }), undefined);
});

test('a board variable is read at its default, as the viewer starts', async () => {
  const document = { ...board({ a: { as: 'chart.bar', of: 'months', title: 'A', x: 'Month', y: 'Revenue' } }, { months: read('{{$which}}', { list: '/value', key: 'Month' }) }), variables: { which: { options: ['months', 'zeros'], default: 'zeros' } } };
  assert.equal((await look(document)).a.state, 'check');
});

// ---- what the model is told

test('the model is told in a few lines: what drew, what did not and why, what is empty, and what to do', async () => {
  const good = { id: 'a', kind: 'stat', state: 'ok', shows: '12' };
  const bad = { id: 'b', kind: 'chart.line', state: 'problem', problem: 'x "Day" is not in the rows' };
  const told = renderedForModel({ tiles: [good, bad, { id: 'c', kind: 'table', state: 'empty', shows: 'no rows' }] }, { fixesLeft: 2 });
  assert.deepEqual(told.drew, ['a: 12']);
  assert.deepEqual(told.problems, [{ id: 'b', kind: 'chart.line', problem: 'x "Day" is not in the rows' }]);
  assert.deepEqual(told.empty, ['c']);
  assert.match(told.next, /Fix the part or its read with one more patch_board/);
  assert.match(renderedForModel({ tiles: [bad] }, { fixesLeft: 0 }).next, /^Do not edit again/);
  assert.equal(renderedForModel({ tiles: [good] }, { fixesLeft: 2 }).next, undefined, 'a board that drew as read is not asked for anything');
});

test('what the model is told costs a few hundred characters for a whole board, and stays bounded for a large one', async () => {
  const tiles = Array.from({ length: 40 }, (_, n) => ({ id: `tile-${n}`, kind: 'chart.line', state: n % 2 ? 'ok' : 'problem', shows: '12 points · Month 2026-01…2026-12 · Revenue 10…400', problem: 'x "A very long name of a field that is not there" is not in the rows (they have: ' + 'Field, '.repeat(40) + ')' }));
  const told = JSON.stringify(renderedForModel({ tiles }, { fixesLeft: 2 }));
  assert.ok(told.length < 5000, `${told.length} characters for forty parts`);
  const six = JSON.stringify(renderedForModel({ tiles: tiles.slice(0, 6).map(tile => ({ ...tile, state: 'ok' })) }, { fixesLeft: 2 }));
  assert.ok(six.length < 600, `${six.length} characters for six parts that drew`);
});

// ---- the loop: a first edit that draws wrong, and a model that reads what the board drew

const KPI = '/$data/Orders?$apply=aggregate(Total with sum as Revenue)';
const MONTHLY = '/$data/Orders?$apply=groupby((OrderDate),aggregate(Total with sum as Revenue))';
const wrongFirst = () => calls(['patch_board', { patch: [
  { op: 'add', path: '/data', value: { kpi: { from: 'odata', path: KPI, fields: { Revenue: { at: '/value/0/Total', label: 'Revenue' } } }, monthly: { from: 'odata', path: MONTHLY, list: '/value', key: 'OrderDate' } } },
  { op: 'add', path: '/parts/revenue', value: { as: 'stat', of: 'kpi', show: 'Revenue', title: 'Revenue' } },
  { op: 'add', path: '/parts/trend', value: { as: 'chart.line', of: 'monthly', title: 'Monthly revenue', x: 'OrderDate', y: 'Total' } },
  { op: 'add', path: '/layout/items/-', value: ['revenue', 4] },
  { op: 'add', path: '/layout/items/-', value: ['trend', 8] },
] }]);
const fixes = [
  { op: 'replace', path: '/data/kpi/fields/Revenue/at', value: '/value/0/Revenue' },
  { op: 'replace', path: '/parts/trend/y', value: 'Revenue' },
];
// A model that does what the tool results tell it: it fixes a part the board says it drew wrong, and says it is done when the board says all is well.
const readsTheBoard = ({ toolResults }) => {
  const problems = toolResults.flatMap(result => result?.rendered?.problems ?? []);
  return problems.length > 0 ? calls(['patch_board', { patch: fixes }]) : say('Done — the revenue total and the monthly line.');
};
const lookAtSaved = async world => (await inspectRendering({ document: world.host.document, ids: Object.keys(world.host.document.parts), fetcher: world.fetch })).tiles;

test('a first edit that draws wrong is shown to the model with what the board drew, the model corrects it in the same turn, and the last revision draws right', async () => {
  const world = makeWorld({ model: [wrongFirst(), readsTheBoard, readsTheBoard] });
  const rec = recorder();
  const assistant = createAssistant(world.assistantDeps({ emit: rec.emit }));
  const result = await assistant.send('Show monthly revenue with a total.');

  const first = world.modelRequests()[1].body.messages.filter(message => message.role === 'tool').map(message => JSON.parse(message.content)).find(told => told.rendered);
  assert.ok(first, 'the result of the first save carries what the board drew');
  assert.deepEqual(first.rendered.problems.map(problem => problem.id), ['revenue', 'trend']);
  assert.match(first.rendered.problems[0].problem, /could not be read.*\/value\/0\/Total/);
  assert.match(first.rendered.problems[1].problem, /y "Total" is not in the rows \(they have: OrderDate, Revenue\)/);
  assert.match(first.rendered.next, /one more patch_board/);
  assert.equal(first.revision, 1, 'the answer of the edit itself is still there, after what the board drew');

  assert.equal(result.outcome, 'done');
  assert.deepEqual(result.revision, { from: 0, to: 2 });
  assert.deepEqual(result.renderProblems, [], 'nothing is left wrong');
  assert.equal(world.host.revision, 2);
  const states = (await lookAtSaved(world)).map(tile => `${tile.id}:${tile.state}`);
  assert.deepEqual(states, ['revenue:ok', 'trend:ok'], 'the saved board draws what it should');
  assert.equal(world.queue.length, 0);
  // what the person sees of it: the first save says what did not draw, the second that all did
  const boardSteps = result.steps.filter(step => step.kind === 'board');
  assert.deepEqual(boardSteps.map(step => step.outcome), ['drew-wrong', null]);
  assert.match(boardSteps[0].detail, /2 tiles did not draw as intended/);
  assert.match(boardSteps[1].detail, /drew as read: 2 tiles checked/);
});

test('0 where the read holds other numbers is told the same way, and the model may keep it when 0 is right', async () => {
  const odata = url => json(200, url === KPI ? { value: [{ Orders: 0, Revenue: 1234.5 }] } : { value: [{ OrderDate: '2026-01', Revenue: 1234.5 }] });
  const world = makeWorld({ odata, model: [
    calls(['patch_board', { patch: [
      { op: 'add', path: '/data', value: { kpi: { from: 'odata', path: KPI, fields: { Orders: { at: '/value/0/Orders', label: 'Orders' } } } } },
      { op: 'add', path: '/parts/orders', value: { as: 'stat', of: 'kpi', show: 'Orders', title: 'Orders' } },
      { op: 'add', path: '/layout/items/-', value: ['orders', 4] },
    ] }]),
    ({ toolResults }) => { assert.match(toolResults[0].rendered.problems[0].problem, /shows 0, yet the read's row holds non-zero Revenue=1234\.5/); return say('Orders is 0 in this period; revenue is 1234.5 and is not on the board.'); },
  ] });
  const result = await createAssistant(world.assistantDeps()).send('Show the number of orders.');
  assert.equal(result.outcome, 'done');
  assert.equal(world.host.revision, 1, 'the model kept what is right');
  assert.deepEqual(result.renderProblems.map(problem => problem.id), ['orders'], 'what is left to check is on the turn, so the screen can say it');
});

test('corrections are bounded: after the allowed ones the model is told not to edit again, and the turn ends with the problem said', async () => {
  let n = 0;
  const keepsEditing = ({ toolResults }) => {
    const told = toolResults.find(result => result?.rendered)?.rendered;
    if (told?.next?.startsWith('Do not edit again')) return say('The revenue tile still does not draw; I would try the other field next.');
    n += 1;
    return calls(['patch_board', { patch: [{ op: 'replace', path: '/parts/revenue/title', value: `Revenue ${n}` }] }]);
  };
  const world = makeWorld({ model: [wrongFirst(), keepsEditing, keepsEditing, keepsEditing] });
  const result = await createAssistant(world.assistantDeps()).send('Show monthly revenue with a total.');

  assert.equal(world.host.revision, 1 + MAX_RENDER_FIXES, 'the first edit and the corrections allowed, no more');
  const nexts = world.modelRequests().slice(1).map(request => request.body.messages.filter(message => message.role === 'tool').map(message => JSON.parse(message.content)).filter(told => told.rendered).at(-1).rendered.next);
  assert.equal(nexts.length, MAX_RENDER_FIXES + 1);
  assert.ok(nexts.slice(0, MAX_RENDER_FIXES).every(next => /one more patch_board/.test(next)));
  assert.match(nexts.at(-1), /^Do not edit again/);
  assert.equal(result.outcome, 'done');
  assert.match(result.reply, /still does not draw/);
  assert.ok(result.renderProblems.some(problem => problem.id === 'revenue'));
});

test('a look that fails or takes too long leaves the saved edit as it was, and a model that is not asked to look is not told anything', async () => {
  for (const inspect of [() => { throw new Error('the look broke'); }, () => new Promise(() => {})]) {
    const world = makeWorld({ model: [calls(['patch_board', { patch: FIRST_PATCH }]), say('Built.')] });
    const result = await createAssistant(world.assistantDeps({ inspect, inspectTimeoutMs: 20 })).send('Build it.');
    assert.equal(result.outcome, 'done');
    assert.equal(world.host.revision, 1);
    const told = JSON.parse(world.modelRequests()[1].body.messages.find(message => message.role === 'tool').content);
    assert.equal(told.rendered, undefined);
    assert.equal(told.revision, 1);
  }
  const quiet = makeWorld({ model: [calls(['patch_board', { patch: FIRST_PATCH }]), say('Built.')] });
  await createAssistant(quiet.assistantDeps({ inspect: false })).send('Build it.');
  assert.equal(quiet.dataRequests().length, 0, 'no look, no read');
});

test('a board that draws as read is listed, in a line a part, and nothing is asked of the model', async () => {
  const world = makeWorld({ model: [calls(['patch_board', { patch: FIRST_PATCH }]), say('Built.')] });
  const result = await createAssistant(world.assistantDeps()).send('Build it.');
  const told = JSON.parse(world.modelRequests()[1].body.messages.find(message => message.role === 'tool').content).rendered;
  assert.deepEqual(told, { checked: 2, drew: ['revenue: 1234.5', 'trend: 2 points · OrderDate 2026-01…2026-02 · Revenue 400…834.5'] });
  assert.deepEqual(result.renderProblems, []);
});

test('the rules tell a model that may edit what `rendered` is; one that may only answer is told nothing of it', () => {
  assert.match(workflowRules({ canEdit: true }), /starts with `rendered`.*never state a number a tile does not show/s);
  assert.equal(workflowRules({ canEdit: false }).includes('rendered'), false);
});
