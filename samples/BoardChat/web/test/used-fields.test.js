import test from 'node:test';
import assert from 'node:assert/strict';
import { usedFields } from '../src/used-fields.js';
import { CATALOG } from '../testkit/world.js';

test('the catalog marks the fields a board reads, through links too, and leaves out aliases and literals', () => {
  const board = {
    data: {
      byRegion: { from: 'odata', path: "/$data/Orders?$apply=filter(Status eq 'Margin')/groupby((Region/Name),aggregate(Total with sum as Revenue))&$orderby=Revenue desc" },
      page: { from: 'odata', path: '/$data/Orders?$select=OrderDate,Total&$top=50&$count=true' },
      other: { from: 'elsewhere', path: '/not/odata' },
    },
  };
  const used = usedFields(board, CATALOG);
  assert.deepEqual([...used].sort(), ['Orders.OrderDate', 'Orders.Region', 'Orders.Status', 'Orders.Total', 'Regions.Name']);
  assert.ok(!used.has('Orders.Margin'), 'a word inside a quoted literal is not a field');
  assert.equal(usedFields(board, null).size, 0);
});
