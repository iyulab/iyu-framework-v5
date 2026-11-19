import test from 'node:test';
import assert from 'node:assert/strict';
import { choosable, fillVariables, pageNote, variableDefaults, variablesMentioned } from '../src/variables.js';

test('a variable starts at its default, else at its first option; one with neither has no value', () => {
  assert.deepEqual(variableDefaults({ status: { options: ['Paid', 'Pending'], default: 'Paid' }, skip: { options: ['0', '50'] }, free: {} }), { status: 'Paid', skip: '0' });
  assert.deepEqual(variableDefaults({ skip: { options: [0, 50] } }), { skip: '0' }, 'numbers are the text they are written as');
  assert.deepEqual(variableDefaults(undefined), {});
});

test('the variables a read mentions are found, each once, and filled with the chosen values', () => {
  const path = "/$data/Orders?$filter=Status eq '{{$status}}' and Total gt {{$min}}&$skip={{$skip}}&$top=50&x={{$status}}";
  assert.deepEqual(variablesMentioned(path), ['status', 'min', 'skip']);
  assert.equal(fillVariables(path, { status: 'Paid', skip: '50' }), "/$data/Orders?$filter=Status eq 'Paid' and Total gt {{$min}}&$skip=50&$top=50&x=Paid");
  assert.equal(fillVariables('no mention', { status: 'Paid' }), 'no mention');
});

test('only a variable that lists options is offered to the person', () => {
  const offered = choosable({ status: { label: 'Status', options: ['Paid', 'Pending'] }, skip: { options: [0, 50, 100] }, free: { default: 'x' }, empty: { options: [] } });
  assert.deepEqual(offered, [
    { name: 'status', label: 'Status', options: ['Paid', 'Pending'] },
    { name: 'skip', label: 'skip', options: ['0', '50', '100'] },
  ]);
});

test('a table page says which rows of how many it shows, from the $skip it was asked with', () => {
  assert.equal(pageNote('/$data/Orders?$top=50&$count=true', 50, 640), 'Showing 1–50 of 640');
  assert.equal(pageNote('/$data/Orders?$top=50&$skip=50&$count=true', 50, 640), 'Showing 51–100 of 640');
  assert.equal(pageNote('/$data/Orders?$skip=600&$top=50&$count=true', 40, 640), 'Showing 601–640 of 640');
  assert.equal(pageNote('/$data/Orders?$skip=700&$top=50&$count=true', 0, 640), 'Nothing on this page of 640');
  assert.equal(pageNote('/$data/Orders', undefined, 640), undefined);
});
