import Label from 'Components/Label';
import StatusLabel from 'Components/StatusLabel';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import type { Ceiling, ProblemRow } from 'typings/api';
import { describeCeiling, describeChecks, describeProblem, describeProblemKind, describeWhy } from 'Utilities/describe';
import translate from 'Utilities/translate';

import styles from './ProblemTable.module.css';

const ceilingKinds: Record<Ceiling, Kind> = {
  noAction: kinds.DISABLED,
  qualification: kinds.INFO,
  mergeIntoDevelop: kinds.PRIMARY,
  production: kinds.SUCCESS,
};

interface ProblemTableProps {
  rows: ProblemRow[];
  selected: ReadonlySet<string>;
  stops: Readonly<Record<string, boolean>>;
  repairs: Readonly<Record<string, boolean>>;
  onSelect: (row: ProblemRow, selected: boolean) => void;
  onSelectAll: (selected: boolean) => void;
  onStop: (row: ProblemRow, stop: boolean) => void;
  onRepair: (row: ProblemRow, repair: boolean) => void;
}

export const isActionable = (row: ProblemRow) => row.goesUpTo !== 'noAction' && row.headSha !== null;

// One row per problem: what it is, how far it goes by itself and why it stops there. Nothing unfolds.
export default function ProblemTable({
  rows,
  selected,
  stops,
  repairs,
  onSelect,
  onSelectAll,
  onStop,
  onRepair,
}: Readonly<ProblemTableProps>) {
  const actionable = rows.filter(isActionable);
  const allSelected = actionable.length > 0 && actionable.every((row) => selected.has(row.id));

  return (
    <div className={styles.wrap}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>
              <input
                type="checkbox"
                aria-label={translate('SelectAllShown')}
                checked={allSelected}
                disabled={actionable.length === 0}
                onChange={(event) => onSelectAll(event.target.checked)}
              />
            </th>
            <th>{translate('ColumnApplication')}</th>
            <th>{translate('ColumnType')}</th>
            <th>{translate('ColumnProblem')}</th>
            <th>{translate('ColumnSeverity')}</th>
            <th>{translate('ColumnChecks')}</th>
            <th>{translate('ColumnGoesUp')}</th>
            <th>{translate('ColumnOptions')}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <ProblemTableRow
              key={row.id}
              row={row}
              isSelected={selected.has(row.id)}
              stops={stops[row.id] ?? false}
              repairs={repairs[row.id] ?? false}
              onSelect={onSelect}
              onStop={onStop}
              onRepair={onRepair}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function rowClass(isSelected: boolean, actionable: boolean): string | undefined {
  if (isSelected) {
    return styles.selected;
  }
  return actionable ? undefined : styles.off;
}

function ProblemTableRow({
  row,
  isSelected,
  stops,
  repairs,
  onSelect,
  onStop,
  onRepair,
}: Readonly<{
  row: ProblemRow;
  isSelected: boolean;
  stops: boolean;
  repairs: boolean;
  onSelect: (row: ProblemRow, selected: boolean) => void;
  onStop: (row: ProblemRow, stop: boolean) => void;
  onRepair: (row: ProblemRow, repair: boolean) => void;
}>) {
  const sentence = describeProblem(row);
  const actionable = isActionable(row);
  return (
    <tr className={rowClass(isSelected, actionable)}>
      <td>
        <input
          type="checkbox"
          aria-label={translate('SelectRow', { problem: `${row.application} ${sentence}` })}
          checked={isSelected}
          disabled={!actionable}
          onChange={(event) => onSelect(row, event.target.checked)}
        />
      </td>
      <td>{row.application}</td>
      <td>
        <Label>{describeProblemKind(row.kind)}</Label>
      </td>
      <td>
        {row.url ? (
          <a
            href={row.url}
            target="_blank"
            rel="noopener noreferrer"
            aria-label={translate('ProblemLink', { problem: sentence })}
          >
            {sentence}
          </a>
        ) : (
          sentence
        )}
        {row.updateClass && row.updateClass !== 'unknown' ? <Label>{row.updateClass}</Label> : null}
      </td>
      <td>{row.severity ? <StatusLabel severity={row.severity} /> : <span className={styles.none}>—</span>}</td>
      <td>{row.checks ? describeChecks(row.checks) : <span className={styles.none}>—</span>}</td>
      <td>
        <Label kind={ceilingKinds[row.goesUpTo]}>{describeCeiling(row.goesUpTo)}</Label>
        {row.why === 'none' ? null : <small className={styles.why}>{describeWhy(row.why)}</small>}
      </td>
      <td>
        {row.canStopBeforeProduction ? (
          <label className={styles.stop}>
            <input
              type="checkbox"
              aria-label={translate('StopBeforeProductionOf', { problem: `${row.application} ${sentence}` })}
              checked={stops}
              onChange={(event) => onStop(row, event.target.checked)}
            />
            {translate('ColumnStop')}
          </label>
        ) : null}
        {row.canRepair ? (
          <label className={styles.stop}>
            <input
              type="checkbox"
              aria-label={translate('RepairOf', { problem: `${row.application} ${sentence}` })}
              checked={repairs}
              onChange={(event) => onRepair(row, event.target.checked)}
            />
            {translate('ColumnRepair')}
          </label>
        ) : null}
        {row.canStopBeforeProduction || row.canRepair ? null : <span className={styles.none}>—</span>}
      </td>
    </tr>
  );
}
