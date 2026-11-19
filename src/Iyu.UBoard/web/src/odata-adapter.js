// The viewer's one data adapter: a read's `path` is asked of the host's OData as the signed-in person. Used by every tile, and by the check
// of what a saved edit drew (render-check.js), so both read a board's data in the same way.
import { isHttpRef, readHttpRef, requestPath, resolvedReading } from '@iyulab/u-board/picture';
import { DATA_SOURCE_ID } from './prompt.js';

/** `fetcher` is the page's fetch. `onAnswer(path, rows, count, body)` tells the caller what came back, so a tile can say «nothing matches» and how many
 *  rows a page is of, and the check can set what was read beside what was drawn. */
export function makeODataAdapter(onAnswer, fetcher = (url, init) => fetch(url, init)) {
  return {
    id: DATA_SOURCE_ID,
    async resolve(ref) {
      if (!isHttpRef(ref)) return { value: undefined, quality: 'disconnected', reason: 'address' };
      const path = requestPath(ref, Date.now());
      let response;
      try {
        response = await fetcher(path);
      } catch {
        return { value: undefined, quality: 'disconnected', reason: 'transport' };
      }
      if (!response.ok) return { value: undefined, quality: 'disconnected', reason: response.status === 401 || response.status === 403 ? 'auth' : 'source', message: `the host answered ${response.status}` };
      const body = await response.json();
      const reading = readHttpRef(body, ref, Date.now());
      if (!reading.ok) return { value: undefined, quality: 'disconnected', reason: reading.reason, message: reading.message };
      const rows = Array.isArray(body.value) ? body.value.length : undefined;
      onAnswer?.(ref.path, rows, body['@odata.count'], body);
      return { ...resolvedReading(reading), meta: { source: 'the app\'s data service' } };
    },
  };
}
