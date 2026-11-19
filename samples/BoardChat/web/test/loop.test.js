import test from 'node:test';
import assert from 'node:assert/strict';
import { createAssistant, DEFAULT_ENDPOINTS } from '../src/agent-loop.js';
import { FIRST_PATCH, calls, json, makeWorld, recorder, say } from '../testkit/world.js';

const QUERY = { entity: 'Orders', apply: 'groupby((OrderDate),aggregate(Total with sum as Revenue))' };

function firstTurn() {
  return [
    calls(['read_catalog', {}], ['query_data', QUERY]),
    calls(['patch_board', { patch: FIRST_PATCH }]),
    say('Done — a revenue KPI and a monthly line.'),
  ];
}

test('steps are announced pending, then running, then done, in the order the model called them', async () => {
  const world = makeWorld({ model: firstTurn() });
  const rec = recorder();
  const assistant = createAssistant(world.assistantDeps({ emit: rec.emit }));
  const result = await assistant.send('Show monthly revenue with a KPI.');

  assert.equal(result.outcome, 'done');
  const states = id => rec.ofType('step').filter(e => e.step.id === id).map(e => e.step.state);
  assert.deepEqual(result.steps.map(s => s.kind), ['catalog', 'query', 'board']);
  for (const step of result.steps) assert.deepEqual(states(step.id), ['pending', 'running', 'done'], step.label);
  // the whole list is announced pending before the first one runs
  const firstRunning = rec.events.findIndex(e => e.type === 'step' && e.step.state === 'running');
  const pendingBefore = rec.events.slice(0, firstRunning).filter(e => e.type === 'step' && e.step.state === 'pending');
  assert.equal(pendingBefore.length, 2);
  assert.match(result.steps[1].label, /Queried Orders/);
  assert.equal(result.steps[2].label, 'Created the board · r1');
});

test('a turn that builds a board keeps one revision per save and marks the new parts', async () => {
  const world = makeWorld({ model: firstTurn() });
  const rec = recorder();
  const assistant = createAssistant(world.assistantDeps({ emit: rec.emit }));
  const result = await assistant.send('Show monthly revenue.');

  assert.deepEqual(result.revision, { from: 0, to: 1 });
  assert.deepEqual(result.changed, { revenue: 'new', trend: 'new' });
  assert.equal(result.reply, 'Done — a revenue KPI and a monthly line.');
  const boardEvent = rec.ofType('board').at(-1);
  assert.equal(boardEvent.revision, 1);
  assert.deepEqual(boardEvent.changed, { revenue: 'new', trend: 'new' });
  // the first save made the board, the host now holds r1
  assert.deepEqual(world.boardWrites().map(r => r.method), ['POST']);
  assert.equal(world.host.revision, 1);
});

test('the raw call of a step is the request that was sent, character for character', async () => {
  const world = makeWorld({ model: firstTurn() });
  const assistant = createAssistant(world.assistantDeps());
  const result = await assistant.send('Show monthly revenue.');

  const query = result.steps.find(s => s.kind === 'query');
  const sent = world.dataRequests();
  assert.equal(sent.length, 1);
  assert.equal(query.call.url, sent[0].url);
  assert.equal(query.raw, `GET ${sent[0].url}`);
  assert.equal(sent[0].url, '/$data/Orders?$apply=groupby((OrderDate),aggregate(Total with sum as Revenue))');

  const catalog = result.steps.find(s => s.kind === 'catalog');
  assert.equal(catalog.raw, `GET ${DEFAULT_ENDPOINTS.catalog}`);
  assert.deepEqual(world.requests.filter(r => r.url === DEFAULT_ENDPOINTS.catalog).length, 1);

  // the patch shown is the patch the store applied: the document the host kept has exactly those parts
  const board = result.steps.find(s => s.kind === 'board');
  assert.equal(board.raw, `patch_board ${JSON.stringify(FIRST_PATCH)}`);
  assert.deepEqual(Object.keys(world.host.document.parts), ['revenue', 'trend']);
  assert.equal(world.host.document.data.kpi.path, FIRST_PATCH[0].value.kpi.path);
});

test('the page never names the model, and every host request carries the signed-in person', async () => {
  const world = makeWorld({ model: firstTurn() });
  const assistant = createAssistant(world.assistantDeps());
  await assistant.send('Show monthly revenue.');

  const requests = world.modelRequests();
  assert.equal(requests.length, 3);
  for (const request of requests) {
    assert.equal(request.url, '/uboard/model/chat/completions');
    assert.equal(request.method, 'POST');
    assert.equal(Object.hasOwn(request.body, 'model'), false, 'no model field in the body');
    assert.deepEqual(Object.keys(request.body).sort(), ['messages', 'tool_choice', 'tools']);
    assert.ok(request.body.tools.length >= 5);
  }
  assert.equal(world.requests.length, 6); // three model calls, the catalog, one read, one save
  for (const request of world.requests) assert.equal(request.headers['X-Sample-User'], 'dev', request.url);
});

test('the model is offered the board tools of U-Board and this sample\'s two data tools', async () => {
  const world = makeWorld({ model: [say('Hello.')] });
  const assistant = createAssistant(world.assistantDeps());
  await assistant.send('Hi');
  const names = world.modelRequests()[0].body.tools.map(tool => tool.function.name);
  for (const name of ['patch_board', 'get_board', 'validate', 'describe', 'read_catalog', 'query_data']) assert.ok(names.includes(name), name);
  const system = world.modelRequests()[0].body.messages[0];
  assert.equal(system.role, 'system');
  assert.match(system.content, /Revision 0/);
});

test('a model that never stops calling tools is stopped after the most rounds it may take', async () => {
  const forever = Array.from({ length: 10 }, () => calls(['get_board', {}]));
  const world = makeWorld({ model: forever });
  const assistant = createAssistant(world.assistantDeps({ maxRounds: 4 }));
  const result = await assistant.send('Keep looking.');

  assert.equal(result.outcome, 'limit');
  assert.equal(result.failure.kind, 'limit');
  assert.equal(world.modelRequests().length, 4);
  assert.match(result.reply, /stopped after 4 steps/);
  assert.equal(world.queue.length, 6);
});

test('a read that matches no rows is an empty result, not an error', async () => {
  const world = makeWorld({
    odata: () => json(200, { value: [], '@odata.count': 0 }),
    model: [
      calls(['query_data', { entity: 'Orders', filter: "Status eq 'Refunded'", top: 50, count: true }]),
      say('There are no refunded orders. I can widen the range.'),
    ],
  });
  const rec = recorder();
  const assistant = createAssistant(world.assistantDeps({ emit: rec.emit }));
  const result = await assistant.send('Show refunded orders.');

  const query = result.steps.find(s => s.kind === 'query');
  assert.equal(query.state, 'done');
  assert.equal(query.outcome, 'empty');
  assert.match(query.detail, /0 rows/);
  assert.deepEqual(result.emptyResults, [query.id]);
  assert.equal(result.outcome, 'empty');
  assert.equal(result.failure, null);
  // the model was told it is not an error
  const toolMessage = world.modelRequests()[1].body.messages.find(m => m.role === 'tool' && m.content.includes('empty'));
  assert.match(toolMessage.content, /not an error/);
});

test('a model service that refuses ends the turn with a structured failure and nothing changed', async () => {
  const world = makeWorld({ modelStatus: 429 });
  const assistant = createAssistant(world.assistantDeps());
  const result = await assistant.send('Hi');
  assert.equal(result.outcome, 'error');
  assert.deepEqual({ kind: result.failure.kind, status: result.failure.status }, { kind: 'model', status: 429 });
  assert.match(result.reply, /busy/);
  assert.equal(world.boardWrites().length, 0);
});

test('arguments that are not JSON, and tools that do not exist, come back to the model as errors without stopping the turn', async () => {
  const world = makeWorld({
    model: [
      { choices: [{ message: { role: 'assistant', content: null, tool_calls: [
        { id: 'a', type: 'function', function: { name: 'patch_board', arguments: '{"patch": [' } },
        { id: 'b', type: 'function', function: { name: 'make_coffee', arguments: '{}' } },
      ] } }] },
      say('Sorry, I will not try that.'),
    ],
  });
  const assistant = createAssistant(world.assistantDeps());
  const result = await assistant.send('Do things.');
  assert.equal(result.outcome, 'done');
  assert.deepEqual(result.steps.map(s => s.state), ['done', 'error', 'error']); // the first is the catalog the loop read before the edit
  const replies = world.modelRequests()[1].body.messages.filter(m => m.role === 'tool');
  assert.equal(replies.length, 2);
  assert.match(replies[0].content, /not valid JSON/);
  assert.match(replies[1].content, /no tool called make_coffee/);
});

test('the revision the model passes is ignored: the host keeps count', async () => {
  const world = makeWorld({
    model: [calls(['patch_board', { revision: 7, patch: FIRST_PATCH }]), say('Done.')],
  });
  const assistant = createAssistant(world.assistantDeps());
  const result = await assistant.send('Build it.');
  assert.equal(result.outcome, 'done');
  assert.equal(result.revision.to, 1);
});
