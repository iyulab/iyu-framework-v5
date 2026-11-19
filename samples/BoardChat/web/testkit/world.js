// A scripted world for the assistant loop: a model that plays back canned replies, and a host that keeps one board, answers the
// catalog and OData, and records every request it was sent. No network, no clock, no page.
import { newBoard } from '../src/session.js';

const prop = (name, type, extra = {}) => ({ name, type, nullable: false, readable: true, ...extra });

/** The catalog of the sample's domain, with one locked field per sensitive entity (the host's opt-in marking is on). */
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

export const json = (status, body) => ({ ok: status >= 200 && status < 300, status, json: async () => body, text: async () => JSON.stringify(body) });

// ---- the model's side
const call = (id, name, args) => ({ id, type: 'function', function: { name, arguments: typeof args === 'string' ? args : JSON.stringify(args) } });
export const say = text => ({ choices: [{ message: { role: 'assistant', content: text } }] });
/** One assistant message carrying tool calls: `calls(['read_catalog', {}], ['patch_board', { patch }])`. */
export const calls = (...list) => ({ choices: [{ message: { role: 'assistant', content: null, tool_calls: list.map(([name, args], i) => call(`c${i + 1}`, name, args)) } }] });

// ---- a first board: a KPI and a line, reading the host's OData
export const FIRST_PATCH = [
  { op: 'add', path: '/data', value: {
    kpi: { from: 'odata', path: '/$data/Orders?$apply=aggregate(Total with sum as Revenue)', list: '/value', fields: { Revenue: { label: 'Revenue' } } },
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

/**
 * `model` is the queue of replies. `odata(url)` answers a read of /$data (default: one aggregate row).
 * The host keeps one board; `world.moveOn()` is another session saving it.
 */
export function makeWorld({ model = [], catalog = CATALOG, odata = () => json(200, REVENUE_ANSWER), catalogStatus = 200, modelStatus } = {}) {
  const requests = [];
  const queue = [...model];
  const host = { revision: 0, document: undefined, name: 'Revenue overview' };

  const envelope = () => ({ id: 'revenue', name: host.name, revision: host.revision, publishedRevision: null, document: host.document });

  const fetch = async (url, init = {}) => {
    const request = { url, method: init.method ?? 'GET', headers: init.headers ?? {}, body: init.body === undefined ? undefined : JSON.parse(init.body) };
    requests.push(request);
    if (url === '/uboard/model/chat/completions') {
      if (modelStatus) return json(modelStatus, { error: 'upstream' });
      const reply = queue.shift();
      if (!reply) throw new Error('the scripted model has no reply left');
      return json(200, reply);
    }
    if (url === '/uboard/catalog') return json(catalogStatus, catalog);
    if (url.startsWith('/$data/')) return odata(url);
    if (url.startsWith('/uboard/boards/revenue')) {
      if (request.method === 'POST') {
        host.revision = 1;
        host.document = request.body;
        return json(200, envelope());
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
    modelRequests: () => requests.filter(r => r.url === '/uboard/model/chat/completions'),
    dataRequests: () => requests.filter(r => r.url.startsWith('/$data/')),
    boardWrites: () => requests.filter(r => r.url.startsWith('/uboard/boards/')),
    /** Another session saves the board: the host moves on without this page knowing. */
    moveOn() {
      host.revision += 1;
    },
    assistantDeps: (extra = {}) => ({
      fetch,
      userName: 'dev',
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
