import test from 'node:test';
import assert from 'node:assert/strict';
import { boardReadProblem } from '../src/agent-loop.js';
import { CATALOG } from '../testkit/world.js';

const board = (reads, variables) => ({ data: Object.fromEntries(Object.entries(reads).map(([name, path]) => [name, { from: 'odata', path, list: '/value' }])), ...(variables && { variables }) });
const status = { status: { options: ['Paid', 'Pending'], default: 'Paid' } };

test('a read that mentions a declared variable is judged with the variable at its default', () => {
  const document = board({ rev: "/$data/Orders?$apply=filter(Status eq '{{$status}}')/aggregate(Total with sum as Revenue)" }, status);
  assert.equal(boardReadProblem(document, CATALOG), undefined);
});

test('a mention of a variable the board does not declare is refused by name', () => {
  const document = board({ rev: "/$data/Orders?$apply=filter(Status eq '{{$status}}')/aggregate(Total with sum as Revenue)" });
  const problem = boardReadProblem(document, CATALOG);
  assert.equal(problem.read, 'rev');
  assert.match(problem.message, /\$status/);
  assert.match(problem.message, /declares no variable/);
});

test('a variable with neither a default nor an option cannot stand in a read', () => {
  const document = board({ rev: "/$data/Orders?$apply=filter(Status eq '{{$status}}')/aggregate(Total with sum as Revenue)" }, { status: {} });
  const problem = boardReadProblem(document, CATALOG);
  assert.match(problem.message, /status/);
  assert.match(problem.message, /default/);
});

test('a default that is not one of the options is refused', () => {
  const document = board({ rev: "/$data/Orders?$apply=filter(Status eq '{{$status}}')/aggregate(Total with sum as Revenue)" }, { status: { options: ['Paid'], default: 'Refunded' } });
  assert.match(boardReadProblem(document, CATALOG).message, /Refunded/);
});

test('the field rules still apply to a read with a variable in it', () => {
  const document = board({ m: "/$data/Orders?$apply=filter(Margin gt {{$min}})/aggregate(Total with sum as Revenue)" }, { min: { options: ['0', '10'] } });
  const problem = boardReadProblem(document, CATALOG);
  assert.ok(problem.permission, 'Margin is locked for this person');
});

test('a table page walks the rows with a $skip variable and still has to be one page with the count', () => {
  const skip = { skip: { options: ['0', '50', '100'], default: '0' } };
  const ok = board({ orders: '/$data/Orders?$select=Id,Total&$top=50&$skip={{$skip}}&$count=true' }, skip);
  assert.equal(boardReadProblem(ok, CATALOG), undefined);
  const tooBig = board({ orders: '/$data/Orders?$select=Id,Total&$top=500&$skip={{$skip}}&$count=true' }, skip);
  assert.equal(boardReadProblem(tooBig, CATALOG).read, 'orders');
});
