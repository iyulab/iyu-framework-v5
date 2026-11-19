// Which fields of the catalog a board reads — what the left pane marks with a dot.
import { fieldPaths, parseODataUrl } from './odata.js';

/** `Set` of `Entity.Field`; a path through a link marks the link on its entity and the field on the entity it leads to. */
export function usedFields(document, catalog) {
  const used = new Set();
  if (!catalog) return used;
  const entities = new Map(catalog.entities.map(entity => [entity.name, entity]));
  for (const read of Object.values(document?.data ?? {})) {
    const query = parseODataUrl(read?.path ?? '');
    const entity = query && entities.get(query.entity);
    if (!entity) continue;
    const texts = [query.apply, query.filter, query.orderby].filter(Boolean).flatMap(fieldPaths).concat(query.select ?? []);
    for (const path of texts) {
      let current = entity;
      for (const segment of path.split('/')) {
        if (!current) break;
        if (current.properties.some(property => property.name === segment)) used.add(`${current.name}.${segment}`);
        const link = current.links?.find(candidate => candidate.name === segment);
        if (link) {
          used.add(`${current.name}.${segment}`);
          current = entities.get(link.target);
        } else break;
      }
    }
  }
  return used;
}
