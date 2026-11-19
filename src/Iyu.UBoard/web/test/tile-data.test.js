import test from 'node:test';
import assert from 'node:assert/strict';
import { captioned, tileReads } from '../src/tile-data.js';

const kpi = { from: 'odata', path: '/$data/Orders?$apply=aggregate(Total with sum as Revenue)', fields: { Revenue: { at: '/value/0/Revenue', label: 'Revenue', unit: '$' }, AvgOrder: { at: '/value/0/AvgOrder' } } };

test('the field a stat shows is captioned with the title, its place and unit are kept', () => {
  const read = captioned(kpi, 'Revenue', 'Revenue YTD');
  assert.equal(read.fields.Revenue.label, 'Revenue YTD');
  assert.equal(read.fields.Revenue.at, '/value/0/Revenue');
  assert.equal(read.fields.Revenue.unit, '$');
});

test('a field with no label gets the title instead of showing its raw name', () => {
  assert.equal(captioned(kpi, 'AvgOrder', 'Avg. order value').fields.AvgOrder.label, 'Avg. order value');
});

test('the original read, and the other fields, are not touched', () => {
  captioned(kpi, 'Revenue', 'Revenue YTD');
  assert.equal(kpi.fields.Revenue.label, 'Revenue');
  assert.equal(captioned(kpi, 'Revenue', 'Revenue YTD').fields.AvgOrder, kpi.fields.AvgOrder);
});

test('nothing to caption with, or nothing to caption, comes back as it was', () => {
  assert.equal(captioned(kpi, 'Revenue', undefined), kpi);
  assert.equal(captioned(kpi, 'Revenue', ''), kpi);
  assert.equal(captioned(kpi, 'Missing', 'x'), kpi);
  assert.equal(captioned(kpi, 'Revenue', 'Revenue'), kpi);
  assert.equal(captioned(undefined, 'Revenue', 'x'), undefined);
});

test('only a stat is captioned: a chart or a table reads the data as written', () => {
  const document = { data: { kpi } };
  assert.equal(tileReads(document, { as: 'stat', of: 'kpi', show: 'Revenue', title: 'Revenue YTD' }, ['kpi']).kpi.fields.Revenue.label, 'Revenue YTD');
  assert.equal(tileReads(document, { as: 'table', of: 'kpi', title: 'T' }, ['kpi']).kpi, kpi);
  assert.equal(tileReads(document, { as: 'stat', of: 'kpi', show: 'Revenue' }, ['kpi']).kpi, kpi, 'a stat with no title keeps what the board wrote');
  assert.equal(tileReads(document, { as: 'stat', of: 'kpi', show: 'Revenue', title: 'x' }, ['missing']).missing, undefined);
});
