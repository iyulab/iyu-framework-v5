import test from 'node:test';
import assert from 'node:assert/strict';
import { createAssistant } from '../src/agent-loop.js';
import { buildODataUrl, bulkProblem, closest, fieldPaths, parseODataUrl, permissionProblem } from '../src/odata.js';
import { CATALOG, CATALOG_PLAIN, FIRST_PATCH, calls, json, makeWorld, recorder, say } from '../testkit/world.js';

const ask = async (world, text = 'Do it.') => createAssistant(world.assistantDeps()).send(text);

// ---------------------------------------------------------------- permission: stop before any request

test('a field the person cannot read stops the turn before any read, and names an alternative', async () => {
  const world = makeWorld({
    model: [calls(['read_catalog', {}], ['query_data', { entity: 'Orders', apply: 'groupby((Id),aggregate(Margin with sum as M))' }]), say('unreachable')],
  });
  const result = await ask(world, 'Show margin by product.');

  assert.equal(world.dataRequests().length, 0, 'no OData request left the page');
  assert.equal(result.outcome, 'blocked');
  assert.equal(result.failure.kind, 'permission');
  assert.equal(result.failure.field, 'Orders.Margin');
  assert.equal(result.failure.reason, 'locked');
  assert.ok(result.failure.alternatives.includes('Orders.Total'));
  assert.ok(!result.failure.alternatives.includes('Orders.Margin'));
  assert.match(result.reply, /Orders\.Margin is hidden by a read policy/);
  assert.match(result.reply, /Nothing was read or changed/);
  const stop = result.steps.at(-1);
  assert.equal(stop.label, 'Stopped before querying');
  assert.equal(stop.outcome, 'blocked');
  assert.equal(world.modelRequests().length, 1, 'the model is not asked again');
});

test('with the host\'s locked-field display off, a hidden field is simply unknown and stops the same way', async () => {
  const world = makeWorld({ catalog: CATALOG_PLAIN, model: [calls(['query_data', { entity: 'Orders', select: ['Margin'], top: 10 }])] });
  const result = await ask(world);
  assert.equal(world.dataRequests().length, 0);
  assert.deepEqual({ field: result.failure.field, reason: result.failure.reason }, { field: 'Orders.Margin', reason: 'absent' });
  assert.match(result.reply, /not a field you can read/);
});

test('the catalog is read by the loop itself when the model goes straight to a query', async () => {
  const world = makeWorld({ model: [calls(['query_data', { entity: 'Orders', select: ['Margin'], top: 10 }])] });
  const result = await ask(world);
  assert.equal(world.requests.filter(r => r.url === '/uboard/catalog').length, 1);
  assert.equal(result.steps[0].label, 'Read the catalog');
  assert.equal(world.dataRequests().length, 0);
});

test('a field behind a link is checked too, and so is an entity that is not in the catalog', async () => {
  const through = permissionProblem(CATALOG, { entity: 'Orders', apply: 'groupby((Customer/Email),aggregate($count as N))' });
  assert.equal(through.field, 'Orders.Customer/Email');
  assert.equal(through.reason, 'locked');
  assert.deepEqual(through.alternatives.slice(0, 2), ['Customers.Id', 'Customers.Name']);
  assert.equal(permissionProblem(CATALOG, { entity: 'Orders', apply: 'groupby((Customer/Name),aggregate(Total with sum as T))' }), undefined);
  const missing = permissionProblem(CATALOG, { entity: 'Salaries', top: 5 });
  assert.deepEqual({ field: missing.field, reason: missing.reason }, { field: 'Salaries', reason: 'entity-absent' });
  assert.ok(missing.alternatives.includes('Orders'));
  const expanded = permissionProblem(CATALOG, { entity: 'Orders', top: 5, expand: [{ link: 'Customer', select: ['Name', 'Email'] }] });
  assert.equal(expanded.field, 'Orders.Customer/Email');
});

test('the words of an OData expression are not mistaken for fields, and what a read names itself is not either', () => {
  assert.deepEqual(fieldPaths("Status eq 'Paid' and OrderDate ge 2026-01-01 and year(OrderDate) eq 2026"), ['Status', 'OrderDate']);
  assert.deepEqual(fieldPaths('groupby((Region/Name),aggregate(Total with sum as Revenue, $count as Orders))'), ['Region/Name', 'Total']);
  assert.deepEqual(fieldPaths("Name eq 'it''s Margin'"), ['Name']);
  assert.equal(permissionProblem(CATALOG, { entity: 'Orders', apply: 'groupby((Region/Name),aggregate(Total with sum as Revenue))', orderby: 'Revenue desc', top: 5 }), undefined);
  assert.equal(permissionProblem(CATALOG, { entity: 'Orders', filter: "Status eq 'Margin' and Total gt 10.5", top: 5 }), undefined);
});

test('a board that would read something the person cannot read is refused before it is saved', async () => {
  const hidden = [
    { op: 'add', path: '/data', value: { m: { from: 'odata', path: '/$data/Orders?$apply=groupby((Id),aggregate(Margin with sum as M))', list: '/value' } } },
    { op: 'add', path: '/parts/m', value: { as: 'chart.bar', of: 'm', x: 'Id', y: 'M' } },
    { op: 'add', path: '/layout/items/-', value: 'm' },
  ];
  const world = makeWorld({ model: [calls(['patch_board', { patch: hidden }])] });
  const result = await ask(world);
  assert.equal(result.outcome, 'blocked');
  assert.equal(result.failure.field, 'Orders.Margin');
  assert.equal(world.boardWrites().length, 0);
  assert.equal(world.dataRequests().length, 0);
});

// ---------------------------------------------------------------- large data: aggregate first, page the rest

test('a read with neither $apply nor $top is refused with advice and never sent', async () => {
  const world = makeWorld({ model: [calls(['query_data', { entity: 'Orders' }]), say('I will aggregate instead.')] });
  const result = await ask(world);
  assert.equal(world.dataRequests().length, 0);
  assert.equal(result.outcome, 'done');
  const advice = world.modelRequests()[1].body.messages.find(m => m.role === 'tool').content;
  assert.match(advice, /aggregate-first/);
  assert.match(advice, /\$apply=groupby/);
  assert.match(advice, /\$top=50&\$count=true/);
  assert.equal(result.steps.find(s => s.kind === 'query').outcome, 'refused');
});

test('a read asking for more than 100 rows is refused, and a $filter alone is not an aggregate', () => {
  assert.equal(bulkProblem({ entity: 'Orders', top: 5000 }).rule, 'top');
  assert.equal(bulkProblem({ entity: 'Orders', apply: 'groupby((Status),aggregate($count as N))', top: 101 }).rule, 'top');
  assert.equal(bulkProblem({ entity: 'Orders', apply: "filter(Status eq 'Paid')" }).rule, 'aggregate-first');
  assert.equal(bulkProblem({ entity: 'Orders', top: 100 }), undefined);
  assert.equal(bulkProblem({ entity: 'Orders', top: 0 }).rule, 'top');
  assert.equal(bulkProblem({ entity: 'Orders', apply: 'aggregate(Total with sum as T)' }), undefined);
});

test('a table reads one page of 50 rows with the count, as written, and the model gets at most 100 rows back', async () => {
  const rows = Array.from({ length: 50 }, (_, i) => ({ Id: i, Total: i * 2, '@odata.id': `Orders(${i})` }));
  const world = makeWorld({
    odata: () => json(200, { '@odata.count': 4218306, value: rows }),
    model: [calls(['query_data', { entity: 'Orders', orderby: 'OrderDate desc', top: 50, count: true }]), say('A table of the latest orders.')],
  });
  const result = await ask(world);
  assert.equal(world.dataRequests()[0].url, '/$data/Orders?$orderby=OrderDate desc&$top=50&$count=true');
  const query = result.steps.find(s => s.kind === 'query');
  assert.match(query.detail, /50 rows of 4,218,306/);
  const told = JSON.parse(world.modelRequests()[1].body.messages.find(m => m.role === 'tool').content);
  assert.equal(told.total, 4218306);
  assert.equal(told.rows.length, 50);
  assert.ok(!Object.hasOwn(told.rows[0], '@odata.id'));
});

test('an aggregate that returns very many groups is cut for the model, with the instruction to narrow it', async () => {
  const groups = Array.from({ length: 250 }, (_, i) => ({ Customer: `c${i}`, Revenue: i }));
  const world = makeWorld({
    odata: () => json(200, { value: groups }),
    model: [calls(['query_data', { entity: 'Orders', apply: 'groupby((Customer/Name),aggregate(Total with sum as Revenue))' }]), say('ok')],
  });
  await ask(world);
  const told = JSON.parse(world.modelRequests()[1].body.messages.find(m => m.role === 'tool').content);
  assert.equal(told.rows.length, 100);
  assert.equal(told.returned, 250);
  assert.match(told.truncated, /only the first 100 of 250/);
});

test('a board whose table would read every row is refused before it is saved; a paged one is kept', async () => {
  const board = path => [
    { op: 'add', path: '/data', value: { orders: { from: 'odata', path, list: '/value', key: 'Id' } } },
    { op: 'add', path: '/parts/latest', value: { as: 'table', of: 'orders', title: 'Latest orders', pageSize: 50 } },
    { op: 'add', path: '/layout/items/-', value: ['latest', 12] },
  ];
  const unbounded = makeWorld({ model: [calls(['patch_board', { patch: board('/$data/Orders') }]), say('I will page it.')] });
  const refused = await ask(unbounded);
  assert.equal(unbounded.boardWrites().length, 0);
  assert.equal(refused.outcome, 'done');
  assert.equal(refused.steps.find(s => s.kind === 'board').outcome, 'refused');
  assert.match(unbounded.modelRequests()[1].body.messages.find(m => m.role === 'tool').content, /without \$apply/);

  const paged = makeWorld({ model: [calls(['patch_board', { patch: board('/$data/Orders?$orderby=OrderDate desc&$top=50&$count=true') }]), say('Added.')] });
  const kept = await ask(paged);
  assert.equal(paged.boardWrites().length, 1);
  assert.deepEqual(kept.changed, { latest: 'new' });
});

test('arguments of query_data are checked: unknown names, a $top that is not a number, a path that is not an entity', async () => {
  const world = makeWorld({
    model: [
      calls(['query_data', { entity: 'Orders', url: '/$data/Orders' }], ['query_data', { entity: 'Orders/../Secrets', top: 5 }], ['query_data', { entity: 'Orders', top: '50' }]),
      say('I will fix them.'),
    ],
  });
  const result = await ask(world);
  assert.equal(world.dataRequests().length, 0);
  const replies = world.modelRequests()[1].body.messages.filter(m => m.role === 'tool').map(m => m.content);
  assert.match(replies[0], /unknown argument \\"url\\"/);
  assert.match(replies[1], /entity must be an entity set name/);
  assert.match(replies[2], /top must be a whole number/);
  assert.deepEqual(result.steps.filter(s => s.kind === 'query').map(s => s.state), ['error', 'error', 'error']);
});

// ---------------------------------------------------------------- reads written down and read back

test('a read built from arguments and the same read parsed from a board path are one query', () => {
  const query = { entity: 'Orders', apply: 'groupby((Region/Name),aggregate(Total with sum as Revenue))', orderby: 'Revenue desc', top: 5, count: true };
  const url = buildODataUrl(query);
  assert.equal(url, '/$data/Orders?$apply=groupby((Region/Name),aggregate(Total with sum as Revenue))&$orderby=Revenue desc&$top=5&$count=true');
  assert.deepEqual(parseODataUrl(url), { ...query, topText: '5' });
  assert.equal(parseODataUrl('/elsewhere/Orders'), undefined);
  assert.equal(buildODataUrl({ entity: 'Orders', filter: 'Name eq \'A&B\' and Total gt 1+1' }), "/$data/Orders?$filter=Name eq 'A%26B' and Total gt 1%2B1");
});

test('a «did you mean» comes from the closest name, and from nothing when nothing is near', () => {
  assert.equal(closest('NetTotal', ['Id', 'OrderDate', 'Total']), 'Total');
  assert.equal(closest('chart.lines', ['chart.bar', 'chart.line', 'chart.pie']), 'chart.line');
  assert.equal(closest('zzzzzzzz', ['Id', 'Total']), undefined);
});

test('the filter of a board is carried in each read: the diff marks every tile that reads it', async () => {
  const world = makeWorld({ model: [calls(['patch_board', { patch: FIRST_PATCH }]), say('Built.'), calls(['patch_board', { patch: [
    { op: 'replace', path: '/data/kpi/path', value: "/$data/Orders?$apply=filter(Status eq 'Paid')/aggregate(Total with sum as Revenue)" },
    { op: 'replace', path: '/data/monthly/path', value: "/$data/Orders?$apply=filter(Status eq 'Paid')/groupby((OrderDate),aggregate(Total with sum as Revenue))" },
  ] }]), say('Paid orders only.')] });
  const rec = recorder();
  const assistant = createAssistant(world.assistantDeps({ emit: rec.emit }));
  await assistant.send('Build.');
  const second = await assistant.send('Only paid orders.');
  assert.equal(second.outcome, 'done');
  assert.deepEqual(second.changed, { revenue: 'edited', trend: 'edited' });
  assert.equal(second.revision.to, 2);
});

// ---------------------------------------------------------------- the catalog reads, and the tools that are offered

test('every step that says it read the catalog sent one request, and a query never runs on a catalog nobody read', async () => {
  const world = makeWorld({
    model: [
      calls(['query_data', { entity: 'Orders', top: 5 }], ['read_catalog', {}]), // the query comes first: the loop reads the catalog before it
      calls(['read_catalog', {}], ['query_data', { entity: 'Orders', top: 5 }]),
      say('Done.'),
    ],
  });
  const assistant = createAssistant(world.assistantDeps());
  const result = await assistant.send('Look.');
  const catalogSteps = result.steps.filter(s => s.kind === 'catalog');
  assert.equal(catalogSteps.length, 3); // one the loop added, two the model asked for
  assert.equal(world.requests.filter(r => r.url === '/uboard/catalog').length, catalogSteps.length);
  assert.deepEqual(result.steps.map(s => s.kind), ['catalog', 'query', 'catalog', 'catalog', 'query']);
  assert.equal(world.dataRequests().length, 2);
});

test('edit_text is not offered: a read\'s path cannot be changed behind the read rules', async () => {
  const world = makeWorld({ model: [say('Hi.')] });
  await createAssistant(world.assistantDeps()).send('Hi');
  const names = world.modelRequests()[0].body.tools.map(tool => tool.function.name).sort();
  assert.deepEqual(names, ['describe', 'get_board', 'patch_board', 'query_data', 'read_catalog', 'validate']);
});

test('an entity the host lists as not readable is not one to read', () => {
  const closed = { entities: [{ name: 'Salaries', readable: false, properties: [{ name: 'Amount', type: 'decimal', nullable: false, readable: true }], links: [] }, ...CATALOG.entities] };
  const problem = permissionProblem(closed, { entity: 'Salaries', top: 5 });
  assert.equal(problem.reason, 'entity-absent');
  assert.ok(problem.alternatives.includes('Orders') && !problem.alternatives.includes('Salaries'));
});

test('a board that already exists on the host answers the first save with 409 and its own board: a conflict, not a failure', async () => {
  const world = makeWorld({ model: [calls(['patch_board', { patch: FIRST_PATCH }]), say('unreachable')] });
  world.host.revision = 3; // somebody made the board while this page believed there was none
  const result = await ask(world, 'Build it.');
  assert.equal(result.failure.kind, 'conflict');
  assert.equal(result.failure.serverRevision, 3);
  assert.match(result.steps.find(s => s.kind === 'board').raw, /^POST \/uboard\/boards\/revenue\?name=Revenue%20overview → 409 Conflict$/);
});
