import classNames from 'classnames';

import InfoPopover from 'Components/InfoPopover';
import * as icons from 'Helpers/Props/icons';
import { iconSizes } from 'Helpers/Props/sizes';
import type { ChecksState, ProblemRow, Severity, WorkshopVerdict } from 'typings/api';
import {
  describeChecks,
  describeProblem,
  describeProblemKindShort,
  describeSeverity,
  describeWhy,
  describeWhyShort,
  describeWorkshop,
} from 'Utilities/describe';
import translate from 'Utilities/translate';

import AppCell from './AppCell';
import styles from './ProblemTable.module.css';
import { nextSort, type Sort, type SortColumn } from './sortRows';
import Steps from './Steps';

interface ProblemTableProps {
  rows: ProblemRow[];
  // The number of each row in the whole table (not in what the filters leave): it is how the operator and Claude name a row
  numbers: ReadonlyMap<string, number>;
  selected: ReadonlySet<string>;
  stops: Readonly<Record<string, boolean>>;
  sort: Sort | null;
  onSort: (sort: Sort | null) => void;
  onSelect: (row: ProblemRow, selected: boolean) => void;
  onSelectAll: (selected: boolean) => void;
  onStop: (row: ProblemRow, stop: boolean) => void;
}

export const isActionable = (row: ProblemRow) => row.goesUpTo !== 'noAction' && row.headSha !== null;

const columns: { column: SortColumn; label: Parameters<typeof translate>[0]; className?: string }[] = [
  { column: 'number', label: 'ColumnNumber', className: styles.narrow },
  { column: 'application', label: 'ColumnApplication' },
  { column: 'type', label: 'ColumnType' },
  { column: 'problem', label: 'ColumnProblem' },
  { column: 'checks', label: 'ColumnForge', className: styles.narrow },
  { column: 'workshop', label: 'ColumnWorkshop' },
  { column: 'plan', label: 'ColumnPlan' },
  { column: 'why', label: 'ColumnWhy' },
];

function SortHeader({
  column,
  label,
  className,
  sort,
  onSort,
}: Readonly<{
  column: SortColumn;
  label: Parameters<typeof translate>[0];
  className?: string;
  sort: Sort | null;
  onSort: (sort: Sort | null) => void;
}>) {
  const active = sort?.column === column;
  let ariaSort: 'ascending' | 'descending' | 'none' = 'none';
  if (active) {
    ariaSort = sort.descending ? 'descending' : 'ascending';
  }
  return (
    <th className={className} aria-sort={ariaSort}>
      <button
        type="button"
        className={styles.sort}
        aria-label={translate('SortBy', { column: translate(label) })}
        onClick={() => onSort(nextSort(sort, column))}
      >
        {translate(label)}
        {active ? <span aria-hidden="true">{sort.descending ? '▼' : '▲'}</span> : null}
      </button>
    </th>
  );
}

// One row per problem, one line per row, with a number, a column for each thing worth sorting by, and the plan step by step. What is long is in a bubble.
export default function ProblemTable({
  rows,
  numbers,
  selected,
  stops,
  sort,
  onSort,
  onSelect,
  onSelectAll,
  onStop,
}: Readonly<ProblemTableProps>) {
  const actionable = rows.filter(isActionable);
  const allSelected = actionable.length > 0 && actionable.every((row) => selected.has(row.id));

  return (
    <div className={styles.wrap}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th className={styles.narrow}>
              <input
                type="checkbox"
                aria-label={translate('SelectAllShown')}
                checked={allSelected}
                disabled={actionable.length === 0}
                onChange={(event) => onSelectAll(event.target.checked)}
              />
            </th>
            {columns.map((column) => (
              <SortHeader key={column.column} {...column} sort={sort} onSort={onSort} />
            ))}
            <th className={styles.narrow} title={translate('StopBeforeProductionHint')}>
              {translate('ColumnProd')}
            </th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <ProblemTableRow
              key={row.id}
              row={row}
              number={numbers.get(row.id) ?? 0}
              isSelected={selected.has(row.id)}
              stops={stops[row.id] ?? false}
              onSelect={onSelect}
              onStop={onStop}
            />
          ))}
        </tbody>
      </table>
      <p className={styles.legend}>{translate('PlanLegend')}</p>
    </div>
  );
}

function rowClass(isSelected: boolean, actionable: boolean): string | undefined {
  if (isSelected) {
    return styles.selected;
  }
  return actionable ? undefined : styles.off;
}

const checkIcons: Record<ChecksState, { Icon: typeof icons.SUCCESS; tone: string }> = {
  passing: { Icon: icons.SUCCESS, tone: styles.good },
  failing: { Icon: icons.DANGER, tone: styles.bad },
  pending: { Icon: icons.PENDING, tone: styles.wait },
  unknown: { Icon: icons.UNKNOWN, tone: styles.none },
};

function Checks({ state }: Readonly<{ state: ChecksState | null }>) {
  if (state === null) {
    return <span className={styles.none}>—</span>;
  }
  const { Icon, tone } = checkIcons[state];
  return (
    <span className={tone} title={describeChecks(state)}>
      <Icon size={iconSizes.SMALL} aria-label={describeChecks(state)} />
    </span>
  );
}

const verdictTones: Record<WorkshopVerdict, string> = {
  unknown: styles.none,
  passes: styles.good,
  fails: styles.bad,
  repaired: styles.ai,
};

function Workshop({ verdict }: Readonly<{ verdict: WorkshopVerdict }>) {
  return (
    <span className={verdictTones[verdict]}>
      {verdict === 'repaired' ? '✦ ' : ''}
      {describeWorkshop(verdict)}
    </span>
  );
}

const severityTones: Record<Severity, string> = { danger: styles.bad, warning: styles.wait, info: styles.info };

function ProblemTableRow({
  row,
  number,
  isSelected,
  stops,
  onSelect,
  onStop,
}: Readonly<{
  row: ProblemRow;
  number: number;
  isSelected: boolean;
  stops: boolean;
  onSelect: (row: ProblemRow, selected: boolean) => void;
  onStop: (row: ProblemRow, stop: boolean) => void;
}>) {
  const sentence = describeProblem(row);
  const actionable = isActionable(row);
  return (
    <tr className={rowClass(isSelected, actionable)}>
      <td className={styles.narrow}>
        <input
          type="checkbox"
          aria-label={translate('SelectRow', { problem: `${row.application} ${sentence}` })}
          checked={isSelected}
          disabled={!actionable}
          onChange={(event) => onSelect(row, event.target.checked)}
        />
      </td>
      <td className={classNames(styles.narrow, styles.number)}>{number}</td>
      <td>
        <AppCell application={row.application} />
      </td>
      <td>{describeProblemKindShort(row.kind)}</td>
      <td className={styles.problem}>
        {row.severity ? (
          <span
            className={classNames(styles.dot, severityTones[row.severity])}
            role="img"
            title={describeSeverity(row.severity)}
            aria-label={describeSeverity(row.severity)}
          />
        ) : null}
        {row.url ? (
          <a
            href={row.url}
            target="_blank"
            rel="noopener noreferrer"
            title={sentence}
            aria-label={translate('ProblemLink', { problem: sentence })}
          >
            {sentence}
          </a>
        ) : (
          <span title={sentence}>{sentence}</span>
        )}
        {row.updateClass && row.updateClass !== 'unknown' ? (
          <span className={styles.class}>{row.updateClass}</span>
        ) : null}
      </td>
      <td className={styles.narrow}>
        <Checks state={row.checks} />
      </td>
      <td>
        <Workshop verdict={row.workshop} />
      </td>
      <td>
        <Steps row={row} stops={stops} />
      </td>
      <td>
        {describeWhyShort(row.why)}
        {row.why === 'none' ? null : (
          <InfoPopover label={translate('WhyMore', { problem: `${row.application} ${sentence}` })}>
            {describeWhy(row.why)}
          </InfoPopover>
        )}
      </td>
      <td className={styles.narrow}>
        {row.canStopBeforeProduction ? (
          <input
            type="checkbox"
            title={translate('StopBeforeProductionHint')}
            aria-label={translate('StopBeforeProductionOf', { problem: `${row.application} ${sentence}` })}
            checked={stops}
            onChange={(event) => onStop(row, event.target.checked)}
          />
        ) : (
          <span className={styles.none}>—</span>
        )}
      </td>
    </tr>
  );
}
