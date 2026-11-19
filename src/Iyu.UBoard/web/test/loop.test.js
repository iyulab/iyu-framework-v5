import test from 'node:test';
import assert from 'node:assert/strict';
import { createAssistant, DEFAULT_ENDPOINTS, newBoard } from '../src/agent-loop.js';
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

test('the page never names the model and sends nothing but a turn', async () => {
  const world = makeWorld({ model: firstTurn() });
  const assistant = createAssistant(world.assistantDeps());
  await assistant.send('Show monthly revenue.');

  const requests = world.requests.filter(r => r.url === '/uboard/boards/revenue/turn');
  assert.equal(requests.length, 3);
  for (const request of requests) {
    assert.equal(request.method, 'POST');
    // Neither the model, nor its key, nor a limit on its reply: the host holds those. The page sends the conversation, the rules text and its tools.
    assert.deepEqual(Object.keys(request.body).filter(key => !['system', 'history', 'tools', 'message', 'results'].includes(key)), [], 'nothing but what a turn is');
    for (const field of ['model', 'max_tokens', 'max_completion_tokens', 'temperature', 'stream', 'reasoning_effort']) assert.equal(Object.hasOwn(request.body, field), false, `no ${field} in the body`);
    assert.ok(request.body.tools.length >= 5);
    assert.ok(request.body.tools.every(tool => Object.keys(tool).sort().join() === 'description,edits,name,schema'), 'a tool is a name, words, a schema and whether it edits');
    assert.deepEqual(request.body.tools.filter(tool => tool.edits).map(tool => tool.name), ['patch_board'], 'only the board edit declares that it edits');
  }
  assert.equal(world.requests.length, 6); // three model calls, the catalog, one read, one save
});

test('the model is offered the board tools of U-Board and the two data tools', async () => {
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

test('a turn the page ends itself is closed on the host, so the next turn starts from a whole conversation', async () => {
  // The cap ends the turn with the last answer's tool calls run and never sent back; the failure the person reads is recorded with them.
  const world = makeWorld({ model: [...Array.from({ length: 2 }, () => calls(['get_board', {}])), say('Here is the board.')] });
  const assistant = createAssistant(world.assistantDeps({ maxRounds: 2 }));
  const first = await assistant.send('Keep looking.');
  assert.equal(first.outcome, 'limit');

  const closing = world.requests.filter(r => r.url === '/uboard/boards/revenue/turn').at(-1).body;
  assert.ok(closing.results.length > 0 && closing.final === first.reply, 'the results owed and the words said go to the host together');
  assert.equal(world.modelRequests().length, 2, 'closing does not ask the model');

  const second = await assistant.send('Try again.'); // the host refuses a history that holds a call without its result
  assert.equal(second.outcome, 'done');
  assert.match(world.modelRequests().at(-1).body.messages.at(-2).content, /stopped after 2 steps/);
});

test('pressing stop ends the turn where it stands, says so, and leaves the conversation whole', async () => {
  const world = makeWorld({ model: [calls(['read_catalog', {}], ['query_data', QUERY]), calls(['patch_board', { patch: FIRST_PATCH }]), say('Done.'), say('Here you are.')] });
  const stop = new AbortController();
  const rec = recorder();
  // the person presses stop while the first read is running
  const deps = world.assistantDeps({ emit: event => { rec.emit(event); if (event.type === 'step' && event.step.kind === 'catalog' && event.step.state === 'running') stop.abort(); } });
  const assistant = createAssistant(deps);
  const first = await assistant.send('Show monthly revenue.', { signal: stop.signal });

  assert.equal(first.outcome, 'stopped');
  assert.match(first.reply, /^Stopped\. The board is at r0 — nothing was saved\./);
  assert.deepEqual(first.steps.map(s => s.outcome), [null, 'skipped'], 'the step that was running ends, the next is not started');
  assert.equal(world.boardWrites().length, 0, 'nothing was saved');
  assert.equal(world.modelRequests().length, 1, 'the model is not asked again');

  const second = await assistant.send('Try again.'); // the host would refuse a conversation that held a call without its result
  assert.equal(second.outcome, 'done');
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

// ---- what the person may do, and what the host has switched on

const offered = world => world.modelRequests()[0].body.tools.map(tool => tool.function.name);

test('a person who may not edit the board is offered no tool that edits it, and the model is told to answer', async () => {
  const world = makeWorld({ model: [say('The board shows revenue.')] });
  const assistant = createAssistant(world.assistantDeps({ canEdit: false }));
  const result = await assistant.send('What does this board show?');

  const names = offered(world);
  assert.equal(names.includes('patch_board'), false);
  for (const name of ['get_board', 'read_catalog', 'query_data']) assert.ok(names.includes(name), name);
  const sent = world.requests.find(r => r.url === '/uboard/boards/revenue/turn').body;
  assert.equal(sent.tools.some(tool => tool.edits), false, 'nothing declared as an edit');
  assert.match(world.modelRequests()[0].body.messages[0].content, /may open and read it but not change it/);
  assert.equal(result.outcome, 'done');
  assert.deepEqual(result.revision, { from: 0, to: 0 });
});

test('a host with no source catalog gets no data tools, and a board edit is not held to a catalog nobody can read', async () => {
  const world = makeWorld({ model: [calls(['patch_board', { patch: [{ op: 'add', path: '/parts/note', value: { as: 'label', title: 'Hello' } }, { op: 'add', path: '/layout/items/-', value: 'note' }] }]), say('Added a note.')] });
  const assistant = createAssistant(world.assistantDeps({ catalog: false }));
  const result = await assistant.send('Add a note.');

  const names = offered(world);
  assert.equal(names.includes('read_catalog') || names.includes('query_data'), false);
  assert.equal(world.requests.some(r => r.url === DEFAULT_ENDPOINTS.catalog), false, 'the catalog is never asked for');
  assert.equal(result.outcome, 'done');
  assert.equal(world.host.revision, 1);
});

test('a board id with characters a path does not allow is one segment of every address', async () => {
  const world = makeWorld({ model: [say('Hi.')] });
  const seen = [];
  const assistant = createAssistant({ ...world.assistantDeps({ board: { id: 'a b/c?d', loaded: newBoard('Odd') } }), fetch: (url, init) => { seen.push(url); return world.fetch(url, init); } });
  await assistant.send('Hello.').catch(() => {});

  assert.ok(seen.every(url => url.startsWith('/uboard/boards/a%20b%2Fc%3Fd')), seen.join(', '));
});
