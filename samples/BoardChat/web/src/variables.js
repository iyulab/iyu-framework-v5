// Board variables: a value the page holds that the board's reads mention as `{{$name}}`. A board-wide filter is one variable
// every read's `$filter` mentions; paging a table on the server is a variable `skip` in its `$skip`. The board holds the
// variable's name, its options and its default; the page holds the chosen value and the viewer reads again when it changes.

const MENTION = /\{\{\$([A-Za-z_][A-Za-z0-9_]*)\}\}/g;

/** The value each declared variable has until the person chooses another: its `default`, else its first option. */
export function variableDefaults(variables) {
  const values = {};
  for (const [name, spec] of Object.entries(variables ?? {})) {
    const value = spec?.default ?? spec?.options?.[0];
    if (value !== undefined) values[name] = String(value);
  }
  return values;
}

/** The variables a text mentions, in order, each once. */
export function variablesMentioned(text) {
  return [...new Set([...String(text ?? '').matchAll(MENTION)].map(match => match[1]))];
}

/** The text with each mentioned variable replaced by its value; a mention with no value is left as written. */
export function fillVariables(text, values) {
  return String(text ?? '').replace(MENTION, (whole, name) => (values?.[name] === undefined ? whole : String(values[name])));
}

/** The variables a person may choose between: those that list their options. */
export function choosable(variables) {
  return Object.entries(variables ?? {})
    .filter(([, spec]) => Array.isArray(spec?.options) && spec.options.length > 0)
    .map(([name, spec]) => ({ name, label: spec.label ?? name, options: spec.options.map(String) }));
}

/** «Showing 51–100 of 640» for a table read with `$top`, `$skip` and `$count`: `path` is the read as asked (variables filled). */
export function pageNote(path, rows, count) {
  if (rows === undefined || count === undefined) return undefined;
  const skip = Number(/[?&]\$skip=(\d+)/.exec(path ?? '')?.[1] ?? 0);
  if (rows === 0) return `Nothing on this page of ${Number(count).toLocaleString('en-US')}`;
  return `Showing ${skip + 1}–${skip + rows} of ${Number(count).toLocaleString('en-US')}`;
}
