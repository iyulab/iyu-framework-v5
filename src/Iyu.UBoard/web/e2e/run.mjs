// End-to-end run of the board screen the library serves, in a real browser against a real host (the board chat sample).
//
// What is real: the host (started here on a free port with a throwaway database), its OData service, the catalog, the board
// routes, publishing, the screen the library serves and the viewer in it — and the assistant's route, with IronHive.Agent and IronProw
// behind it. What is scripted is the model service: a small OpenAI-compatible server here answers the host's requests with canned replies
// (tool calls included, sent as the stream the host asks for), so no key and no model are needed. Everything the assistant does with
// those replies — the loop, the host-run tools, reading the catalog, querying, checking and saving the board — goes through the real page
// and host. The page's Content-Security-Policy is held to: a violation, or a console error, fails the run.
//
//   node e2e/run.mjs        (from src/Iyu.UBoard/web; prints E2E-OK steps=N; BOARDCHAT_SHOTS_DIR chooses where the screenshots go)
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
const sample = resolve(web, '../../../samples/BoardChat');
// Screenshots go to BOARDCHAT_SHOTS_DIR when it is set, else to e2e/screenshots (not committed).
const imagesDir = process.env.BOARDCHAT_SHOTS_DIR ? resolve(process.env.BOARDCHAT_SHOTS_DIR) : join(here, 'screenshots');
const HOST_KEY = 'host-held-key';
const MODEL_NAME = 'scripted-model';
const sleep = ms => new Promise(done => setTimeout(done, ms));
const BOARD = 'revenue';
const SCREEN = '/uboard/ui';

let steps = 0;
let stepStarted = Date.now();
const step = async (name, run) => {
  stepStarted = Date.now();
  try {
    await run();
  } catch (error) {
    console.error(`E2E-FAIL at step ${steps + 1}: ${name}\n${error.stack ?? error}`);
    throw error;
  }
  steps += 1;
  console.log(`  ok ${String(steps).padStart(2)}  ${name}  (${((Date.now() - stepStarted) / 1000).toFixed(1)}s)`);
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

// The library's build builds the screen (npm) and embeds it, so building the sample builds what the browser is shown.
function build() {
  const dotnet = spawnSync('dotnet', ['build', join(sample, 'BoardChat.csproj'), '-v', 'q', '-nologo'], { encoding: 'utf8' });
  if (dotnet.status !== 0) throw new Error(`the host did not build:\n${dotnet.stdout}${dotnet.stderr}`);
}

// The host runs from a copy of its build output, so a build or a test run elsewhere can replace the files meanwhile.
async function startHost(port, work, modelUrl) {
  const app = join(work, 'app');
  cpSync(join(sample, 'bin/Debug/net10.0'), app, { recursive: true });
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
        const answer = () => (body.stream ? response.writeHead(200, { 'content-type': 'text/event-stream' }).end(asStream(reply)) : response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(reply)));
        return reply.delay ? setTimeout(() => answer(), reply.delay) : answer();
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
    // The sample signs in by a header (a host puts its own authentication in front of the same routes): the analyst makes and edits boards.
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, extraHTTPHeaders: { 'X-Sample-User': 'analyst' } });
    // A page the policy refuses something on says so by an event; every page of every context is watched from its first script.
    const watch = async target => {
      await target.addInitScript(() => {
        window.__violations = [];
        document.addEventListener('securitypolicyviolation', event => window.__violations.push(`${event.violatedDirective} ${event.blockedURI}`));
      });
    };
    await watch(context);
    const page = await context.newPage();
    const pageErrors = [];
    const policyProblems = [];
    const odata = [];
    const watchConsole = target => {
      target.on('pageerror', error => { pageErrors.push(error.message); console.error('pageerror:', error.message); });
      target.on('console', m => {
        if (m.type() !== 'error') return;
        console.error('console:', m.text());
        if (/Content.Security.Policy/i.test(m.text())) policyProblems.push(m.text());
      });
    };
    watchConsole(page);
    page.on('request', request => {
      if (request.url().includes('/$data/')) odata.push(decodeURI(request.url().slice(origin.length)));
    });

    let boardId = BOARD;
    const api = async (path, init = {}) => {
      const response = await fetch(`${origin}/uboard/boards/${boardId}${path}`, { ...init, headers: { 'X-Sample-User': 'analyst', 'content-type': 'application/json', ...init.headers } });
      return { status: response.status, body: response.status === 204 ? null : await response.json().catch(() => null) };
    };
    const hostRevision = async () => (await api('')).body.revision;
    const replies = () => page.locator('[data-role=assistant] [data-testid=reply]').count();
    const settle = async before => {
      await page.waitForFunction(n => document.querySelectorAll('[data-role=assistant] [data-testid=reply]').length > n && !document.querySelector('u-prompt').loading, before, { timeout: 30000 });
      await page.waitForTimeout(400); // the tiles read their data after the board is drawn
    };
    const say_ = async text => {
      const before = await replies();
      await page.locator('u-prompt textarea').fill(text); // the chat components keep the input inside shadow roots
      await page.locator('u-prompt textarea').press('Enter');
      await settle(before);
    };
    const chip = async label => {
      const before = await replies();
      await page.click(`.chip[data-chip="${label}"]`);
      await settle(before);
    };
    // What a person types for each turn of the scripted conversation (the screen's starters name none of this data).
    const ASK = {
      first: 'Show monthly revenue for 2026, with total revenue, order count and average order value up top.',
      second: 'Split revenue by region as stacked bars, and add the top 5 products.',
      third: 'Only paid orders — and mark the months under our 30k target.',
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
    await step('the list: no boards yet, a button to make one because the application lets this person; the page carries its policy', async () => {
      const response = await page.goto(`${origin}${SCREEN}`);
      equal(response.status(), 200, 'the list page');
      const policy = response.headers()['content-security-policy'] ?? '';
      check(/script-src 'self'(;|$)/.test(policy) && policy.includes("default-src 'none'") && policy.includes("frame-ancestors 'none'"), `a restrictive policy: ${policy}`);
      equal(response.headers()['x-content-type-options'], 'nosniff', 'nosniff');
      await page.waitForSelector('[data-testid=no-boards]');
      check((await page.textContent('[data-testid=no-boards]')).includes('No boards yet'), 'the empty state');
      equal(await page.locator('.top [data-act=newboard]').count(), 1, 'a new-board button');
      equal(await page.locator('.cat').count(), 0, 'no catalog pane: the catalog is the assistant\'s tool, not a screen');
      await shot(page, 'list-empty');
    });

    await step('a new board: a name, then an empty board that is the person\'s to edit, with starters and no revision of content yet', async () => {
      await page.click('.top [data-act=newboard]');
      await page.waitForSelector('[data-testid=new-board-dialog]');
      equal(await page.locator('[data-act=createboard]').isDisabled(), true, 'a board needs a name');
      await page.fill('[aria-label="Board name"]', 'Revenue overview');
      await page.click('[data-act=createboard]');
      await page.waitForSelector('[data-testid=empty-board]');
      const where = new URL(page.url());
      check(/^\/uboard\/ui\/boards\/revenue-overview-[0-9a-f]{4}$/.test(where.pathname), `the board's own page: ${where.pathname}`);
      boardId = where.pathname.split('/').pop();
      equal((await page.textContent('[data-testid=board-title]')), 'Revenue overview', 'the name');
      check((await page.locator('.chip').count()) === 2, 'two starting chips');
      equal(await page.locator('[data-testid=pill-readonly]').count(), 0, 'not read only');
      equal((await api('')).body.revision, 1, 'the host holds the empty board');
      await shot(page, 'start');
    });

    // 2 -------------------------------------------------------------------------------------------------- turn 1
    await step('conversation 1: one sentence builds KPIs and a line, a revision is saved, tiles are marked new', async () => {
      model.load(TURN1);
      await say_(ASK.first);
      const rows = await lastSteps();
      equal(rows.map(r => r.kind).join(','), 'catalog,query,board', 'step kinds');
      check(rows.every(r => r.state === 'done'), 'all steps done');
      equal(rows[2].label, 'Edited the board · r2', 'board step label');
      for (const id of ['k1', 'k2', 'k3', 'rev']) equal(await mark(id), 'new', `${id} is new`);
      equal(await pill('pill-draft'), 'Draft r2', 'draft pill');
      equal(await hostRevision(), 2, 'the host kept r2 (r1 is the empty board made with it)');
      for (let i = 0; i < 40 && !/\d+(\.\d)?K/.test(await tileText('k1')); i++) await sleep(250);
      check(/\d+(\.\d)?K/.test(await tileText('k1')), `KPI shows a value: ${await tileText('k1')}`);
      check((await tileText('k2')).includes('533'), 'order count 533 read from the host');
      check(await page.locator('.tile[data-id=rev] canvas, .tile[data-id=rev] svg').count() > 0, 'the line chart drew');
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
      await say_(ASK.second);
      equal(await mark('rev'), 'edited', 'rev edited');
      equal(await mark('top'), 'new', 'top new');
      equal(await mark('region'), 'new', 'region new');
      equal(await mark('k1'), null === null ? '' : '', 'k1 unmarked');
      equal(await hostRevision(), 3, 'host at r3');
      check((await page.textContent('.tile[data-id=rev] .pill')).includes('edited in r3'), 'badge says edited in r3');
      check((await tileText('top')).includes('Delta Max'), 'top products from the host');
      await shot(page, 'turn2');
    });

    // 4 -------------------------------------------------------------------------------------------------- turn 3 + loading
    await step('conversation 3: the filter reaches every read; while the save is slow only changed tiles show skeletons', async () => {
      model.load(TURN3);
      let held = false;
      await page.route('**/uboard/boards/*?expected=3', async route => {
        held = true;
        await sleep(2500);
        await route.continue();
      });
      const before = await replies();
      await page.locator('u-prompt textarea').fill(ASK.third);
      await page.locator('u-prompt textarea').press('Enter');
      await page.waitForSelector('.tile[data-loading=true]');
      const loading = await page.$$eval('.tile[data-loading=true]', tiles => tiles.map(t => t.dataset.id).sort());
      check(loading.length >= 1 && loading.every(id => ['k1', 'k2', 'k3', 'rev', 'region', 'top'].includes(id)), `skeletons only on tiles the edit touches: ${loading}`);
      const running = await page.$$eval('#msgs [data-role=assistant]:last-of-type .step', rows => rows.map(r => r.dataset.state));
      check(running.includes('running'), `a step is running: ${running}`);
      await shot(page, 'loading');
      await settle(before);
      await page.unroute('**/uboard/boards/*?expected=3');
      check(held, 'the save was held');
      for (const id of ['k1', 'k2', 'k3', 'rev', 'region', 'top']) equal(await mark(id), 'edited', `${id} edited`);
      equal(await hostRevision(), 4, 'host at r4');
      check((await deepTextOf('#msgs')).includes('paid orders only'), 'the reply says paid only');
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
      const reply = await deepTextOf('#msgs [data-role=assistant]:last-of-type [data-testid=reply]');
      check(reply.includes('Orders.Margin is hidden by a read policy for your role'), reply);
      check(/Orders\.Total/.test(reply), 'an alternative is offered');
      equal(await hostRevision(), 4, 'nothing was saved');
      await shot(page, 'policy');
    });

    await step('the other user may read it: the host answers each person as they are, the screen holds no sign-in of its own', async () => {
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
      equal(await hostRevision(), 5, 'host at r5');
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
      equal(await hostRevision(), 6, 'host at r6');
      await page.evaluate(() => document.querySelector('.cbody').scrollTo(0, 100000));
      await shot(page, 'nodata');
    });

    // 7b ------------------------------------------------------------------------------------------------- stop
    await step('stop: the prompt turns into a stop button while the assistant works; pressing it ends the turn and the next message still works', async () => {
      model.load([{ ...say('too late'), delay: 5000 }, say('Back again — nothing was lost.')]);
      const before = await replies();
      await page.locator('u-prompt textarea').fill('Think about the whole year.');
      await page.locator('u-prompt textarea').press('Enter');
      await page.waitForFunction(() => document.querySelector('u-prompt').loading);
      await page.getByRole('button', { name: 'Stop generating' }).click();
      await settle(before);
      const stopped = await deepTextOf('#msgs [data-role=assistant]:last-of-type [data-testid=reply]');
      check(stopped.startsWith('Stopped. The board is at r6'), stopped);
      await say_('Are you still there?');
      check((await deepTextOf('#msgs [data-role=assistant]:last-of-type [data-testid=reply]')).includes('Back again'), 'the conversation goes on after a stop');
      equal(await hostRevision(), 6, 'host still at r6');
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
      const reply = await deepTextOf('#msgs [data-role=assistant]:last-of-type [data-testid=reply]');
      check(reply.includes('Nothing was saved') && reply.includes('/parts/cum/as'), reply);
      equal(await page.locator('[data-testid=banner-rejected]').count(), 1, 'the banner says the edit was rejected');
      equal(await hostRevision(), 6, 'the host still holds r6');
      equal(await page.locator('.tile[data-id=cum]').count(), 0, 'the refused tile is not on the board');
      await page.evaluate(() => document.querySelector('.cbody').scrollTo(0, 0));
      await shot(page, 'error');
    });

    // 9 -------------------------------------------------------------------------------------------------- conflict
    await step('conflict: another session saved first — nothing is overwritten, the person is told what was not saved', async () => {
      const current = (await api('')).body;
      const other = structuredClone(current.document);
      other.parts.rev.title = 'Monthly revenue (finance view)';
      const saved = await fetch(`${origin}/uboard/boards/${boardId}?expected=${current.revision}`, { method: 'PUT', headers: { 'content-type': 'application/json', 'X-Sample-User': 'finance' }, body: JSON.stringify(other) });
      equal(saved.status, 200, 'the other session saved');
      model.load(CONFLICT);
      await say_('Make the target line red.');
      equal(await page.locator('[data-testid=banner-conflict]').count(), 1, 'conflict banner');
      check((await page.textContent('[data-testid=banner-conflict]')).includes('Another session saved r7 while you were on r6'), await page.textContent('[data-testid=banner-conflict]'));
      const rows = await lastSteps();
      const board = rows.find(r => r.kind === 'board');
      equal(board.outcome, 'conflict', 'conflict outcome');
      check(/^PUT \/uboard\/boards\/revenue-overview-[0-9a-f]{4}\?expected=6 → 409 Conflict$/.test(board.raw), board.raw);
      const reply = await deepTextOf('#msgs [data-role=assistant]:last-of-type [data-testid=reply]');
      check(reply.includes("wasn't saved") && reply.includes('r7'), reply);
      equal(await hostRevision(), 7, 'the other session\'s r7 stands');
      equal((await api('')).body.document.parts.rev.referenceLines[0].color, '#c92a2a', 'the refused change did not land');
      await page.evaluate(() => document.querySelector('.cbody').scrollTo(0, 0));
      await shot(page, 'conflict');
    });

    await step('conflict: reload the new revision and the same edit is applied on top of it', async () => {
      model.load(RETRY);
      const before = await replies();
      await page.click('.chip[data-chip="Reload r7 and retry"]');
      await settle(before + 0);
      await page.waitForFunction(() => !document.querySelector('u-prompt').loading);
      await page.waitForFunction(() => document.querySelector('[data-testid=pill-draft]').textContent === 'Draft r8');
      const saved = (await api('')).body;
      equal(saved.revision, 8, 'host at r8');
      equal(saved.document.parts.rev.referenceLines[0].color, '#d92d20', 'the edit landed');
      equal(saved.document.parts.rev.title, 'Monthly revenue (finance view)', 'the other session\'s change is kept');
      equal(await page.locator('[data-testid=banner-conflict]').count(), 0, 'the banner is gone');
    });

    // 9b ------------------------------------------------------------------------------------------------- variables
    await step('board variable: a filter for the whole board is one variable — changing it above the board asks the reads again', async () => {
      model.load(VARIABLE_FILTER);
      await say_('Let me pick the order status for the whole board.');
      equal(await hostRevision(), 9, 'host at r9');
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
      equal(await hostRevision(), 9, 'choosing a value saves nothing');
      await shot(page, 'variables');
    });

    await step('table paging on the server: a skip variable walks the rows, the board holds one page at a time', async () => {
      model.load(VARIABLE_PAGING);
      await say_('Page through the latest orders.');
      equal(await hostRevision(), 10, 'host at r10');
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
      equal(await hostRevision(), 11, 'host at r11');
      equal((await api('')).body.document.parts.rev.as, 'chart.line', 'the pointed part changed');
      check((await page.locator('[data-role=user]').last().locator('[data-testid=user-on]').textContent()).includes('Monthly revenue'), 'the message says what it was about');
      equal(await page.locator('[data-testid=selection-chip]').count(), 0, 'pointing is cleared once the message is sent');
      equal(await page.locator('.tile[data-selected=true]').count(), 0, 'no tile stays selected');
    });

    // 10 ------------------------------------------------------------------------------------------------- history
    await step('history drawer: revisions r1…r11 with who saved them, compare shows what a revision changed', async () => {
      await page.click('header [data-act=history]');
      await page.waitForSelector('[data-testid=drawer]');
      equal(await page.locator('[data-testid=drawer] .dr').count(), 11, 'eleven revisions');
      check((await page.textContent('[data-testid=drawer] .dr[data-rev="11"]')).includes('draft'), 'r11 is the draft');
      await page.click('[data-testid=drawer] .dr[data-rev="3"] [data-act=compare]');
      const text = await page.textContent('[data-testid=compare]');
      check(text.includes('rev (edited)') && text.includes('top (new)') && text.includes('region (new)'), text);
      await shot(page, 'history');
    });

    await step('publish r3, then r4 — readers follow the pointer; republishing r3 is the rollback', async () => {
      const publishFromDrawer = async n => {
        await page.click(`[data-testid=drawer] .dr[data-rev="${n}"] [data-act=publish]`);
        await page.waitForSelector('[data-testid=publish-modal]');
        await page.click('[data-act=confirmpub]');
        await page.waitForFunction(r => document.querySelector('[data-testid=pill-published]')?.textContent === `Published r${r}`, n);
      };
      await publishFromDrawer(3);
      equal((await api('/published')).body.revision, 3, 'published r3');
      check((await page.textContent('[data-testid=banner-notice]')).includes('Published r3'), 'the board says what was published');
      await page.click('header [data-act=history]');
      await publishFromDrawer(4);
      equal((await api('/published')).body.revision, 4, 'published r4');
      check((await deepTextOf('#msgs')).includes('r3 is one click away if you need to roll back'), 'the chat says how to roll back');
      await page.click('header [data-act=history]');
      await publishFromDrawer(3);
      equal((await api('/published')).body.revision, 3, 'rolled back to r3');
      const history = (await api('/publications')).body;
      equal(history.length, 3, 'the host kept three publication events');
      check((await page.textContent('[data-testid=readers-note]')).includes('Readers see r3 · you are editing r11'), await page.textContent('[data-testid=readers-note]'));
    });

    await step('a person who may only read: the board opens, the chat answers only, no edit tool is offered, and nothing to publish or restore', async () => {
      const readOnly = await browser.newContext({ viewport: { width: 1440, height: 900 }, extraHTTPHeaders: { 'X-Sample-User': 'viewer' } });
      await watch(readOnly);
      const viewer = await readOnly.newPage();
      watchConsole(viewer);
      await viewer.goto(`${origin}${SCREEN}/boards/${boardId}`);
      await viewer.waitForSelector('[data-testid=pill-readonly]');
      equal(await viewer.locator('[data-testid=banner-invalid]').count(), 0, 'a sound draft is drawn');
      check(await viewer.locator('[data-testid=grid] .tile').count() > 0, 'the board is drawn for a person who may only read');
      equal(await viewer.locator('header [data-act=publish]').count(), 0, 'no Publish');
      equal(await viewer.locator('header [data-act=reader]').count(), 0, 'no preview as reader');
      check((await viewer.textContent('.ctx')).includes('Read only — answers only, no edits'), 'the chat says it is answers only');
      equal(await viewer.locator('u-prompt textarea').getAttribute('placeholder'), 'Ask a question about this board…', 'the prompt invites a question');
      await viewer.click('header [data-act=history]');
      await viewer.waitForSelector('[data-testid=drawer]');
      equal(await viewer.locator('[data-testid=drawer] [data-act=publish], [data-testid=drawer] [data-act=restore]').count(), 0, 'no publish and no restore in the history');
      check(await viewer.locator('[data-testid=drawer] [data-act=compare]').count() > 0, 'compare is reading, it stays');
      await viewer.click('[data-testid=drawer] [data-act=closehist]');
      model.load([say('The board shows revenue by month, and I cannot change it for you.')]);
      const before = model.requests.length;
      await viewer.locator('u-prompt textarea').fill('What does this show?');
      await viewer.locator('u-prompt textarea').press('Enter');
      await viewer.waitForFunction(() => document.querySelectorAll('[data-role=assistant] [data-testid=reply]').length > 0 && !document.querySelector('u-prompt').loading, null, { timeout: 30000 });
      const sent = model.requests.slice(before);
      equal(sent.length, 1, 'the model was asked once');
      check(!sent[0].body.tools.some(tool => tool.function.name === 'patch_board'), 'the model was offered no tool that edits');
      check(sent[0].body.tools.some(tool => tool.function.name === 'query_data'), 'it may still read');
      equal(await viewer.locator('[data-testid=answer-only]').count(), 1, 'the answer is labelled');
      // The routes are the ones that decide: the page left the buttons out, and the host would refuse them anyway.
      const refused = await fetch(`${origin}/uboard/boards/${boardId}?expected=1`, { method: 'PUT', headers: { 'X-Sample-User': 'viewer', 'content-type': 'application/json' }, body: '{}' });
      equal(refused.status, 403, 'an edit by the viewer is refused by the host');
      const viewerList = await readOnly.newPage();
      watchConsole(viewerList);
      await viewerList.goto(`${origin}${SCREEN}`);
      await viewerList.waitForSelector('[data-testid=board-list] table');
      equal(await viewerList.locator('[data-act=newboard]').count(), 0, 'no new-board button for a person who may not make boards');
      check((await viewerList.textContent('[data-testid=list-sub]')).includes('Creating boards is not enabled'), 'the list says why');
      check((await viewerList.textContent('tr[data-board] .acc')).includes('Read only'), 'the row says read only');
      await shot(viewer, 'readonly');
      await readOnly.close();
    });

    // 11 ------------------------------------------------------------------------------------------------- reader
    await step('reader view: the published revision only — no chat, no catalog, no edit marks', async () => {
      await page.click('header [data-act=reader]');
      await page.waitForSelector('[data-testid=reader][data-reader-state=published]');
      equal(await page.locator('.chat').count(), 0, 'no chat');
      equal(await page.locator('[data-testid=grid] .tile').count(), 6, 'r3 has six tiles');
      equal(await page.locator('.tile .pill').count(), 0, 'no edit badges');
      equal(await pill('pill-published'), 'Published r3', 'pill');
      for (let i = 0; i < 40 && !(await tileText('top')).includes('Delta Max'); i++) await sleep(250);
      check((await tileText('top')).includes('Delta Max'), 'the reader sees live values');
      await page.waitForTimeout(500);
      await shot(page, 'reader');
    });

    await step('reader fallback: when the newest publication fails the viewer\'s check, the last good one is shown and the reader is told', async () => {
      const current = (await api('')).body;
      const broken = structuredClone(current.document);
      broken.parts.rev.colour = 'red'; // an option the board language does not have: the host stores it, the viewer's check refuses it
      const saved = await fetch(`${origin}/uboard/boards/${boardId}?expected=${current.revision}`, { method: 'PUT', headers: { 'content-type': 'application/json', 'X-Sample-User': 'analyst' }, body: JSON.stringify(broken) });
      equal(saved.status, 200, 'the host stores a document its viewer refuses');
      const revision = (await saved.json()).revision;
      equal((await fetch(`${origin}/uboard/boards/${boardId}/publish?revision=${revision}`, { method: 'POST', headers: { 'X-Sample-User': 'analyst' } })).status, 200, 'published');
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
      const turn = (headers, body) => fetch(`${origin}/uboard/boards/${boardId}/turn`, { method: 'POST', headers: { 'X-Sample-User': 'analyst', 'content-type': 'application/json', ...headers }, body: typeof body === 'string' ? body : JSON.stringify(body) }).then(r => r.status);
      const ok = { message: 'hi', tools: [] };
      equal(await turn({ 'Sec-Fetch-Site': 'cross-site' }, ok), 403, 'another site');
      equal(await turn({ Origin: 'http://elsewhere.example' }, ok), 403, 'another origin');
      equal(await turn({ 'content-type': 'text/plain' }, 'message=hi'), 415, 'not JSON');
      equal(await turn({}, { ...ok, tools: [{ name: 'two words', schema: {} }] }), 400, 'a tool that is not a plain name');
      equal(await turn({}, { message: 'x'.repeat(1 << 20), tools: [] }), 413, 'over 1 MiB');
      equal(await turn({}, { ...ok, results: [] }), 400, 'a message and results together');
      equal(model.requests.length, asked, 'none of them reached the model');
    });

    await step('a draft the board check refuses is told and not drawn, and the assistant says it cannot work on it without asking the model', async () => {
      const readOnly = await browser.newContext({ viewport: { width: 1440, height: 900 }, extraHTTPHeaders: { 'X-Sample-User': 'analyst' } });
      await watch(readOnly);
      const broken = await readOnly.newPage();
      watchConsole(broken);
      await broken.goto(`${origin}${SCREEN}/boards/${boardId}`);
      await broken.waitForSelector('[data-testid=banner-invalid]');
      equal(await broken.locator('[data-testid=grid] .tile').count(), 0, 'nothing of it is drawn');
      check((await broken.textContent('[data-testid=banner-invalid]')).includes('does not pass the board check'), 'the banner says why');
      const before = model.requests.length;
      await broken.locator('u-prompt textarea').fill('Can you fix it?');
      await broken.locator('u-prompt textarea').press('Enter');
      await broken.waitForFunction(() => document.querySelectorAll('[data-role=assistant] [data-testid=reply]').length > 0, null, { timeout: 30000 });
      equal(model.requests.length, before, 'the model was not asked about a board it cannot be given');
      await broken.click('header [data-act=history]');
      await broken.waitForSelector('[data-testid=drawer]');
      check(await broken.locator('[data-testid=drawer] [data-act=restore]').count() > 0, 'an earlier revision can be restored from the history');
      await readOnly.close();
    });

    await step('a question is answered and the board is not changed; the answer says so', async () => {
      await page.click('[data-act=editor]');
      await page.waitForSelector('[data-testid=chat]');
      model.load([say('Revenue is highest in the east, and the board already shows it.')]);
      const revision = await hostRevision();
      await say_('Which region is highest?');
      equal(await page.locator('#msgs [data-role=assistant]:last-of-type [data-testid=answer-only]').count(), 1, 'the answer is labelled as one');
      equal(await hostRevision(), revision, 'nothing was saved');
      check(model.requests.at(-1).body.tools.some(tool => tool.function.name === 'patch_board'), 'an editor is offered the edit tool, a question just does not use it');
    });


    await step('the list shows the board with what this person may do to it; a board the host does not hold is not there', async () => {
      await page.goto(`${origin}${SCREEN}`);
      await page.waitForSelector('[data-testid=board-list] tr[data-board]');
      const row = page.locator(`tr[data-board="${boardId}"]`);
      check((await row.textContent()).includes('Revenue overview') && (await row.textContent()).includes('Can edit'), await row.textContent());
      check((await row.textContent()).includes('r11') && (await row.textContent()).includes('r3'), 'latest and published revisions');
      await page.goto(`${origin}${SCREEN}/boards/not-a-board`);
      await page.waitForSelector('[data-testid=missing]');
      await shot(page, 'list');
    });

    await step('the files the page loads are the library\'s: hashed, kept for good, with the notices beside them', async () => {
      const html = await (await fetch(`${origin}${SCREEN}`, { headers: { 'X-Sample-User': 'analyst' } })).text();
      const files = [...html.matchAll(/(?:src|href)="\.\/(assets\/[^"]+)"/g)].map(m => m[1]);
      check(files.length >= 2, `the page names its files: ${files}`);
      for (const file of files) {
        const response = await fetch(`${origin}${SCREEN}/${file}`, { headers: { 'X-Sample-User': 'analyst' } });
        equal(response.status, 200, file);
        check((response.headers.get('cache-control') ?? '').includes('immutable'), `${file} is kept for good`);
      }
      const notices = await fetch(`${origin}${SCREEN}/third-party-notices.txt`, { headers: { 'X-Sample-User': 'analyst' } });
      check((await notices.text()).includes('pretendard'), 'the notices travel with the screen');
      equal((await fetch(`${origin}${SCREEN}/assets/../index.html`)).status === 200, false, 'a path outside the files is not a file');
      equal((await fetch(`${origin}${SCREEN}/boards/${boardId}`, { headers: { 'Sec-Fetch-Site': 'cross-site', 'X-Sample-User': 'analyst' } })).status, 403, 'another site may not load a page');
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
      equal(policyProblems.length, 0, `the policy refused something: ${policyProblems.join('; ')}`);
      const violations = await page.evaluate(() => window.__violations ?? []);
      equal(violations.length, 0, `policy violations on the page: ${violations.join('; ')}`);
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
