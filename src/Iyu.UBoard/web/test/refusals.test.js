import test from 'node:test';
import assert from 'node:assert/strict';
import { createAssistant } from '../src/agent-loop.js';
import { FIRST_PATCH, badPatch, calls, makeWorld, recorder, say } from '../testkit/world.js';

// A world whose board is already at r1: one turn builds it.
async function withBoard(extra = []) {
  const world = makeWorld({ model: [calls(['patch_board', { patch: FIRST_PATCH }]), say('Built.'), ...extra] });
  const rec = recorder();
  const assistant = createAssistant(world.assistantDeps({ emit: rec.emit }));
  await assistant.send('Build the board.');
  return { world, assistant, rec };
}

test('the board check refuses an edit twice: one retry with the reason, then nothing is saved and the person is told where', async () => {
  const { world, assistant } = await withBoard([
    calls(['patch_board', { patch: badPatch('mix') }]),
    calls(['patch_board', { patch: badPatch('mix2') }]),
    calls(['patch_board', { patch: badPatch('mix3') }]), // a third attempt must never be asked for
    say('unreachable'),
  ]);
  const writesBefore = world.boardWrites().length;
  const result = await assistant.send('Add a 3D donut.');

  assert.equal(result.outcome, 'rejected');
  assert.equal(result.failure.kind, 'rejected');
  assert.match(result.failure.issues[0].path, /^\/parts\/mix2\/as$/);
  assert.match(result.failure.issues[0].message, /unknown kind/);
  assert.ok(result.failure.suggestion.length > 0);
  assert.match(result.reply, /Nothing was saved/);
  assert.match(result.reply, /\/parts\/mix2\/as/);
  // nothing reached the host, and the board is where it was
  assert.equal(world.boardWrites().length, writesBefore);
  assert.equal(result.revision.from, 1);
  assert.equal(result.revision.to, 1);
  assert.deepEqual(Object.keys(result.document.parts), ['revenue', 'trend']);
  // exactly one retry: the model was asked twice in this turn, and the third scripted reply was never taken
  assert.equal(world.modelRequests().length, 2 + 2); // two for the first turn, two for this one
  assert.equal(world.queue.length, 2);
  const labels = result.steps.map(s => s.label);
  assert.deepEqual(labels, ['Edited the board', 'Retried once', 'Nothing was saved']); // the catalog was read in the session's first turn and is kept
  assert.equal(result.steps[0].outcome, 'rejected');
  assert.equal(result.steps[1].outcome, 'rejected');
  assert.equal(result.steps[1].state, 'error');
});

test('the reason of the first refusal is what the model reads before its single retry', async () => {
  const { world, assistant } = await withBoard([
    calls(['patch_board', { patch: badPatch('mix') }]),
    calls(['patch_board', { patch: [
      { op: 'add', path: '/parts/mix', value: { as: 'chart.bar', of: 'monthly', title: 'By month', x: 'OrderDate', y: 'Revenue' } },
      { op: 'add', path: '/layout/items/-', value: 'mix' },
    ] }]),
    say('Added a bar chart instead.'),
  ]);
  const result = await assistant.send('Add a 3D donut.');

  assert.equal(result.outcome, 'done');
  assert.equal(result.revision.to, 2);
  const retry = world.modelRequests().at(-2).body.messages.filter(m => m.role === 'tool').at(-1);
  assert.match(retry.content, /INVALID_DOCUMENT/);
  assert.match(retry.content, /unknown kind/);
  assert.deepEqual(result.changed, { mix: 'new' });
  assert.equal(result.steps[0].outcome, 'rejected');
  assert.equal(result.steps[1].label, 'Edited the board · r2'); // the retry that was kept is named by the revision it made
});

test('a patch that is not a patch counts as a refusal too, and also gets only one retry', async () => {
  const { assistant, world } = await withBoard([
    calls(['patch_board', { patch: [{ op: 'replace', path: '/parts/nothere/title', value: 'x' }] }]),
    calls(['patch_board', { patch: [{ op: 'remove', path: '/parts/also-not-here' }] }]),
    say('unreachable'),
  ]);
  const result = await assistant.send('Rename a tile that is not there.');
  assert.equal(result.outcome, 'rejected');
  assert.equal(world.boardWrites().length, 1);
  assert.equal(world.queue.length, 1);
});

test('a save the host refuses because the board moved on is a structured conflict that says what was not saved', async () => {
  const { world, assistant } = await withBoard([
    calls(['patch_board', { patch: [{ op: 'replace', path: '/parts/revenue/title', value: 'Revenue YTD' }] }]),
    say('unreachable'),
  ]);
  world.moveOn(); // another session saved r2
  const result = await assistant.send('Rename the KPI.');

  assert.equal(result.outcome, 'conflict');
  assert.equal(result.failure.kind, 'conflict');
  assert.equal(result.failure.serverRevision, 2);
  assert.equal(result.failure.workedFrom, 1);
  assert.equal(result.failure.offer, 'reload');
  assert.equal(result.failure.notSaved.tool, 'patch_board');
  assert.deepEqual(result.failure.notSaved.args.patch, [{ op: 'replace', path: '/parts/revenue/title', value: 'Revenue YTD' }]);
  assert.deepEqual(result.failure.notSaved.parts, ['revenue']);
  assert.equal(result.failure.board.revision, 2);
  assert.match(result.failure.retryPrompt, /Rename the KPI/);
  assert.match(result.reply, /wasn't saved/);
  // the store put its board back: this session is still at r1 with the old title, and the step shows the refused request
  assert.equal(result.revision.to, 1);
  assert.equal(result.document.parts.revenue.title, 'Revenue');
  const step = result.steps.find(s => s.kind === 'board');
  assert.equal(step.outcome, 'conflict');
  assert.match(step.raw, /^PUT \/uboard\/boards\/revenue\?expected=1 → 409 Conflict$/);
  assert.equal(world.queue.length, 1);
});

test('after a conflict the person can reload the host\'s board and the same assistant carries on from it', async () => {
  const { world, assistant } = await withBoard([
    calls(['patch_board', { patch: [{ op: 'replace', path: '/parts/revenue/title', value: 'Revenue YTD' }] }]),
    calls(['patch_board', { patch: [{ op: 'replace', path: '/parts/revenue/title', value: 'Revenue YTD' }] }]),
    say('Renamed on top of r2.'),
  ]);
  world.moveOn();
  const conflict = (await assistant.send('Rename the KPI.')).failure;
  assistant.reload(conflict.board);
  assert.equal(assistant.revision, 2);

  // the host's envelope carries the document the other session left; here it is the one r1 had
  const again = await assistant.send(conflict.retryPrompt);
  assert.equal(again.outcome, 'done');
  assert.equal(again.revision.from, 2);
  assert.equal(again.revision.to, 3);
  assert.equal(world.host.document.parts.revenue.title, 'Revenue YTD');
  assert.ok(assistant.messages.length > 6, 'the conversation is kept across the reload');
});

test('a host that refuses a save for another reason is reported as a failed save, not as a conflict', async () => {
  const { world, assistant } = await withBoard([calls(['patch_board', { patch: [{ op: 'replace', path: '/parts/revenue/title', value: 'X' }] }]), say('unreachable')]);
  const fetch = world.fetch;
  const failing = createAssistant({ ...world.assistantDeps(), fetch: async (url, init) => (init?.method === 'PUT' ? { ok: false, status: 500, json: async () => ({}) } : fetch(url, init)), board: { id: 'revenue', loaded: { name: 'Revenue overview', revision: 1, document: world.host.document } } });
  const result = await failing.send('Rename.');
  assert.equal(result.failure.kind, 'save-failed');
  assert.equal(result.failure.status, 500);
  assert.equal(result.revision.to, 1);
  void assistant;
});
