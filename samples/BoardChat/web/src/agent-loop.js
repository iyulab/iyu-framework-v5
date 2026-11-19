// The assistant's tool runtime — the part of the chat panel that runs the model's tool calls and keeps the board honest. The model, the
// conversation and the loop around them are the host's (IronHive.Agent behind one route); every tool here is a tool the host declared
// without an implementation, because only this page can check a board edit, save it and read as the signed-in person.
// It is a plain module with no page in it: everything it touches (the network, the board session, the clock) is handed in, so a
// script can play the model and the host and the whole conversation can be tested without a browser.
//
// TODO(move-to-library: the loop belongs with the viewer assets in Iyu.UBoard — a host should switch the assistant on, not
// carry this file. It stays in the sample until the library ships viewer assets, which is a dependency decision still to be taken.)
//
// What the loop guarantees (the screens depend on each of these):
//   - The page never names the model or lifts its limits: a request carries the conversation, the rules text and the tools, nothing else.
//   - Every step is announced pending -> running -> done | error, with the raw call that was made. The text shown for a step and
//     the request that was sent are the same string, built once.
//   - A board edit the check refuses is shown to the model once; refused again, nothing is saved and the person is told where.
//   - A save the host refuses because the board moved on is a structured conflict, never a bare string.
//   - A read of something the person cannot read stops before any request. A read that would return rows without a page, or
//     more than MAX_ROWS of them, is refused with advice; tables read one page of TABLE_PAGE rows.
//   - The number of model calls in one turn is capped. A turn the page ends itself is closed with the words it said, so the conversation
//     the host keeps never holds a tool call without its result.
import { applyJsonPatch } from '@iyulab/u-board/domain';
import { DATA_SOURCE_ID, buildSystemPrompt, workflowRules } from './prompt.js';
import { changedParts, partsTouched, removedParts } from './board-diff.js';
import { BoardMovedOn, newBoard, openBoardSession } from './session.js';
import { contextBlock, editContext } from './edit-context.js';
import { fillVariables, variableDefaults, variablesMentioned } from './variables.js';
import {
  DATA_ROOT, MAX_ROWS, TABLE_PAGE, buildODataUrl, bulkProblem, catalogForModel, closest, parseODataUrl, permissionProblem,
} from './odata.js';

export { DATA_SOURCE_ID, buildSystemPrompt, workflowRules, changedParts, removedParts, partsTouched, newBoard, openBoardSession, BoardMovedOn };
export { buildODataUrl, parseODataUrl, bulkProblem, permissionProblem, DATA_ROOT, MAX_ROWS, TABLE_PAGE };

export const DEFAULT_ENDPOINTS = {
  turn: '/assistant/turn',
  catalog: '/uboard/catalog',
  boards: '/uboard/boards',
};
const USER_HEADER = 'X-Sample-User';
const MAX_TOOL_TEXT = 12000;
const MUTATING = new Set(['patch_board']); // edit_text is not offered: it would change a read's path without the read rules looking at it

// ---------------------------------------------------------------- the two tools of this sample

const QUERY_DATA = {
  type: 'function',
  function: {
    name: 'query_data',
    description:
      `Reads the host's data over OData, as the signed-in person — only what they may read. Aggregate on the server first: give apply ` +
      `(groupby/aggregate) and read the groups. Rows only as a page: top at most ${MAX_ROWS} (a table uses top ${TABLE_PAGE} with count true). ` +
      `A read without apply and without top is refused.`,
    parameters: {
      type: 'object',
      properties: {
        entity: { type: 'string', description: 'The entity set, as read_catalog names it, e.g. Orders.' },
        apply: { type: 'string', description: '$apply, e.g. groupby((Region/Name),aggregate(Total with sum as Revenue))' },
        filter: { type: 'string', description: "$filter, e.g. Status eq 'Paid'" },
        select: { type: 'array', items: { type: 'string' }, description: '$select: fields of the entity' },
        expand: {
          type: 'array',
          description: '$expand: a link of the entity, with the fields of the target to take',
          items: { type: 'object', properties: { link: { type: 'string' }, select: { type: 'array', items: { type: 'string' } } }, required: ['link'] },
        },
        orderby: { type: 'string', description: "$orderby, e.g. Revenue desc" },
        top: { type: 'integer', minimum: 1, maximum: MAX_ROWS },
        skip: { type: 'integer', minimum: 0 },
        count: { type: 'boolean', description: 'Ask for the total count ($count=true) — for a paged table.' },
      },
      required: ['entity'],
      additionalProperties: false,
    },
  },
};

const READ_CATALOG = {
  type: 'function',
  function: {
    name: 'read_catalog',
    description: 'The entities, fields and links this person can read from the host. Call it first; use only these names.',
    parameters: { type: 'object', properties: {}, additionalProperties: false },
  },
};

/** The tool definitions a request carries: the board tools of the session plus this sample's two. */
export const toolDefinitions = session => [READ_CATALOG, QUERY_DATA, ...session.functionTools];

/** What the model sent for query_data, checked and turned into a query; or what is wrong with it. */
function queryFromArguments(args) {
  if (typeof args !== 'object' || args === null || Array.isArray(args)) return { error: 'the arguments must be an object' };
  const known = ['entity', 'apply', 'filter', 'select', 'expand', 'orderby', 'top', 'skip', 'count'];
  const extra = Object.keys(args).find(key => !known.includes(key));
  if (extra) return { error: `unknown argument "${extra}" — query_data takes ${known.join(', ')}` };
  if (typeof args.entity !== 'string' || !/^[A-Za-z_][A-Za-z0-9_]*$/.test(args.entity)) return { error: 'entity must be an entity set name such as Orders' };
  for (const key of ['apply', 'filter', 'orderby'])
    if (args[key] !== undefined && typeof args[key] !== 'string') return { error: `${key} must be a string` };
  if (args.select !== undefined && !(Array.isArray(args.select) && args.select.every(item => typeof item === 'string'))) return { error: 'select must be a list of field names' };
  if (args.expand !== undefined && !(Array.isArray(args.expand) && args.expand.every(item => item && typeof item.link === 'string' && (item.select === undefined || Array.isArray(item.select)))))
    return { error: 'expand must be a list of { link, select? }' };
  for (const key of ['top', 'skip'])
    if (args[key] !== undefined && !Number.isInteger(args[key])) return { error: `${key} must be a whole number` };
  if (args.count !== undefined && typeof args.count !== 'boolean') return { error: 'count must be true or false' };
  return { query: args };
}

// ---------------------------------------------------------------- helpers

const clip = text => (text.length > MAX_TOOL_TEXT ? `${text.slice(0, MAX_TOOL_TEXT)}… (cut: ${text.length - MAX_TOOL_TEXT} more characters)` : text);
/** A long message cut for a line of chat. */
const short = (text, n = 140) => (text.length > n ? `${text.slice(0, n - 1)}…` : text);
const asText = value => (typeof value === 'string' ? value : JSON.stringify(value));
const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
const result = (value, isError = false, extra = {}) => ({ text: clip(asText(value)), isError, ...extra });

function parseJson(text) {
  try {
    return { value: JSON.parse(text) };
  } catch {
    return { error: true };
  }
}

/** A «did you mean» from the board check's own words: `unknown kind "bogus" — one of a, b, c`. */
function suggestionFor(issues, catalog) {
  const issue = issues[0];
  if (!issue) return 'Tell me what to change and I will try a different approach.';
  const quoted = /"([^"]+)"[^"]*?(?:one of|expected)\s+([^\n]+)$/.exec(issue.message);
  if (quoted) {
    const options = quoted[2].split(/,\s*/).map(option => option.replace(/["“”]/g, '').trim()).filter(Boolean);
    const near = closest(quoted[1], options);
    if (near) return `Did you mean ${near}?`;
  }
  const word = /"([^"]+)"/.exec(issue.message)?.[1];
  if (word && catalog) {
    const fields = catalog.entities.flatMap(entity => entity.properties.filter(p => p.readable !== false).map(p => `${entity.name}.${p.name}`));
    const near = closest(word, fields);
    if (near) return `Did you mean ${near}?`;
  }
  return 'Tell me what to change and I will try a different approach.';
}

/** Problems with the reads a board would hold: the same rules as query_data, applied to what is about to be saved. A read may
 *  mention a board variable (`{{$status}}`, `$skip={{$skip}}`); it is judged with each variable at its default, and a mention of a
 *  variable the board does not declare, or one that has no default to stand in, is refused. */
export function boardReadProblem(document, catalog) {
  const defaults = variableDefaults(document.variables);
  for (const [name, read] of Object.entries(document.data ?? {})) {
    if (read?.from !== DATA_SOURCE_ID) continue;
    for (const variable of variablesMentioned(read.path)) {
      const spec = document.variables?.[variable];
      if (!spec) return { read: name, message: `read "${name}" mentions {{$${variable}}} but the board declares no variable "${variable}" — declare it under "variables" with its "options" and a "default".` };
      if (defaults[variable] === undefined) return { read: name, message: `variable "${variable}" needs a "default" (or "options" to take the first of) to stand in read "${name}".` };
      if (Array.isArray(spec.options) && !spec.options.map(String).includes(defaults[variable])) return { read: name, message: `the default of variable "${variable}" is ${defaults[variable]}, which is not one of its options.` };
    }
    const query = parseODataUrl(fillVariables(read.path ?? '', defaults));
    if (!query) return { read: name, message: `the path of read "${name}" must start with ${DATA_ROOT}/<Entity>.` };
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(query.entity)) return { read: name, message: `read "${name}" does not name an entity set.` };
    const bulk = bulkProblem(query);
    if (bulk) return { read: name, rule: bulk.rule, message: `read "${name}": ${bulk.message}` };
    if (catalog) {
      const denied = permissionProblem(catalog, query);
      if (denied) return { read: name, permission: denied, message: `read "${name}": ${denied.message}` };
    }
  }
  return undefined;
}

// ---------------------------------------------------------------- the assistant

/**
 * deps:
 *   fetch      (url, init) => Response — the page's fetch. Every request to the host goes through it.
 *   userName   the signed-in person; sent as X-Sample-User on every request to the host.
 *   board      { id, loaded }  loaded is the host's board { name, revision, document } or newBoard(name)
 *   endpoints  { model, catalog, boards } — where the host serves them (defaults: DEFAULT_ENDPOINTS)
 *   emit       (event) => void — what the screen listens to
 *   now        () => ms        — the clock
 *   maxRounds  most model calls in one turn (default 10)
 *   signal     an AbortSignal for the requests (a turn may bring its own: `send(text, { signal })` — the person pressed stop)
 */
export function createAssistant(deps) {
  const endpoints = { ...DEFAULT_ENDPOINTS, ...deps.endpoints };
  const emit = deps.emit ?? (() => {});
  const now = deps.now ?? (() => Date.now());
  const maxRounds = deps.maxRounds ?? 10;

  let signal = deps.signal; // the turn's own when it brings one
  const host = (url, init = {}) =>
    deps.fetch(url, {
      ...init,
      headers: { ...(deps.userName ? { [USER_HEADER]: deps.userName } : {}), ...init.headers },
      ...(signal ? { signal } : {}),
    });

  const messages = []; // the conversation as the host last returned it: opaque to the page, handed back with the next request
  let session = openBoardSession({ id: deps.board.id, loaded: deps.board.loaded, host, boards: endpoints.boards });
  let catalog; // kept for the session (a new sign-in is a new assistant); a read_catalog step always asks the host again
  let turnNumber = 0;

  // One GET per catalog step: a step that says it read the catalog did.
  async function fetchCatalog(step) {
    if (step) step.call = { method: 'GET', url: endpoints.catalog };
    const response = await host(endpoints.catalog);
    if (!response.ok) throw new Error(`the catalog could not be read (${response.status})`);
    catalog = await response.json();
    emit({ type: 'catalog', catalog });
    return catalog;
  }

  // ---- one turn
  /** `options.selection`: the ids of the parts the person is pointing at; the ones the board has go to the model as an edit context. */
  async function send(userText, options = {}) {
    turnNumber += 1;
    signal = options.signal ?? deps.signal;
    const turn = { steps: [], rejections: 0, order: 0, emptyResults: [], failure: undefined, halted: false };

    const startDocument = await session.document();
    const startRevision = session.known.revision;
    const context = editContext(startDocument, options.selection, { name: deps.board.loaded?.name, revision: startRevision });
    emit({ type: 'turn-start', turn: turnNumber, text: userText, ...(context && { selection: context.selection.map(s => s.id) }) });
    let pending = { message: context ? `${userText}

${contextBlock(context)}` : userText }; // what the next request carries: the person's words, then the results of the tools the answer asked for
    let lastDocument = startDocument;
    let finalText;
    let rounds = 0;

    const snapshot = step => JSON.parse(JSON.stringify(step));
    const emitStep = step => emit({ type: 'step', step: snapshot(step) });
    const makeStep = (tool, label, kind) => {
      const step = { id: `t${turnNumber}s${++turn.order}`, order: turn.order, tool, kind, label, detail: '', state: 'pending', call: null, raw: null, outcome: null, startedAt: null, endedAt: null };
      turn.steps.push(step);
      return step;
    };
    const setCall = (step, call, raw) => {
      step.call = call;
      step.raw = raw;
    };
    const noteStep = (label, detail, state = 'done', outcome = null) => {
      const step = makeStep('note', label, 'note');
      Object.assign(step, { detail, state, outcome, startedAt: now(), endedAt: now() });
      emitStep(step);
      return step;
    };

    // ---- running one tool call
    async function runCatalog(step) {
      const read = await fetchCatalog(step);
      const readable = read.entities.reduce((n, entity) => n + entity.properties.filter(p => p.readable !== false).length, 0);
      step.detail = `${read.entities.map(entity => entity.name).join(', ')} — ${plural(readable, 'field', 'fields')} you can read`;
      return result(catalogForModel(read));
    }

    async function runQuery(step, query) {
      const bulk = bulkProblem(query);
      if (bulk) {
        step.detail = 'refused before asking the server — it would return rows without a page';
        step.outcome = 'refused';
        return result({ refused: bulk.rule, message: bulk.message }, true);
      }
      if (!catalog) return result({ error: 'the catalog is not loaded — call read_catalog first' }, true);
      const denied = permissionProblem(catalog, query);
      if (denied) {
        step.state = 'done';
        step.outcome = 'blocked';
        step.label = 'Stopped before querying';
        step.detail = `${denied.message} Nothing was read or changed.`;
        turn.failure = { kind: 'permission', field: denied.field, reason: denied.reason, alternatives: denied.alternatives, message: denied.message };
        turn.halted = true;
        return result({ blocked: 'permission', field: denied.field, message: denied.message, alternatives: denied.alternatives }, true, { halt: true, notAnError: true });
      }
      const response = await host(step.call.url);
      if (!response.ok) {
        const reason = response.status === 401 || response.status === 403 ? 'the server says you may not read this' : (await response.text?.().catch(() => ''))?.slice(0, 400);
        step.detail = `the server answered ${response.status}`;
        return result({ status: response.status, error: reason || 'the read failed' }, true);
      }
      const body = await response.json();
      const rows = Array.isArray(body.value) ? body.value : body.value === undefined ? [] : [{ value: body.value }];
      const total = body['@odata.count'];
      const clean = rows.slice(0, MAX_ROWS).map(row => Object.fromEntries(Object.entries(row).filter(([key]) => !key.startsWith('@'))));
      if (rows.length === 0 && !(typeof total === 'number' && total > 0)) {
        step.outcome = 'empty';
        step.detail = '0 rows — nothing matches (not an error)';
        turn.emptyResults.push(step.id);
        return result({ rows: 0, empty: true, note: 'The read is valid and matched no rows. This is not an error: add the tile anyway with a title that says what is empty, or offer to widen the range.' });
      }
      step.detail = typeof total === 'number' ? `${plural(rows.length, 'row', 'rows')} of ${total.toLocaleString('en-US')}` : plural(rows.length, 'row', 'rows');
      return result({
        rows: clean,
        returned: rows.length,
        ...(typeof total === 'number' ? { total } : {}),
        ...(rows.length > MAX_ROWS ? { truncated: `only the first ${MAX_ROWS} of ${rows.length} are shown — add $orderby and $top` } : {}),
      });
    }

    async function runBoardTool(step, name, args) {
      const tool = session.tools[name];
      if (!tool) return result({ error: `no tool called ${name}` }, true);
      const { revision: _ignored, ...rest } = args; // the host keeps count of revisions, not the model
      const parsed = tool.inputSchema.safeParse(rest);
      if (!parsed.success)
        return result({ error: 'the arguments are not valid', issues: parsed.error.issues.map(issue => ({ path: issue.path.join('.'), message: issue.message })) }, true);
      const mutating = MUTATING.has(name);

      if (name === 'patch_board') {
        const current = await session.document();
        let candidate;
        try {
          candidate = applyJsonPatch(structuredClone(current), parsed.data.patch);
        } catch {
          candidate = undefined; // the tool says what is wrong with the patch
        }
        if (candidate) {
          if (!catalog) return result({ error: 'the catalog is not loaded — call read_catalog first' }, true);
          const problem = boardReadProblem(candidate, catalog);
          if (problem?.permission) {
            const denied = problem.permission;
            step.outcome = 'blocked';
            step.detail = `${denied.message} Nothing was changed.`;
            turn.failure = { kind: 'permission', field: denied.field, reason: denied.reason, alternatives: denied.alternatives, message: denied.message };
            turn.halted = true;
            step.label = 'Stopped before saving';
            return result({ blocked: 'permission', field: denied.field, message: denied.message, alternatives: denied.alternatives }, true, { halt: true, notAnError: true });
          }
          if (problem) {
            step.outcome = 'refused';
            step.detail = 'refused before saving — a read would return rows without a page';
            return result({ refused: problem.rule ?? 'read', message: problem.message }, true);
          }
        }
        emit({ type: 'tiles-loading', ids: partsTouched(parsed.data.patch, candidate ?? current) });
      }

      let answered;
      try {
        answered = await tool.run(parsed.data);
      } finally {
        if (name === 'patch_board') emit({ type: 'tiles-loading', ids: [] });
      }
      const text = answered.content.find(part => part.type === 'text')?.text ?? '';
      const body = parseJson(text).value;

      if (!mutating) return result(text, Boolean(answered.isError));

      const conflict = session.takeConflict();
      if (conflict) {
        step.outcome = 'conflict';
        step.detail = `save refused: the board moved to r${conflict.serverRevision} while I worked from r${session.known.revision}`;
        step.saveCall = session.lastSave;
        step.raw = `${session.lastSave.method} ${session.lastSave.url} → 409 Conflict`;
        turn.failure = {
          kind: 'conflict',
          serverRevision: conflict.serverRevision,
          workedFrom: session.known.revision,
          notSaved: { tool: name, args: parsed.data, parts: partsTouched(parsed.data.patch ?? [], lastDocument) },
          board: conflict.board,
          offer: 'reload',
          retryPrompt: `The board moved to r${conflict.serverRevision} while you worked from r${session.known.revision}. Apply my previous request again on top of the current board: "${userText}"`,
        };
        turn.halted = true;
        return result(text, true, { halt: true });
      }
      const failedSave = session.takeSaveFailure();
      if (failedSave) {
        step.detail = `the host refused the save (${failedSave.status})`;
        turn.failure = { kind: 'save-failed', status: failedSave.status, message: `The host refused to keep the change (${failedSave.status}). Nothing was saved.` };
        turn.halted = true;
        return result(text, true, { halt: true });
      }
      if (answered.isError) {
        const rejected = body && (body.code === 'INVALID_DOCUMENT' || body.code === 'INVALID_PATCH');
        if (rejected) {
          turn.rejections += 1;
          step.outcome = 'rejected';
          const issues = body.issues ?? [{ path: `operation ${body.operation}`, message: body.message }];
          step.detail = `the board check rejected the edit — ${issues[0].path}: ${short(issues[0].message)}`;
          if (turn.rejections >= 2) {
            turn.failure = {
              kind: 'rejected',
              issues: issues.slice(0, 5),
              path: issues[0].path,
              suggestion: suggestionFor(issues, catalog),
              revision: session.known.revision,
              notSaved: { tool: name, args: parsed.data },
            };
            turn.halted = true;
            return result(text, true, { halt: true });
          }
        } else step.detail = body?.error ?? 'the edit failed';
        return result(text, true);
      }

      // Saved.
      const after = await session.document();
      const marks = changedParts(lastDocument, after);
      const removed = removedParts(lastDocument, after);
      const revision = session.known.revision;
      step.label = revision === 1 && startRevision === 0 ? `Created the board · r${revision}` : `Edited the board · r${revision}`;
      const fresh = Object.values(marks).filter(mark => mark === 'new').length;
      const edited = Object.values(marks).filter(mark => mark === 'edited').length;
      step.detail = [fresh && `${plural(fresh, 'tile', 'tiles')} added`, edited && `${plural(edited, 'tile', 'tiles')} changed`, removed.length && `${removed.length} removed`].filter(Boolean).join(', ') || 'no tile changed';
      step.saveCall = session.lastSave;
      lastDocument = after;
      emit({ type: 'board', revision, document: after, changed: marks, removed });
      return result(text);
    }

    async function runTool(step, name, args) {
      if (name === 'read_catalog') return runCatalog(step);
      if (name === 'query_data') return runQuery(step, step.query);
      return runBoardTool(step, name, args);
    }

    // ---- the rounds
    while (!turn.halted && rounds < maxRounds) {
      rounds += 1;
      const document = await session.document();
      const response = await callTurn(document, pending);
      if (response.failure) {
        turn.failure = response.failure;
        turn.halted = true;
        break;
      }
      const answer = response.answer;
      messages.splice(0, messages.length, ...answer.history);
      pending = undefined;
      const toolCalls = (answer.toolCalls ?? []).map(call => ({ id: call.id, function: { name: call.name, arguments: call.arguments } }));
      if (!toolCalls.length) {
        finalText = (answer.text ?? '').trim();
        break;
      }
      const results = []; // what the next request carries

      // Plan: every call becomes a pending step before the first runs.
      const planned = [];
      let catalogPlanned = Boolean(catalog); // loaded earlier this turn, or read by a step that comes before the one that needs it
      for (const call of toolCalls) {
        const name = call.function?.name;
        if (name === 'read_catalog') catalogPlanned = true;
        const parsed = parseJson(call.function?.arguments || '{}');
        const args = parsed.value;
        const needsCatalog = name === 'query_data' || name === 'patch_board';
        if (needsCatalog && !catalogPlanned) {
          const step = makeStep('read_catalog', 'Read the catalog', 'catalog');
          setCall(step, { method: 'GET', url: endpoints.catalog }, `GET ${endpoints.catalog}`);
          planned.push({ call: undefined, step, name: 'read_catalog', args: {} });
          catalogPlanned = true;
        }
        const step = makeStep(name, labelFor(name, args, turn.rejections), kindFor(name));
        if (name === 'query_data' && parsed.value) {
          const checked = queryFromArguments(args);
          if (checked.query) {
            step.query = checked.query;
            const url = buildODataUrl(checked.query);
            setCall(step, { method: 'GET', url }, `GET ${url}`);
          }
        } else if (name === 'read_catalog') setCall(step, { method: 'GET', url: endpoints.catalog }, `GET ${endpoints.catalog}`);
        else if (MUTATING.has(name) && args) setCall(step, { tool: name, args }, `${name} ${JSON.stringify(args.patch ?? args.edits ?? args)}`);
        else if (args && name) setCall(step, { tool: name, args }, `${name} ${JSON.stringify(args)}`);
        planned.push({ call, step, name, args, parseError: parsed.error });
      }
      planned.forEach(item => emitStep(item.step));

      for (const item of planned) {
        const { call, step } = item;
        if (!turn.halted && signal?.aborted) {
          turn.failure = { kind: 'stopped', message: 'Stopped.' };
          turn.halted = true;
        }
        if (turn.halted) {
          step.state = 'error';
          step.outcome = 'skipped';
          step.detail = 'not run — the turn stopped';
          step.endedAt = now();
          emitStep(step);
          if (call) results.push({ callId: call.id, output: JSON.stringify({ skipped: 'an earlier step stopped the turn' }) });
          continue;
        }
        step.state = 'running';
        step.startedAt = now();
        emitStep(step);
        let outcome;
        try {
          if (item.parseError) outcome = result({ error: 'the arguments were not valid JSON' }, true);
          else if (item.name === 'query_data' && !step.query) outcome = result({ error: queryFromArguments(item.args).error }, true);
          else if (!['read_catalog', 'query_data'].includes(item.name) && !session.tools[item.name]) outcome = result({ error: `no tool called ${item.name}` }, true);
          else outcome = await runTool(step, item.name, item.args ?? {});
        } catch (error) {
          outcome = result({ error: error instanceof Error ? error.message : String(error) }, true);
        }
        step.endedAt = now();
        step.state = outcome.isError && !outcome.notAnError ? 'error' : 'done';
        if (step.state === 'error' && !step.detail) step.detail = firstError(outcome.text);
        emitStep(step);
        if (call) results.push({ callId: call.id, output: outcome.text });
      }
      pending = { results };
      if (turn.halted) break;
    }

    // ---- the end of the turn
    const endDocument = await session.document();
    if (!turn.halted && finalText === undefined) {
      turn.failure = { kind: 'limit', rounds, message: `I stopped after ${rounds} steps without finishing.` };
      turn.halted = true;
    }
    let reply = finalText;
    if (turn.failure) {
      reply = composeFailure(turn.failure, startRevision, session.known.revision);
      if (turn.failure.kind === 'rejected') noteStep('Nothing was saved', `the board is still r${session.known.revision}`);
      emit({ type: 'failure', failure: turn.failure });
    } else if (!reply) reply = session.known.revision !== startRevision ? `Done — saved r${session.known.revision}.` : 'Done.';

    // The tools the last answer asked for were run and the model will not read their results: close the turn on the host's side with the words
    // the person is about to read, so the conversation it keeps stays whole.
    if (pending?.results) await closeTurn(endDocument, pending.results, reply);
    const outcome = turn.failure ? ({ permission: 'blocked', rejected: 'rejected', conflict: 'conflict', limit: 'limit', stopped: 'stopped' }[turn.failure.kind] ?? 'error') : turn.emptyResults.length > 0 ? 'empty' : 'done';
    const summary = {
      outcome,
      reply,
      steps: turn.steps.map(snapshot),
      revision: { from: startRevision, to: session.known.revision },
      changed: changedParts(startDocument, endDocument),
      removed: removedParts(startDocument, endDocument),
      document: endDocument,
      failure: turn.failure ?? null,
      emptyResults: turn.emptyResults,
      rounds,
    };
    emit({ type: 'message', text: reply });
    emit({ type: 'turn-end', result: summary });
    return summary;
  }

  // ---- the host's turn route
  const declared = () => toolDefinitions(session).map(tool => ({ name: tool.function.name, description: tool.function.description, schema: tool.function.parameters }));
  const turnRequest = (document, body) =>
    host(endpoints.turn, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ system: buildSystemPrompt({ board: { revision: session.known.revision, document } }), history: messages, tools: declared(), ...body }),
    });

  async function callTurn(document, body) {
    let response;
    try {
      response = await turnRequest(document, body);
    } catch (error) {
      if (signal?.aborted) return { failure: { kind: 'stopped', message: 'Stopped.' } };
      return { failure: { kind: 'model', status: 0, message: `The assistant could not be reached (${error instanceof Error ? error.message : error}).` } };
    }
    if (!response.ok) {
      const status = response.status;
      const kind = (await response.json().catch(() => null))?.error;
      return { failure: { kind: 'model', status, message: MODEL_FAILURES[kind] ?? `The assistant's service answered ${status}. Nothing was changed.` } };
    }
    const answer = await response.json().catch(() => null);
    if (!answer?.history) return { failure: { kind: 'model', status: 200, message: 'The assistant answered with nothing. Nothing was changed.' } };
    return { answer };
  }

  async function closeTurn(document, results, final) {
    signal = deps.signal; // pressing stop ends the work, not the housekeeping: the host's record must still be closed
    const response = await turnRequest(document, { results, final }).catch(() => null);
    const answer = response?.ok ? await response.json().catch(() => null) : null;
    if (answer?.history) messages.splice(0, messages.length, ...answer.history);
  }

  return {
    messages,
    send,
    loadCatalog: () => fetchCatalog(),
    get session() {
      return session;
    },
    get revision() {
      return session.known.revision;
    },
    /** Starts over from the host's board — after a conflict. Keeps the conversation. */
    reload(board) {
      session = openBoardSession({ id: deps.board.id, loaded: { name: board.name, revision: board.revision, document: board.document }, host, boards: endpoints.boards });
      return session;
    },
  };
}

// ---------------------------------------------------------------- words for the screen

// What the host's route says went wrong (the agent's own classification of the failure), in words for the person.
const MODEL_FAILURES = {
  ratelimit: 'The assistant is busy right now. Try again in a moment.',
  timeout: 'The assistant took too long to answer. Nothing was changed.',
  network: 'The assistant could not be reached. Nothing was changed.',
  contextlimit: 'This conversation has grown too long for the assistant. Start a new one. Nothing was changed.',
  guard: "The assistant's safety check stopped this request. Nothing was changed.",
};

function kindFor(name) {
  if (name === 'read_catalog') return 'catalog';
  if (name === 'query_data') return 'query';
  if (MUTATING.has(name)) return 'board';
  return 'read';
}

function labelFor(name, args, rejections) {
  if (name === 'read_catalog') return 'Read the catalog';
  if (name === 'query_data') return `Queried ${typeof args?.entity === 'string' ? args.entity : 'the data'}`;
  if (MUTATING.has(name)) return rejections > 0 ? 'Retried once' : 'Edited the board';
  if (name === 'get_board') return 'Read the board';
  if (name === 'describe') return 'Looked up the board rules';
  if (name === 'validate') return 'Checked the board';
  return name ?? 'Tool call';
}

function firstError(text) {
  const parsed = parseJson(text).value;
  return parsed?.message ?? parsed?.error ?? parsed?.issues?.[0]?.message ?? 'failed';
}

function composeFailure(failure, startRevision, revision) {
  switch (failure.kind) {
    case 'permission':
      return `I can't do that: ${failure.message} Nothing was read or changed. ${failure.alternatives.length ? `You can ask me for: ${failure.alternatives.slice(0, 5).join(', ')}.` : ''}`.trim();
    case 'rejected':
      return `I couldn't make that edit safely: the board check rejected it at ${failure.path} — ${short(failure.issues[0].message, 160)}. Nothing was saved; the board is still r${revision}. ${failure.suggestion}`;
    case 'conflict':
      return `Your change wasn't saved: another session saved r${failure.serverRevision} first, and I worked from r${failure.workedFrom}. Reload r${failure.serverRevision} and I'll apply the edit again on top of it.`;
    case 'save-failed':
      return failure.message;
    case 'stopped':
      return `Stopped. The board is at r${revision}${revision === startRevision ? ' — nothing was saved' : ''}.`;
    case 'limit':
      return `${failure.message} The board is at r${revision}. Ask me to carry on and I'll continue from there.`;
    default:
      return failure.message;
  }
}
