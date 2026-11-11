import { describe, expect, it } from 'vitest';

import { problem } from 'testUtils';
import type { ProblemRow } from 'typings/api';

import { nextSort, sortRows } from './sortRows';

const rows: ProblemRow[] = [
  problem({ id: 'a', application: 'example/zeta', goesUpTo: 'production', checks: 'passing', workshop: 'passes' }),
  problem({ id: 'b', application: 'example/alpha', goesUpTo: 'noAction', checks: 'failing', workshop: 'fails' }),
  problem({ id: 'c', application: 'example/alpha', goesUpTo: 'repair', checks: 'pending', workshop: 'unknown' }),
  problem({ id: 'd', application: 'example/mid', goesUpTo: 'qualification', checks: null, workshop: 'repaired' }),
];
const numbers = new Map(rows.map((row, index) => [row.id, index + 1]));
const text = (row: ProblemRow) => ({ type: 'Update', problem: row.description, why: row.id });
const ids = (sorted: ProblemRow[]) => sorted.map((row) => row.id);

describe('nextSort', () => {
  it('goes ascending, then descending, then back to the order of the table, and starts again on another column', () => {
    const first = nextSort(null, 'plan');
    const second = nextSort(first, 'plan');

    expect(first).toEqual({ column: 'plan', descending: false });
    expect(second).toEqual({ column: 'plan', descending: true });
    expect(nextSort(second, 'plan')).toBeNull();
    expect(nextSort(second, 'application')).toEqual({ column: 'application', descending: false });
  });
});

describe('sortRows', () => {
  it('keeps the order of the table (its numbers) when there is no sort, whatever order the rows come in', () => {
    expect(ids(sortRows([...rows].reverse(), numbers, null, text))).toEqual(['a', 'b', 'c', 'd']);
  });

  it('sorts by application and breaks a tie by the number, so that equal lines never shuffle', () => {
    expect(ids(sortRows(rows, numbers, { column: 'application', descending: false }, text))).toEqual([
      'b',
      'c',
      'd',
      'a',
    ]);
    expect(ids(sortRows(rows, numbers, { column: 'application', descending: true }, text))).toEqual([
      'a',
      'd',
      'b',
      'c',
    ]);
  });

  it('sorts by the plan from nothing to production, by the forge from failing to passing, and by the workshop from failing to passing', () => {
    expect(ids(sortRows(rows, numbers, { column: 'plan', descending: false }, text))).toEqual(['b', 'd', 'c', 'a']);
    expect(ids(sortRows(rows, numbers, { column: 'checks', descending: false }, text))).toEqual(['b', 'c', 'd', 'a']);
    expect(ids(sortRows(rows, numbers, { column: 'workshop', descending: false }, text))).toEqual(['b', 'c', 'd', 'a']);
  });

  it('sorts by the number, the type, the problem and the reason with the words the screen shows, numbers in order', () => {
    expect(ids(sortRows([...rows].reverse(), numbers, { column: 'number', descending: false }, text))).toEqual([
      'a',
      'b',
      'c',
      'd',
    ]);
    expect(ids(sortRows(rows, numbers, { column: 'why', descending: true }, text))).toEqual(['d', 'c', 'b', 'a']);
    expect(ids(sortRows(rows, numbers, { column: 'type', descending: false }, text))).toEqual(['a', 'b', 'c', 'd']);
    expect(ids(sortRows(rows, numbers, { column: 'problem', descending: false }, text))).toEqual(['a', 'b', 'c', 'd']);
  });
});
