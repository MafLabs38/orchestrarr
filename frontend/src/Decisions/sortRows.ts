import type { ChecksState, ProblemRow, WorkshopVerdict } from 'typings/api';

export type SortColumn = 'number' | 'application' | 'type' | 'problem' | 'checks' | 'workshop' | 'plan' | 'why';
export interface Sort {
  column: SortColumn;
  descending: boolean;
}

// Each click on a header goes ascending, then descending, then back to the order of the table (its numbers)
export function nextSort(current: Sort | null, column: SortColumn): Sort | null {
  if (current?.column !== column) {
    return { column, descending: false };
  }
  return current.descending ? null : { column, descending: true };
}

const planRank: Record<ProblemRow['goesUpTo'], number> = {
  noAction: 0,
  qualification: 1,
  repair: 2,
  mergeIntoDevelop: 3,
  production: 4,
};
const checksRank: Record<ChecksState, number> = { failing: 0, pending: 1, notRun: 2, unknown: 3, passing: 4 };
const workshopRank: Record<WorkshopVerdict, number> = { fails: 0, unknown: 1, repaired: 2, passes: 3 };

function compareText(a: string, b: string): number {
  return a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
}

// A pure function of the rows and the numbers: the same table is sorted the same way, and a row keeps its number whatever the order
export function sortRows(
  rows: readonly ProblemRow[],
  numbers: ReadonlyMap<string, number>,
  sort: Sort | null,
  textOf: (row: ProblemRow) => { type: string; problem: string; why: string },
): ProblemRow[] {
  const number = (row: ProblemRow) => numbers.get(row.id) ?? 0;
  const byNumber = (a: ProblemRow, b: ProblemRow) => number(a) - number(b);
  if (sort === null) {
    return [...rows].sort(byNumber);
  }
  const compare = (a: ProblemRow, b: ProblemRow): number => {
    switch (sort.column) {
      case 'number':
        return byNumber(a, b);
      case 'application':
        return compareText(a.application, b.application);
      case 'type':
        return compareText(textOf(a).type, textOf(b).type);
      case 'problem':
        return compareText(textOf(a).problem, textOf(b).problem);
      case 'checks':
        return (checksRank[a.checks ?? 'unknown'] ?? 2) - (checksRank[b.checks ?? 'unknown'] ?? 2);
      case 'workshop':
        return workshopRank[a.workshop] - workshopRank[b.workshop];
      case 'plan':
        return planRank[a.goesUpTo] - planRank[b.goesUpTo];
      default:
        return compareText(textOf(a).why, textOf(b).why);
    }
  };
  // The order of the table breaks a tie, so that a sort never shuffles equal rows
  return [...rows].sort((a, b) => (sort.descending ? -compare(a, b) : compare(a, b)) || byNumber(a, b));
}
