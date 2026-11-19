// What a tile hands its viewer. A `stat` draws a caption above its value — the label of the field it shows, or that field's name
// when it has none — and the tile's header used to write the part's title as well: the same words twice, or a title and a raw field
// name. A stat's tile has no header row; the viewer's caption is the one caption, and it is the part's title. The board document
// itself is not changed.

/** A copy of a read in which the field `field` carries `caption` as its label (the read, and the board it came from, are left as they were). */
export function captioned(read, field, caption) {
  if (!read || typeof read !== 'object' || !field || !read.fields?.[field] || typeof caption !== 'string' || caption === '') return read;
  if (read.fields[field].label === caption) return read;
  return { ...read, fields: { ...read.fields, [field]: { ...read.fields[field], label: caption } } };
}

/** The data reads one part draws from, as its tile hands them over: a stat's field is captioned with the part's title, everything else is as written. */
export function tileReads(document, part, names) {
  return Object.fromEntries(
    names.filter(name => document.data?.[name]).map(name => [name, part.as === 'stat' ? captioned(document.data[name], part.show, part.title) : document.data[name]]),
  );
}
