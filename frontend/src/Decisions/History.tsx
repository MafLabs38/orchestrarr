import { useState } from 'react';

import Alert from 'Components/Alert';
import Button from 'Components/Button';
import CapabilityHint from 'Components/CapabilityHint';
import { kinds } from 'Helpers/Props/kinds';
import type { HistoryLine, HistoryOutcome, HistoryState } from 'typings/api';
import { describeHistoryOutcome, describeProblemKindShort } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import { shortName } from './AppCell';
import styles from './History.module.css';
import { useHistory, useUnskip } from './useProblems';

const tones: Record<HistoryOutcome, string> = {
  skipped: styles.none,
  inProgress: styles.wait,
  passed: styles.good,
  failed: styles.bad,
  repaired: styles.ai,
  refused: styles.bad,
  expired: styles.none,
  ended: styles.none,
};

// What was decided, newest first, a page at a time: the approved rows with what became of them, and the rows set aside (which can be taken back)
function asState(value: string): HistoryState | null {
  return value === 'approved' || value === 'skipped' ? value : null;
}

export default function History({ canDecide }: Readonly<{ canDecide: boolean }>) {
  const [state, setState] = useState<HistoryState | null>(null);
  const { data, error, isPending, hasNextPage, fetchNextPage, isFetchingNextPage } = useHistory(state);
  const unskip = useUnskip();
  const lines = data?.pages.flatMap((page) => page.lines) ?? [];

  return (
    <>
      <div className={styles.controls}>
        <label className={styles.filter}>
          {translate('HistoryFilter')}
          <select value={state ?? ''} onChange={(event) => setState(asState(event.target.value))}>
            <option value="">{translate('HistoryAll')}</option>
            <option value="approved">{translate('HistoryApproved')}</option>
            <option value="skipped">{translate('HistorySkipped')}</option>
          </select>
        </label>
      </div>
      {isPending ? <CapabilityHint>{translate('Loading')}</CapabilityHint> : null}
      {error ? <Alert kind={kinds.DANGER}>{translate('CannotReadFleet', { error: error.message })}</Alert> : null}
      {data && lines.length === 0 ? <CapabilityHint>{translate('HistoryEmpty')}</CapabilityHint> : null}
      {lines.length > 0 ? (
        <div className={styles.wrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>{translate('ColumnWhen')}</th>
                <th>{translate('ColumnOutcome')}</th>
                <th>{translate('ColumnApplication')}</th>
                <th>{translate('ColumnType')}</th>
                <th>{translate('ColumnProblem')}</th>
                <th>{translate('ColumnBy')}</th>
                <th>{translate('ColumnReason')}</th>
                <th className={styles.narrow} />
              </tr>
            </thead>
            <tbody>
              {lines.map((line) => (
                <HistoryRow
                  key={line.id}
                  line={line}
                  canDecide={canDecide}
                  onUnskip={() => unskip.mutate(line.id)}
                  busy={unskip.isPending}
                />
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
      {hasNextPage ? (
        <Button onClick={() => void fetchNextPage()} disabled={isFetchingNextPage}>
          {translate('HistoryLoadMore')}
        </Button>
      ) : null}
    </>
  );
}

function HistoryRow({
  line,
  canDecide,
  onUnskip,
  busy,
}: Readonly<{ line: HistoryLine; canDecide: boolean; onUnskip: () => void; busy: boolean }>) {
  const what = `${line.application} ${line.description}`;
  return (
    <tr>
      <td>{formatTime(line.at)}</td>
      <td className={tones[line.outcome]}>{describeHistoryOutcome(line.outcome)}</td>
      <td title={line.application}>{shortName(line.application)}</td>
      <td>{describeProblemKindShort(line.kind)}</td>
      <td className={styles.problem} title={line.description}>
        {line.description}
        {line.pullRequest ? <span className={styles.none}> #{line.pullRequest}</span> : null}
      </td>
      <td>{line.by}</td>
      <td>{line.reason}</td>
      <td className={styles.narrow}>
        {line.state === 'skipped' && canDecide ? (
          <Button onClick={onUnskip} disabled={busy} aria-label={translate('HistoryUnskipOf', { problem: what })}>
            {translate('HistoryUnskip')}
          </Button>
        ) : null}
      </td>
    </tr>
  );
}
