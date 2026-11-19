// A scripted world for the assistant loop: a model that plays back canned replies, and a host that keeps one board, answers the
// catalog and OData, and records every request it was sent. No network, no clock, no page.
import { newBoard } from '../src/session.js';

const prop = (name, type, extra = {}) => ({ name, type, nullable: false, readable: true, ...extra });

/** The catalog of a small sales domain, with one locked field per sensitive entity (the host's opt-in marking is on). */
export const CATALOG = {
  entities: [
    {
      name: 'Orders',
      properties: [prop('Id', 'int'), prop('OrderDate', 'date'), prop('Status', 'enum'), prop('Total', 'decimal'), prop('Margin', 'decimal', { readable: false })],
      links: [{ name: 'Region', target: 'Regions', collection: false }, { name: 'Customer', target: 'Customers', collection: false }],
    },
    { name: 'Regions', properties: [prop('Id', 'int'), prop('Name', 'text')], links: [] },
    { name: 'Customers', properties: [prop('Id', 'int'), prop('Name', 'text'), prop('Email', 'text', { readable: false })], links: [] },
  ],
};

/** The same host with the opt-in off: a locked field is simply not in the list. */
export const CATALOG_PLAIN = {
  entities: CATALOG.entities.map(entity => ({ ...entity, properties: entity.properties.filter(p => p.readable !== false) })),
};

export const json = (status, body, headers = {}) => {
  const named = new Map(Object.entries(headers).map(([name, value]) => [name.toLowerCase(), String(value)]));
  return { ok: status >= 200 && status < 300, status, headers: { get: name => named.get(name.toLowerCase()) ?? null }, json: async () => body, text: async () => JSON.stringify(body) };
};

// ---- the model's side
const call = (id, name, args) => ({ id, type: 'function', function: { name, arguments: typeof args === 'string' ? args : JSON.stringify(args) } });
export const say = text => ({ choices: [{ message: { role: 'assistant', content: text } }] });
/** One assistant message carrying tool calls: `calls(['read_catalog', {}], ['patch_board', { patch }])`. */
export const calls = (...list) => ({ choices: [{ message: { role: 'assistant', content: null, tool_calls: list.map(([name, args], i) => call(`c${i + 1}`, name, args)) } }] });

// ---- a first board: a KPI and a line, reading the host's OData
export const FIRST_PATCH = [
  { op: 'add', path: '/data', value: {
    kpi: { from: 'odata', path: '/$data/Orders?$apply=aggregate(Total with sum as Revenue)', fields: { Revenue: { at: '/value/0/Revenue', label: 'Revenue' } } },
    monthly: { from: 'odata', path: '/$data/Orders?$apply=groupby((OrderDate),aggregate(Total with sum as Revenue))', list: '/value', key: 'OrderDate' },
  } },
  { op: 'add', path: '/parts/revenue', value: { as: 'stat', of: 'kpi', show: 'Revenue', title: 'Revenue' } },
  { op: 'add', path: '/parts/trend', value: { as: 'chart.line', of: 'monthly', title: 'Monthly revenue', x: 'OrderDate', y: 'Revenue' } },
  { op: 'add', path: '/layout/items/-', value: ['revenue', 4] },
  { op: 'add', path: '/layout/items/-', value: ['trend', 8] },
];

/** A patch the board check refuses: a kind the language does not have. */
export const badPatch = id => [
  { op: 'add', path: `/parts/${id}`, value: { as: 'chart.donut-3d', title: 'Nope' } },
  { op: 'add', path: '/layout/items/-', value: id },
];

export const REVENUE_ANSWER = { value: [{ Revenue: 1234.5 }] };
export const MONTHLY_ANSWER = { value: [{ OrderDate: '2026-01', Revenue: 400 }, { OrderDate: '2026-02', Revenue: 834.5 }] };

/** The host's data as the small sales domain answers it: a total is one row, a grouping by month is a row per month. */
export const answerData = url => json(200, url.includes('groupby((OrderDate)') ? MONTHLY_ANSWER : REVENUE_ANSWER);

/** What the tool results the model is about to read say: the JSON of each message that follows its last answer, in order (a result that is not JSON is its text). */
const lastToolResults = messages => {
  const results = [];
  for (let i = messages.length - 1; i >= 0 && messages[i].role === 'tool'; i--) {
    try {
      results.unshift(JSON.parse(messages[i].content));
    } catch {
      results.unshift(messages[i].content);
    }
  }
  return results;
};

/**
 * `model` is the queue of replies: a reply is a message (`say`, `calls`), or a function of what the model has just been shown — `({ messages, toolResults }) => reply` —
 * for a model that answers what the tools said rather than a fixed script. `odata(url)` answers a read of /$data (default: one aggregate row).
 * The host keeps one board; `world.moveOn()` is another session saving it.
 */
export function makeWorld({ model = [], catalog = CATALOG, odata = answerData, catalogStatus = 200, modelStatus } = {}) {
  const requests = [];
  const queue = [...model];
  const host = { revision: 0, document: undefined, name: 'Revenue overview' };

  const envelope = () => ({ id: 'revenue', name: host.name, revision: host.revision, publishedRevision: null, document: host.document });

  // The host's turn route, played here: it keeps nothing, takes the conversation back as the page holds it (the replies are in the model's
  // own wire shape, which the page never reads) and says what the model was asked, so a test can read the same messages and tools a model sees.
  const answerTurn = request => {
    const { system, history = [], message, results, final, tools = [] } = request.body;
    const messages = [...history, ...(message === undefined ? results.map(r => ({ role: 'tool', tool_call_id: r.callId, content: r.output })) : [{ role: 'user', content: message }])];
    const asked = history.at(-1)?.tool_calls ?? [];
    const answered = new Set(messages.filter(m => m.role === 'tool').map(m => m.tool_call_id));
    if (message !== undefined && asked.some(c => !answered.has(c.id))) throw new Error('the history holds a tool call without its result');
    if (final !== undefined) return { stop: 'Completed', text: final, history: [...messages, { role: 'assistant', content: final }] };
    request.model = { messages: [{ role: 'system', content: system }, ...messages], tools: tools.map(t => ({ type: 'function', function: { name: t.name, description: t.description, parameters: t.schema } })) };
    const next = queue.shift();
    if (!next) throw new Error('the scripted model has no reply left');
    const reply = typeof next === 'function' ? next({ messages, toolResults: lastToolResults(messages) }) : next;
    const said = reply.choices[0].message;
    const toolCalls = (said.tool_calls ?? []).map(c => ({ id: c.id, name: c.function.name, arguments: c.function.arguments }));
    return { stop: toolCalls.length ? 'AwaitingHostTools' : 'Completed', text: said.content ?? '', toolCalls, history: [...messages, { role: 'assistant', content: said.content ?? null, ...(said.tool_calls ? { tool_calls: said.tool_calls } : {}) }] };
  };

  const fetch = async (url, init = {}) => {
    if (init.signal?.aborted) throw new DOMException('This operation was aborted', 'AbortError'); // a request made after stop never leaves the page
    const request = { url, method: init.method ?? 'GET', headers: init.headers ?? {}, body: init.body === undefined ? undefined : JSON.parse(init.body) };
    requests.push(request);
    if (url === '/uboard/boards/revenue/turn') {
      if (modelStatus) return json(modelStatus, { error: modelStatus === 429 ? 'ratelimit' : 'unknown' });
      return json(200, answerTurn(request));
    }
    if (url === '/uboard/catalog') return json(catalogStatus, catalog);
    if (url.startsWith('/$data/')) return odata(url);
    if (url.startsWith('/uboard/boards/revenue')) {
      if (request.method === 'POST') {
        if (host.revision > 0) return json(409, envelope()); // the board exists: the host answers with it
        host.revision = 1;
        host.document = request.body;
        return json(201, envelope());
      }
      const expected = Number(new URL(url, 'http://host').searchParams.get('expected'));
      if (expected !== host.revision) return json(409, envelope());
      host.revision += 1;
      host.document = request.body;
      return json(200, envelope());
    }
    return json(404, { error: `no route for ${url}` });
  };

  return {
    fetch,
    requests,
    host,
    queue,
    /** The calls that reached the model: each as the model's own request (the system rules, the messages, the tools). */
    modelRequests: () => requests.filter(r => r.model).map(r => ({ ...r, body: r.model })),
    dataRequests: () => requests.filter(r => r.url.startsWith('/$data/')),
    boardWrites: () => requests.filter(r => r.url.startsWith('/uboard/boards/') && !r.url.endsWith('/turn')),
    /** Another session saves the board: the host moves on without this page knowing. */
    moveOn() {
      host.revision += 1;
    },
    assistantDeps: (extra = {}) => ({
      fetch,
      board: { id: 'revenue', loaded: newBoard('Revenue overview') },
      ...extra,
    }),
  };
}

/** Records every event the loop emits. */
export function recorder() {
  const events = [];
  return { events, emit: event => events.push(event), ofType: type => events.filter(event => event.type === type) };
}
