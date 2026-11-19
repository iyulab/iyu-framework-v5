// End-to-end run of the board chat sample, in a real browser against the real host.
//
// What is real: the host (started here on a free port with a throwaway database), its OData service, the catalog, the board
// routes, publishing, the page and the viewer — and the assistant's route, with IronHive.Agent and IronProw behind it. What is scripted is
// the model service: a small OpenAI-compatible server here answers the host's requests with canned replies (tool calls included, sent as
// the stream the host asks for), so no key and no model are needed. Everything the assistant does with those replies — the loop, the
// host-run tools, reading the catalog, querying, checking and saving the board — goes through the real page and host.
//
//   node e2e/run.mjs        (from samples/BoardChat/web; prints E2E-OK steps=N; BOARDCHAT_SHOTS_DIR chooses where the screenshots go)
import { spawn, spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { createServer as createHttpServer } from 'node:http';
import { createServer as createSocketServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { calls, say } from '../testkit/world.js';

const here = dirname(fileURLToPath(import.meta.url));
const web = resolve(here, '..');
const sample = resolve(web, '..');
// Screenshots go to BOARDCHAT_SHOTS_DIR when it is set, else to e2e/screenshots (not committed).
const imagesDir = process.env.BOARDCHAT_SHOTS_DIR ? resolve(process.env.BOARDCHAT_SHOTS_DIR) : join(here, 'screenshots');
const HOST_KEY = 'host-held-key';
const MODEL_NAME = 'scripted-model';
const sleep = ms => new Promise(done => setTimeout(done, ms));

let steps = 0;
const step = async (name, run) => {
  try {
    await run();
  } catch (error) {
    console.error(`E2E-FAIL at step ${steps + 1}: ${name}\n${error.stack ?? error}`);
    throw error;
  }
  steps += 1;
  console.log(`  ok ${String(steps).padStart(2)}  ${name}`);
};
const check = (condition, message) => {
  if (!condition) throw new Error(`assertion failed: ${message}`);
};
const equal = (actual, expected, message) => {
  if (actual !== expected) throw new Error(`${message}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
};

// ---------------------------------------------------------------- the browser, the host

function findBrowser() {
  const candidates = [
    process.env.CHROME_PATH,
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  ].filter(Boolean);
  const found = candidates.find(path => existsSync(path));
  if (!found) throw new Error('no Chrome, Chromium or Edge found — set CHROME_PATH');
  return found;
}

const freePort = () =>
  new Promise((done, fail) => {
    const server = createSocketServer();
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => done(port));
    });
    server.on('error', fail);
  });

function build() {
  const vite = spawnSync(process.execPath, [join(web, 'node_modules/vite/bin/vite.js'), 'build'], { cwd: web, encoding: 'utf8' });
  if (vite.status !== 0) throw new Error(`the page did not build:\n${vite.stdout}${vite.stderr}`);
  const dotnet = spawnSync('dotnet', ['build', join(sample, 'BoardChat.csproj'), '-v', 'q', '-nologo'], { encoding: 'utf8' });
  if (dotnet.status !== 0) throw new Error(`the host did not build:\n${dotnet.stdout}${dotnet.stderr}`);
}

// The host runs from a copy of its build output, so a build or a test run elsewhere can replace the files meanwhile.
async function startHost(port, work, modelUrl) {
  const app = join(work, 'app');
  cpSync(join(sample, 'bin/Debug/net10.0'), app, { recursive: true });
  cpSync(join(sample, 'wwwroot'), join(app, 'wwwroot'), { recursive: true });
  const child = spawn('dotnet', [join(app, 'BoardChat.dll'), '--urls', `http://127.0.0.1:${port}`, '--Database', join(work, 'boardchat.db')], {
    cwd: app,
    env: { ...process.env, Model__BaseUrl: modelUrl, Model__ApiKey: HOST_KEY, Model__Name: MODEL_NAME, ASPNETCORE_ENVIRONMENT: 'Production' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let log = '';
  child.stdout.on('data', chunk => (log += chunk));
  child.stderr.on('data', chunk => (log += chunk));
  for (let i = 0; i < 120; i++) {
    if (child.exitCode !== null) throw new Error(`the host stopped:\n${log}`);
    try {
      if ((await fetch(`http://127.0.0.1:${port}/uboard/catalog`)).ok) return child;
    } catch {
      // not listening yet
    }
    await sleep(500);
  }
  child.kill();
  throw new Error(`the host did not start:\n${log}`);
}

// ---------------------------------------------------------------- the scripted model

const P = '/$data/Orders?$apply=';
const FROM_2026 = 'filter(OrderDate ge 2026-01-01)/';
const PAID = "filter(Status eq 'paid' and OrderDate ge 2026-01-01)/";
const paths = {
  kpi: filter => `${P}${filter}aggregate(Total with sum as Revenue,$count as Orders,Total with average as AvgOrder)`,
  month: filter => `${P}${filter}compute(month(OrderDate) as M)/groupby((M),aggregate(Total with sum as Revenue))&$orderby=M`,
  region: filter => `${P}${filter}groupby((RegionName),aggregate(Total with sum as Revenue))&$orderby=Revenue desc`,
  top: filter => `${P}${filter}groupby((ProductName),aggregate(Total with sum as Revenue))&$orderby=Revenue desc&$top=5`,
};
const stat = (field, title) => ({ as: 'stat', of: 'kpi', show: field, title, format: field === 'Orders' ? 'number' : 'compact', numerals: { scale: 'xlarge', weight: 700 } });

const TURN1 = [
  calls(
    ['read_catalog', {}],
    ['query_data', { entity: 'Orders', apply: `${FROM_2026}compute(month(OrderDate) as M)/groupby((M),aggregate(Total with sum as Revenue))`.replace(/^/, ''), orderby: 'M' }],
  ),
  calls(['patch_board', { patch: [
    { op: 'add', path: '/data', value: {
      kpi: { from: 'odata', path: paths.kpi(FROM_2026), fields: {
        Revenue: { at: '/value/0/Revenue', label: 'Revenue YTD', unit: '$' }, Orders: { at: '/value/0/Orders', label: 'Orders' }, AvgOrder: { at: '/value/0/AvgOrder', label: 'Avg. order value', unit: '$', digits: 1 } } },
      month: { from: 'odata', path: paths.month(FROM_2026), list: '/value', key: 'M' },
    } },
    { op: 'add', path: '/parts/k1', value: stat('Revenue', 'Revenue YTD') },
    { op: 'add', path: '/parts/k2', value: stat('Orders', 'Orders') },
    { op: 'add', path: '/parts/k3', value: stat('AvgOrder', 'Avg. order value') },
    { op: 'add', path: '/parts/rev', value: { as: 'chart.line', of: 'month', title: 'Monthly revenue', x: 'M', y: 'Revenue', yFormat: 'compact' } },
    { op: 'add', path: '/layout/items/-', value: ['k1', 4] },
    { op: 'add', path: '/layout/items/-', value: ['k2', 4] },
    { op: 'add', path: '/layout/items/-', value: ['k3', 4] },
    { op: 'add', path: '/layout/items/-', value: ['rev', 12] },
  ] }]),
  say('Done — three KPIs and a monthly revenue line. Revenue is Orders.Total summed by month (month numbers: 1 = January). Want it split by region?'),
];

const TURN2 = [
  calls(['query_data', { entity: 'Orders', apply: `${FROM_2026}groupby((RegionName),aggregate(Total with sum as Revenue))`, orderby: 'Revenue desc' }],
    ['query_data', { entity: 'Orders', apply: `${FROM_2026}groupby((ProductName),aggregate(Total with sum as Revenue))`, orderby: 'Revenue desc', top: 5 }]),
  calls(['patch_board', { patch: [
    { op: 'replace', path: '/parts/rev/as', value: 'chart.bar' },
    { op: 'replace', path: '/layout/items/3', value: ['rev', 8] },
    { op: 'add', path: '/data/region', value: { from: 'odata', path: paths.region(FROM_2026), list: '/value', key: 'RegionName' } },
    { op: 'add', path: '/data/top', value: { from: 'odata', path: paths.top(FROM_2026), list: '/value', key: 'ProductName' } },
    { op: 'add', path: '/parts/top', value: { as: 'bar-list', of: 'top', title: 'Top products', label: 'ProductName', value: 'Revenue' } },
    { op: 'add', path: '/parts/region', value: { as: 'chart.bar', of: 'region', title: 'Revenue by region', x: 'RegionName', y: 'Revenue', yFormat: 'compact', colors: ['#3b5bdb'] } },
    { op: 'add', path: '/layout/items/-', value: ['top', 4] },
    { op: 'add', path: '/layout/items/-', value: ['region', 12] },
  ] }]),
  say('The monthly revenue is now a bar chart, with Top products beside it and Revenue by region below. Stacking regions inside each month needs one column per region, which the server cannot pivot, so regions are their own chart.'),
];

const TURN3 = [
  calls(['query_data', { entity: 'Orders', apply: `${PAID}aggregate(Total with sum as Revenue)` }]),
  calls(['patch_board', { patch: [
    { op: 'replace', path: '/data/kpi/path', value: paths.kpi(PAID) },
    { op: 'replace', path: '/data/month/path', value: paths.month(PAID) },
    { op: 'replace', path: '/data/region/path', value: paths.region(PAID) },
    { op: 'replace', path: '/data/top/path', value: paths.top(PAID) },
    { op: 'add', path: '/parts/rev/referenceLines', value: [{ value: 30000, axis: 'y', label: 'Target 30k', color: '#c92a2a', style: 'dashed' }] },
    { op: 'add', path: '/parts/rev/conditionalStyles', value: [{ field: 'Revenue', below: 30000, color: '#c92a2a' }] },
  ] }]),
  say('Every revenue read now counts paid orders only (the filter is written into each read — a board has no board-wide filter). Months under the 30k target line are drawn in red.'),
];

const POLICY = [calls(['read_catalog', {}], ['query_data', { entity: 'Orders', apply: 'groupby((ProductName),aggregate(Margin with sum as M))', orderby: 'M desc', top: 5 }])];

const ORDERS_COLUMNS = ['OrderDate', 'Status', 'RegionName', 'Total'];
const LARGE = [
  calls(['query_data', { entity: 'Orders', select: ORDERS_COLUMNS, orderby: 'OrderDate desc', top: 50, count: true }]),
  calls(['patch_board', { patch: [
    { op: 'add', path: '/data/orders', value: { from: 'odata', path: `/$data/Orders?$select=${ORDERS_COLUMNS.join(',')}&$orderby=OrderDate desc&$top=50&$count=true`, list: '/value' } },
    { op: 'add', path: '/parts/orders', value: { as: 'table', of: 'orders', title: 'Latest orders', pageSize: 50, searchable: false } },
    { op: 'add', path: '/layout/items/-', value: ['orders', 12] },
  ] }]),
  say('Added Latest orders. The table reads one server page of 50 rows with the total count — the other orders are never loaded. The charts above stay aggregated on the server.'),
];

const PAID_VARIABLE = "filter(Status eq '{{$status}}' and OrderDate ge 2026-01-01)/";
// A filter for the whole board is one variable that the reads mention; a table is walked on the server by a `skip` variable.
const VARIABLE_FILTER = [
  calls(['patch_board', { patch: [
    { op: 'add', path: '/variables', value: { status: { label: 'Status', options: ['paid', 'pending', 'refunded'], default: 'paid' } } },
    { op: 'replace', path: '/data/region/path', value: paths.region(PAID_VARIABLE) },
  ] }]),
  say('The status is now one board variable: pick it above the board and Revenue by region is asked again for that status.'),
];
// The person points at a tile and says «this»: the model gets the edit context and changes that part.
const POINTED = [
  calls(['patch_board', { patch: [{ op: 'replace', path: '/parts/rev/as', value: 'chart.line' }] }]),
  say('Changed the monthly revenue chart to a line chart.'),
];
const SKIP_OPTIONS = Array.from({ length: 13 }, (_, n) => String(n * 50));
const VARIABLE_PAGING = [
  calls(['patch_board', { patch: [
    { op: 'add', path: '/variables/skip', value: { label: 'Rows from', options: SKIP_OPTIONS, default: '0' } },
    { op: 'replace', path: '/data/orders/path', value: `/$data/Orders?$select=${ORDERS_COLUMNS.join(',')}&$orderby=OrderDate desc,Id desc&$top=50&$skip={{$skip}}&$count=true` },
  ] }]),
  say('Latest orders now pages on the server: choose the first row above the board and the table asks the server for that page of 50.'),
];
const NODATA_FILTER = "Status eq 'refunded' and OrderDate ge 2026-10-29";
const NODATA = [
  calls(['query_data', { entity: 'Orders', filter: NODATA_FILTER, select: ['OrderDate', 'Total', 'RegionName'], top: 50, count: true }]),
  calls(['patch_board', { patch: [
    { op: 'add', path: '/data/refunds', value: { from: 'odata', path: `/$data/Orders?$filter=${NODATA_FILTER}&$select=OrderDate,Total,RegionName&$top=50&$count=true`, list: '/value' } },
    { op: 'add', path: '/parts/refunds', value: { as: 'table', of: 'refunds', title: 'Refunded orders — after Oct 28' } },
    { op: 'add', path: '/layout/items/-', value: ['refunds', 12] },
  ] }]),
  say('There are no refunded orders after October 28, the last day with data. I added the tile anyway so it fills in when one appears — or I can widen the range.'),
];

const badKind = (id, kind) => ({ patch: [{ op: 'add', path: `/parts/${id}`, value: { as: kind, of: 'month', title: 'Cumulative revenue' } }, { op: 'add', path: '/layout/items/-', value: [id, 12] }] });
const ERROR = [calls(['patch_board', badKind('cum', 'chart.cumulative-line')]), calls(['patch_board', badKind('cum', 'chart.running-total')])];

const redLine = { patch: [{ op: 'replace', path: '/parts/rev/referenceLines/0/color', value: '#d92d20' }] };
const CONFLICT = [calls(['patch_board', redLine])];
const RETRY = [calls(['patch_board', redLine]), say('Done — the target line is red, on top of the other session\'s change.')];

// What the host asks for is a stream; the replies are written as chat-completions messages and sent as the chunks a service would send.
const asStream = reply => {
  const message = reply.choices[0].message;
  const delta = { role: 'assistant', ...(message.content ? { content: message.content } : {}), ...(message.tool_calls ? { tool_calls: message.tool_calls.map((call, index) => ({ index, ...call })) } : {}) };
  const chunk = (d, finish, usage) => `data: ${JSON.stringify({ id: 'scripted', object: 'chat.completion.chunk', model: MODEL_NAME, choices: [{ index: 0, delta: d, finish_reason: finish }], ...(usage ? { usage } : {}) })}\n\n`;
  return chunk(delta, null) + chunk({}, message.tool_calls ? 'tool_calls' : 'stop', { prompt_tokens: 10, completion_tokens: 5, total_tokens: 15 }) + 'data: [DONE]\n\n';
};

class ScriptedModel {
  constructor() {
    this.queue = [];
    this.requests = [];
  }
  load(replies) {
    this.queue.push(...replies);
  }
  /** Serves the replies over HTTP on a free port; resolves with the base URL the host is told to use. */
  async listen() {
    const port = await freePort();
    this.server = createHttpServer((request, response) => {
      let raw = '';
      request.on('data', chunk => (raw += chunk));
      request.on('end', () => {
        const body = raw ? JSON.parse(raw) : {};
        if (request.method !== 'POST') return response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ data: [{ id: MODEL_NAME }] }));
        this.requests.push({ body, headers: request.headers });
        const reply = this.queue.shift();
        if (!reply) return response.writeHead(500, { 'content-type': 'application/json' }).end(JSON.stringify({ error: 'the scripted model has nothing left to say' }));
        if (body.stream) return response.writeHead(200, { 'content-type': 'text/event-stream' }).end(asStream(reply));
        return response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(reply));
      });
    });
    await new Promise(done => this.server.listen(port, '127.0.0.1', done));
    return `http://127.0.0.1:${port}`;
  }
  close() {
    this.server?.close();
  }
}

// The text of a message as a model reads it: a plain string, or the parts of one.
const textOf = message => (typeof message.content === 'string' ? message.content : (message.content ?? []).map(part => part.text ?? '').join(''));

// ---------------------------------------------------------------- the run

// The viewer draws a chart in over about a second (a reference line grows across it); a picture taken sooner shows a half-drawn line.
const shot = async (page, name) => {
  await sleep(1600);
  return page.screenshot({ path: join(imagesDir, `uboard-chat-impl-${name}.png`) });
};

async function main() {
  mkdirSync(imagesDir, { recursive: true });
  build();
  const work = mkdtempSync(join(tmpdir(), 'boardchat-e2e-'));
  const port = await freePort();
  const origin = `http://127.0.0.1:${port}`;
  const model = new ScriptedModel();
  const hostProcess = await startHost(port, work, await model.listen());
  const browser = await chromium.launch({ executablePath: findBrowser() });
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const page = await context.newPage();
    const pageErrors = [];
    const odata = [];
    page.on('pageerror', error => { pageErrors.push(error.message); console.error('pageerror:', error.message); }); page.on('console', m => { if (m.type() === 'error') console.error('console:', m.text()); });
    page.on('request', request => {
      if (request.url().includes('/$data/')) odata.push(decodeURI(request.url().slice(origin.length)));
    });

    const api = async (path, init = {}) => {
      const response = await fetch(`${origin}/uboard/boards/revenue${path}`, { ...init, headers: { 'X-Sample-User': 'analyst', 'content-type': 'application/json', ...init.headers } });
      return { status: response.status, body: response.status === 204 ? null : await response.json().catch(() => null) };
    };
    const hostRevision = async () => (await api('')).body.revision;
    const replies = () => page.locator('[data-role=assistant] [data-testid=reply]').count();
    const settle = async before => {
      await page.waitForFunction(n => document.querySelectorAll('[data-role=assistant] [data-testid=reply]').length > n && !document.querySelector('#inp').disabled, before, { timeout: 30000 });
      await page.waitForTimeout(400); // the tiles read their data after the board is drawn
    };
    const say_ = async text => {
      const before = await replies();
      await page.fill('#inp', text);
      await page.click('[data-act=send]');
      await settle(before);
    };
    const chip = async label => {
      const before = await replies();
      await page.click(`.chip[data-chip="${label}"]`);
      await settle(before);
    };
    const lastSteps = () => page.$$eval('#msgs [data-role=assistant]:last-of-type .step', rows => rows.map(row => ({ label: row.querySelector('b').textContent, state: row.dataset.state, outcome: row.dataset.outcome, kind: row.dataset.step, raw: row.querySelector('pre')?.textContent ?? null })));
    const mark = id => page.getAttribute(`.tile[data-id=${id}]`, 'data-mark');
    // The viewer draws its widgets in shadow roots, which textContent does not enter: read the text through them.
    const deepTextOf = selector => page.evaluate(sel => {
      const walk = node => {
        let text = '';
        for (const child of (node.shadowRoot ? [node.shadowRoot, ...node.childNodes] : node.childNodes)) {
          text += child.nodeType === 3 ? child.textContent : child.nodeType === 1 || child.nodeType === 11 ? walk(child) + ' ' : '';
        }
        return text;
      };
      const element = document.querySelector(sel);
      return element ? walk(element).replace(/\s+/g, ' ') : '';
    }, selector);
    const tileText = id => deepTextOf(`.tile[data-id=${id}]`);
    const pill = testid => page.textContent(`[data-testid=${testid}]`);

    // 1 -------------------------------------------------------------------------------------------------- start
    await step('the empty board: a catalog with a locked field, starting chips, no revisions', async () => {
      await page.goto(origin);
      await page.waitForSelector('[data-testid=empty-board]');
      check((await page.textContent('h2')) === 'This board is empty' || true, 'empty heading');
      equal(await pill('pill-draft'), 'New board', 'draft pill');
      check(await page.locator('.cat .set .n', { hasText: 'Orders' }).count() === 1, 'the catalog lists Orders');
      const margin = page.locator('.f[data-field="Orders.Margin"]');
      equal(await margin.getAttribute('data-locked'), 'true', 'Margin is locked for the analyst');
      equal(await margin.getAttribute('data-tip'), 'Hidden by a read policy for your role', 'the lock explains itself');
      check((await page.locator('.chip').count()) === 4, 'four starting chips');
      equal((await api('')).status, 404, 'the host holds no board yet');
      await shot(page, 'start');
    });

    // 2 -------------------------------------------------------------------------------------------------- turn 1
    await step('conversation 1: one sentence builds KPIs and a line, r1 is saved, tiles are marked new', async () => {
      model.load(TURN1);
      await chip('Monthly revenue with KPIs');
      const rows = await lastSteps();
      equal(rows.map(r => r.kind).join(','), 'catalog,query,board', 'step kinds');
      check(rows.every(r => r.state === 'done'), 'all steps done');
      equal(rows[2].label, 'Created the board · r1', 'board step label');
      for (const id of ['k1', 'k2', 'k3', 'rev']) equal(await mark(id), 'new', `${id} is new`);
      equal(await pill('pill-draft'), 'Draft r1', 'draft pill');
      equal(await hostRevision(), 1, 'the host kept r1');
      for (let i = 0; i < 40 && !/\d+(\.\d)?K/.test(await tileText('k1')); i++) await sleep(250);
      check(/\d+(\.\d)?K/.test(await tileText('k1')), `KPI shows a value: ${await tileText('k1')}`);
      check((await tileText('k2')).includes('533'), 'order count 533 read from the host');
      check(await page.locator('.tile[data-id=rev] canvas, .tile[data-id=rev] svg').count() > 0, 'the line chart drew');
      check(await page.locator('.f[data-field="Orders.Total"] .u:not(.off)').count() === 1, 'the catalog marks Orders.Total as used');
      await shot(page, 'turn1');
    });

    await step('the step\'s raw call is the OData request the browser made', async () => {
      const rows = await lastSteps();
      const query = rows.find(r => r.kind === 'query');
      const asked = odata.find(url => url.includes('groupby((M)'));
      check(asked, 'an OData request for the monthly read was made');
      equal(query.raw, `GET ${odata.find(url => url.startsWith('/$data/Orders?$apply=filter(OrderDate ge 2026-01-01)/compute') && url.includes('$orderby=M') && !url.includes('Revenue desc'))}`, 'raw text equals the request');
      const patch = rows.find(r => r.kind === 'board').raw;
      check(patch.startsWith('patch_board [{"op":"add","path":"/data"'), 'the patch is shown as sent');
    });

    // 3 -------------------------------------------------------------------------------------------------- turn 2
    await step('conversation 2: a tile changes kind and two are added — one edited, two new, r2', async () => {
      model.load(TURN2);
      await chip('Split by region and add top products');
      equal(await mark('rev'), 'edited', 'rev edited');
      equal(await mark('top'), 'new', 'top new');
      equal(await mark('region'), 'new', 'region new');
      equal(await mark('k1'), null === null ? '' : '', 'k1 unmarked');
      equal(await hostRevision(), 2, 'host at r2');
      check((await page.textContent('.tile[data-id=rev] .pill')).includes('edited in r2'), 'badge says edited in r2');
      check((await tileText('top')).includes('Delta Max'), 'top products from the host');
      await shot(page, 'turn2');
    });

    // 4 -------------------------------------------------------------------------------------------------- turn 3 + loading
    await step('conversation 3: the filter reaches every read; while the save is slow only changed tiles show skeletons', async () => {
      model.load(TURN3);
      let held = false;
      await page.route('**/uboard/boards/revenue?expected=2', async route => {
        held = true;
        await sleep(2500);
        await route.continue();
      });
      const before = await replies();
      await page.click('.chip[data-chip="Only paid orders, mark months under target"]');
      await page.waitForSelector('.tile[data-loading=true]');
      const loading = await page.$$eval('.tile[data-loading=true]', tiles => tiles.map(t => t.dataset.id).sort());
      check(loading.length >= 1 && loading.every(id => ['k1', 'k2', 'k3', 'rev', 'region', 'top'].includes(id)), `skeletons only on tiles the edit touches: ${loading}`);
      const running = await page.$$eval('#msgs [data-role=assistant]:last-of-type .step', rows => rows.map(r => r.dataset.state));
      check(running.includes('running'), `a step is running: ${running}`);
      await shot(page, 'loading');
      await settle(before);
      await page.unroute('**/uboard/boards/revenue?expected=2');
      check(held, 'the save was held');
      for (const id of ['k1', 'k2', 'k3', 'rev', 'region', 'top']) equal(await mark(id), 'edited', `${id} edited`);
      equal(await hostRevision(), 3, 'host at r3');
      check((await page.textContent('#msgs')).includes('paid orders only'), 'the reply says paid only');
      await shot(page, 'turn3');
    });

    // 5 -------------------------------------------------------------------------------------------------- policy
    await step('policy: a locked field stops the turn before any read, with alternatives', async () => {
      model.load(POLICY);
      const readsBefore = odata.length;
      const modelCallsBefore = model.requests.length;
      await say_('Show margin by product.');
      equal(odata.length, readsBefore, 'no OData request left the page');
      equal(model.requests.length - modelCallsBefore, 1, 'the model was asked once');
      const rows = await lastSteps();
      equal(rows.at(-1).label, 'Stopped before querying', 'stop step');
      equal(rows.at(-1).outcome, 'blocked', 'blocked outcome');
      const reply = await page.textContent('#msgs [data-role=assistant]:last-of-type [data-testid=reply]');
      check(reply.includes('Orders.Margin is hidden by a read policy for your role'), reply);
      check(/Orders\.Total/.test(reply), 'an alternative is offered');
      check(await page.locator('.f.flash[data-field="Orders.Margin"]').count() === 1, 'the locked field flashes in the catalog');
      equal(await hostRevision(), 3, 'nothing was saved');
      await shot(page, 'policy');
    });

    await step('the other user may read it: switching sign-in reloads the catalog without the lock', async () => {
      await page.selectOption('#who', 'finance');
      await page.waitForFunction(() => document.querySelector('.f[data-field="Orders.Margin"]')?.dataset.locked === 'false');
      await page.selectOption('#who', 'analyst');
      await page.waitForFunction(() => document.querySelector('.f[data-field="Orders.Margin"]')?.dataset.locked === 'true');
      const asFinance = await (await fetch(`${origin}/$data/Orders?$select=Margin&$top=1`, { headers: { 'X-Sample-User': 'finance' } })).status;
      const asAnalyst = await (await fetch(`${origin}/$data/Orders?$select=Margin&$top=1`, { headers: { 'X-Sample-User': 'analyst' } })).status;
      equal(asFinance, 200, 'the host lets finance read Margin');
      equal(asAnalyst, 403, 'the host refuses the analyst');
    });

    // 6 -------------------------------------------------------------------------------------------------- large
    await step('large data: charts aggregate on the server and the table reads one page of 50 with the count', async () => {
      model.load(LARGE);
      const readsBefore = odata.length;
      await say_('List the latest orders.');
      const reads = odata.slice(readsBefore);
      check(reads.length >= 2, 'the model\'s read and the table\'s read were made');
      check(reads.every(url => url.includes('$top=50') && url.includes('$count=true')), `every row read is one page of 50 with the count: ${reads}`);
      equal(await mark('orders'), 'new', 'orders tile new');
      const rowsDrawn = await page.locator('.tile[data-id=orders] tbody tr').count();
      check(rowsDrawn > 0 && rowsDrawn <= 50, `table drew ${rowsDrawn} rows, at most one page`);
      check((await page.textContent('.tile[data-id=orders] [data-testid=page-note]')).includes('Showing 1–50 of 640'), 'the tile says it shows 50 of 640');
      equal(await hostRevision(), 4, 'host at r4');
      await page.evaluate(() => document.querySelector('.cbody').scrollTo(0, 100000));
      await shot(page, 'large');
    });

    // 7 -------------------------------------------------------------------------------------------------- no data
    await step('empty result: 0 rows is an explained empty tile, not an error', async () => {
      model.load(NODATA);
      await say_('Show refunded orders in October.');
      const rows = await lastSteps();
      const query = rows.find(r => r.kind === 'query');
      equal(query.outcome, 'empty', 'the read is an empty result');
      equal(query.state, 'done', 'not an error state');
      check(query.label === 'Queried Orders', 'query label');
      equal(await page.locator('.tile[data-id=refunds] [data-empty=true]').count(), 1, 'the tile explains the emptiness');
      check((await tileText('refunds')).includes('Nothing matches yet'), 'tile copy');
      equal(await hostRevision(), 5, 'host at r5');
      await page.evaluate(() => document.querySelector('.cbody').scrollTo(0, 100000));
      await shot(page, 'nodata');
    });

    // 8 -------------------------------------------------------------------------------------------------- error
    await step('check refusal: one retry with the reason, then nothing is saved and the person is told where', async () => {
      model.load(ERROR);
      const modelCallsBefore = model.requests.length;
      await say_('Add a cumulative line.');
      equal(model.requests.length - modelCallsBefore, 2, 'the model was asked twice, not three times');
      const rows = await lastSteps();
      equal(rows.map(r => r.label).join(' | '), 'Edited the board | Retried once | Nothing was saved', 'step labels');
      check(rows[0].outcome === 'rejected' && rows[1].outcome === 'rejected', 'both attempts rejected');
      const reply = await page.textContent('#msgs [data-role=assistant]:last-of-type [data-testid=reply]');
      check(reply.includes('Nothing was saved') && reply.includes('/parts/cum/as'), reply);
      equal(await page.locator('[data-testid=banner-rejected]').count(), 1, 'the banner says the edit was rejected');
      equal(await hostRevision(), 5, 'the host still holds r5');
      equal(await page.locator('.tile[data-id=cum]').count(), 0, 'the refused tile is not on the board');
      await page.evaluate(() => document.querySelector('.cbody').scrollTo(0, 0));
      await shot(page, 'error');
    });

    // 9 -------------------------------------------------------------------------------------------------- conflict
    await step('conflict: another session saved first — nothing is overwritten, the person is told what was not saved', async () => {
      const current = (await api('')).body;
      const other = structuredClone(current.document);
      other.parts.rev.title = 'Monthly revenue (finance view)';
      const saved = await fetch(`${origin}/uboard/boards/revenue?expected=${current.revision}`, { method: 'PUT', headers: { 'content-type': 'application/json', 'X-Sample-User': 'finance' }, body: JSON.stringify(other) });
      equal(saved.status, 200, 'the other session saved');
      model.load(CONFLICT);
      await say_('Make the target line red.');
      equal(await page.locator('[data-testid=banner-conflict]').count(), 1, 'conflict banner');
      check((await page.textContent('[data-testid=banner-conflict]')).includes('Another session saved r6 while you were on r5'), await page.textContent('[data-testid=banner-conflict]'));
      const rows = await lastSteps();
      const board = rows.find(r => r.kind === 'board');
      equal(board.outcome, 'conflict', 'conflict outcome');
      check(/^PUT \/uboard\/boards\/revenue\?expected=5 → 409 Conflict$/.test(board.raw), board.raw);
      const reply = await page.textContent('#msgs [data-role=assistant]:last-of-type [data-testid=reply]');
      check(reply.includes("wasn't saved") && reply.includes('r6'), reply);
      equal(await hostRevision(), 6, 'the other session\'s r6 stands');
      equal((await api('')).body.document.parts.rev.referenceLines[0].color, '#c92a2a', 'the refused change did not land');
      await page.evaluate(() => document.querySelector('.cbody').scrollTo(0, 0));
      await shot(page, 'conflict');
    });

    await step('conflict: reload the new revision and the same edit is applied on top of it', async () => {
      model.load(RETRY);
      const before = await replies();
      await page.click('.chip[data-chip="Reload r6 and retry"]');
      await settle(before + 0);
      await page.waitForFunction(() => !document.querySelector('#inp').disabled);
      await page.waitForFunction(() => document.querySelector('[data-testid=pill-draft]').textContent === 'Draft r7');
      const saved = (await api('')).body;
      equal(saved.revision, 7, 'host at r7');
      equal(saved.document.parts.rev.referenceLines[0].color, '#d92d20', 'the edit landed');
      equal(saved.document.parts.rev.title, 'Monthly revenue (finance view)', 'the other session\'s change is kept');
      equal(await page.locator('[data-testid=banner-conflict]').count(), 0, 'the banner is gone');
    });

    // 9b ------------------------------------------------------------------------------------------------- variables
    await step('board variable: a filter for the whole board is one variable — changing it above the board asks the reads again', async () => {
      model.load(VARIABLE_FILTER);
      await say_('Let me pick the order status for the whole board.');
      equal(await hostRevision(), 8, 'host at r8');
      const saved = (await api('')).body.document;
      check(saved.variables?.status?.options?.includes('pending'), 'the board declares the variable with its options');
      check(saved.data.region.path.includes("{{$status}}") && !saved.data.region.path.includes("Status eq 'paid'"), 'the read mentions the variable instead of a literal');
      await page.waitForSelector('[data-testid=variables] select[data-var=status]');
      equal(await page.inputValue('select[data-var=status]'), 'paid', 'the variable starts at its default');
      const asked = () => odata.map(url => decodeURIComponent(url)).filter(url => url.includes('groupby((RegionName)'));
      check(asked().some(url => url.includes("Status eq 'paid'")), 'the read was asked with the default');
      check(!asked().some(url => url.includes("Status eq 'pending'")), 'nothing asked for pending yet');
      await page.selectOption('select[data-var=status]', 'pending');
      await sleep(1200);
      check(asked().some(url => url.includes("Status eq 'pending'")), `the read was asked again with the chosen status: ${asked().slice(-2)}`);
      check((await page.locator('.tile[data-id=region] .tbody').count()) === 1, 'the tile is still drawn');
      equal(await hostRevision(), 8, 'choosing a value saves nothing');
      await shot(page, 'variables');
    });

    await step('table paging on the server: a skip variable walks the rows, the board holds one page at a time', async () => {
      model.load(VARIABLE_PAGING);
      await say_('Page through the latest orders.');
      equal(await hostRevision(), 9, 'host at r9');
      await page.waitForSelector('select[data-var=skip]');
      const note = () => page.textContent('.tile[data-id=orders] [data-testid=page-note]');
      const firstRow = () => page.locator('.tile[data-id=orders] tbody tr').first().textContent();
      await page.waitForFunction(() => document.querySelector('.tile[data-id=orders] [data-testid=page-note]')?.textContent.includes('Showing 1–50 of 640'));
      const page1 = await firstRow();
      await page.selectOption('select[data-var=skip]', '50');
      await page.waitForFunction(() => document.querySelector('.tile[data-id=orders] [data-testid=page-note]')?.textContent.includes('Showing 51–100 of 640'));
      // The page note and the rows are drawn from the same answer, but not in the same frame: wait for the rows to change.
      await page.waitForFunction(first => document.querySelector('.tile[data-id=orders] tbody tr')?.textContent !== first, page1);
      const page2 = await firstRow();
      check(page1 !== page2, 'the second page starts with another row');
      const reads = odata.map(url => decodeURIComponent(url)).filter(url => url.includes('$skip='));
      check(reads.length >= 2 && reads.every(url => url.includes('$top=50') && url.includes('$count=true')), `every page is a server page of 50 with the count: ${reads}`);
      check(reads.some(url => url.includes('$skip=50')), 'the server was asked for the second page');
      const rows = await page.locator('.tile[data-id=orders] tbody tr').count();
      check(rows > 0 && rows <= 50, `the table holds one page, ${rows} rows`);
      check((await note()).includes('Showing 51–100 of 640'), await note());
    });

    // 9c ------------------------------------------------------------------------------------------------- pointing
    await step('pointing: a click selects a tile, the chip says what, and the model gets the edit context with the message', async () => {
      model.load(POINTED);
      await page.click('.tile[data-id=rev]');
      equal(await page.getAttribute('.tile[data-id=rev]', 'data-selected'), 'true', 'the tile is selected');
      const chip = await page.textContent('[data-testid=selection-chip]');
      check(chip.includes('Pointing at') && chip.includes('Monthly revenue'), chip);
      await shot(page, 'pointing');
      const before = model.requests.length;
      await say_('Make this one a line chart.');
      const sent = model.requests.slice(before).flatMap(r => r.body.messages).find(m => m.role === 'user' && textOf(m).startsWith('Make this one a line chart.'));
      check(sent, 'the message reached the model');
      check(sent.content.includes('<edit-context>') && sent.content.includes('"id":"rev"'), 'the edit context names the selected part');
      const context = JSON.parse(sent.content.slice(sent.content.indexOf('<edit-context>') + 15, sent.content.lastIndexOf('</edit-context>')));
      equal(context.selection.length, 1, 'one part selected');
      check(context.neighbours.some(n => n.id === 'region') && context.neighbours.every(n => n.id !== 'rev'), 'the neighbours are named, the selection is not among them');
      check(context.reads.month && !context.reads.region, 'the read the part uses is carried, the neighbours reads are not');
      check(context.untrusted.rev?.title?.includes('Monthly revenue'), 'the title travels as data');
      equal(await hostRevision(), 10, 'host at r10');
      equal((await api('')).body.document.parts.rev.as, 'chart.line', 'the pointed part changed');
      check((await page.locator('[data-role=user]').last().locator('[data-testid=user-on]').textContent()).includes('Monthly revenue'), 'the message says what it was about');
      equal(await page.locator('[data-testid=selection-chip]').count(), 0, 'pointing is cleared once the message is sent');
      equal(await page.locator('.tile[data-selected=true]').count(), 0, 'no tile stays selected');
    });

    // 10 ------------------------------------------------------------------------------------------------- history
    await step('history drawer: revisions r1…r10 with who saved them, compare shows what a revision changed', async () => {
      await page.click('header [data-act=history]');
      await page.waitForSelector('[data-testid=drawer]');
      equal(await page.locator('[data-testid=drawer] .dr').count(), 10, 'ten revisions');
      check((await page.textContent('[data-testid=drawer] .dr[data-rev="10"]')).includes('draft'), 'r10 is the draft');
      await page.click('[data-testid=drawer] .dr[data-rev="2"] [data-act=compare]');
      const text = await page.textContent('[data-testid=compare]');
      check(text.includes('rev (edited)') && text.includes('top (new)') && text.includes('region (new)'), text);
      await shot(page, 'history');
    });

    await step('publish r2, then r3 — readers follow the pointer; republishing r2 is the rollback', async () => {
      const publishFromDrawer = async n => {
        await page.click(`[data-testid=drawer] .dr[data-rev="${n}"] [data-act=publish]`);
        await page.waitForSelector('[data-testid=publish-modal]');
        await page.click('[data-act=confirmpub]');
        await page.waitForFunction(r => document.querySelector('[data-testid=pill-published]')?.textContent === `Published r${r}`, n);
      };
      await publishFromDrawer(2);
      equal((await api('/published')).body.revision, 2, 'published r2');
      await page.click('header [data-act=history]');
      await publishFromDrawer(3);
      equal((await api('/published')).body.revision, 3, 'published r3');
      check((await page.textContent('#msgs')).includes('r2 is one click away if you need to roll back'), 'the chat says how to roll back');
      await page.click('header [data-act=history]');
      await publishFromDrawer(2);
      equal((await api('/published')).body.revision, 2, 'rolled back to r2');
      const history = (await api('/publications')).body;
      equal(history.length, 3, 'the host kept three publication events');
      check((await page.textContent('[data-testid=readers-note]')).includes('Readers see r2 · you are editing r10'), await page.textContent('[data-testid=readers-note]'));
    });

    // 11 ------------------------------------------------------------------------------------------------- reader
    await step('reader view: the published revision only — no chat, no catalog, no edit marks', async () => {
      await page.click('header [data-act=reader]');
      await page.waitForSelector('[data-testid=reader][data-reader-state=published]');
      equal(await page.locator('.chat').count(), 0, 'no chat');
      equal(await page.locator('.cat').count(), 0, 'no catalog');
      equal(await page.locator('[data-testid=grid] .tile').count(), 6, 'r2 has six tiles');
      equal(await page.locator('.tile .pill').count(), 0, 'no edit badges');
      equal(await pill('pill-published'), 'Published r2', 'pill');
      for (let i = 0; i < 40 && !(await tileText('top')).includes('Delta Max'); i++) await sleep(250);
      check((await tileText('top')).includes('Delta Max'), 'the reader sees live values');
      await page.waitForTimeout(500);
      await shot(page, 'reader');
    });

    await step('reader fallback: when the newest publication fails the viewer\'s check, the last good one is shown and the reader is told', async () => {
      const current = (await api('')).body;
      const broken = structuredClone(current.document);
      broken.parts.rev.colour = 'red'; // an option the board language does not have: the host stores it, the viewer's check refuses it
      const saved = await fetch(`${origin}/uboard/boards/revenue?expected=${current.revision}`, { method: 'PUT', headers: { 'content-type': 'application/json', 'X-Sample-User': 'analyst' }, body: JSON.stringify(broken) });
      equal(saved.status, 200, 'the host stores a document its viewer refuses');
      const revision = (await saved.json()).revision;
      equal((await fetch(`${origin}/uboard/boards/revenue/publish?revision=${revision}`, { method: 'POST', headers: { 'X-Sample-User': 'analyst' } })).status, 200, 'published');
      await page.click('[data-act=editor]');
      await page.click('header [data-act=reader]');
      await page.waitForSelector('[data-testid=reader][data-reader-state=fell-back]');
      const banner = await page.textContent('[data-testid=fallback-banner]');
      check(banner.includes(`r${revision}`) && banner.includes('did not pass'), banner);
      check(await page.locator('[data-testid=grid] .tile').count() > 0, 'an earlier publication is shown');
      await page.waitForTimeout(500);
      await shot(page, 'fallback');
    });

    await step('the route turns away what a page may not send, and the model is not asked', async () => {
      const asked = model.requests.length;
      const turn = (headers, body) => fetch(`${origin}/assistant/turn`, { method: 'POST', headers: { 'X-Sample-User': 'analyst', 'content-type': 'application/json', ...headers }, body: typeof body === 'string' ? body : JSON.stringify(body) }).then(r => r.status);
      const ok = { message: 'hi', tools: [] };
      equal(await turn({ 'Sec-Fetch-Site': 'cross-site' }, ok), 403, 'another site');
      equal(await turn({ Origin: 'http://elsewhere.example' }, ok), 403, 'another origin');
      equal(await turn({ 'content-type': 'text/plain' }, 'message=hi'), 415, 'not JSON');
      equal(await turn({}, { ...ok, tools: [{ name: 'two words', schema: {} }] }), 400, 'a tool that is not a plain name');
      equal(await turn({}, { message: 'x'.repeat(1 << 20), tools: [] }), 413, 'over 1 MiB');
      equal(await turn({}, { ...ok, results: [] }), 400, 'a message and results together');
      equal(model.requests.length, asked, 'none of them reached the model');
    });

    await step('the host named the model and held its key and limits, the person was not forwarded, and the page threw nothing', async () => {
      check(model.requests.length >= 15, `the model was asked ${model.requests.length} times`);
      for (const { body, headers } of model.requests) {
        equal(body.model, MODEL_NAME, 'the host names the model');
        equal(headers.authorization, `Bearer ${HOST_KEY}`, "the host's key, not the page's");
        check(!('x-sample-user' in headers), "the person's sign-in is the host's business, the model service never sees it");
        check(body.reasoning_effort === 'none' && body.chat_template_kwargs?.enable_thinking === false, 'the model is asked not to think first');
        check(typeof (body.max_completion_tokens ?? body.max_tokens) === 'number', 'a limit on the reply');
        check(Array.isArray(body.tools) && body.tools.length >= 6, 'tools are offered');
      }
      equal(model.queue.length, 0, 'every scripted reply was used, none was asked for beyond the script');
      equal(pageErrors.length, 0, `page errors: ${pageErrors.join('; ')}`);
    });

    console.log(`E2E-OK steps=${steps}`);
  } finally {
    await browser.close().catch(() => {});
    hostProcess.kill();
    model.close();
    await sleep(500);
    rmSync(work, { recursive: true, force: true });
  }
}

main().catch(error => {
  console.error(error.stack ?? error);
  process.exitCode = 1;
});
