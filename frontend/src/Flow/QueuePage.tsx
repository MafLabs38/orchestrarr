import { type FormEvent, useState } from 'react';

import useSession, { approvalsCreate, can } from 'Account/useSession';
import Alert from 'Components/Alert';
import Button from 'Components/Button';
import CapabilityHint from 'Components/CapabilityHint';
import formStyles from 'Components/Form/Form.module.css';
import Label from 'Components/Label';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import useNow from 'Helpers/Hooks/useNow';
import { kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import type { DispatchEntry, InProcessEntry } from 'typings/api';
import { describeAge } from 'Utilities/describe';
import translate from 'Utilities/translate';

import { operationLook, operationTitle, placeName } from './operationState';
import styles from './QueuePage.module.css';
import { useDispatch, useHold, useRelease, useSetPriority } from './useDispatch';

// The rank of an operation in the route of its lot, in words (ADR 0055)
function rankName(rank: number): string {
  switch (rank) {
    case 0:
      return translate('RankPipeline');
    case 1:
      return translate('RankRuntime');
    case 2:
      return translate('RankAlert');
    case 3:
      return translate('RankUpdate');
    default:
      return translate('RankOther');
  }
}

function lotOf(repository: string): string {
  return repository.split('/').pop() ?? repository;
}

// The priority of an operation, set by a person: from 0 to 100, 50 is normal (ADR 0050)
function Priority({ problemId, priority, canAct }: Readonly<{ problemId: string; priority: number; canAct: boolean }>) {
  const [value, setValue] = useState(String(priority));
  const set = useSetPriority();
  if (!canAct) {
    return <span>{priority}</span>;
  }
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const number = Number(value);
    if (Number.isInteger(number) && number >= 0 && number <= 100 && number !== priority) {
      set.mutate({ problemId, priority: number });
    }
  };
  return (
    <form className={styles.inline} onSubmit={submit}>
      <input
        className={`${formStyles.input} ${styles.priority}`}
        type="number"
        min={0}
        max={100}
        value={value}
        aria-label={translate('PriorityOf', { problem: problemId })}
        onChange={(event) => setValue(event.target.value)}
      />
      <Button type="submit" disabled={set.isPending || value === String(priority)}>
        {translate('SetPriority')}
      </Button>
    </form>
  );
}

// Hold an operation of the line with a reason, or release it (ADR 0056)
function HoldControl({ entry }: Readonly<{ entry: DispatchEntry }>) {
  const [asking, setAsking] = useState(false);
  const [reason, setReason] = useState('');
  const hold = useHold();
  const release = useRelease();
  if (entry.held) {
    return (
      <Button onClick={() => release.mutate(entry.problemId)} disabled={release.isPending}>
        {translate('Release')}
      </Button>
    );
  }
  if (!asking) {
    return <Button onClick={() => setAsking(true)}>{translate('Hold')}</Button>;
  }
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (reason.trim()) {
      hold.mutate({ problemId: entry.problemId, reason: reason.trim() }, { onSuccess: () => setAsking(false) });
    }
  };
  return (
    <form className={styles.inline} onSubmit={submit}>
      <input
        className={formStyles.input}
        value={reason}
        maxLength={300}
        placeholder={translate('HoldReason')}
        aria-label={translate('HoldReasonOf', { problem: entry.problemId })}
        onChange={(event) => setReason(event.target.value)}
      />
      <Button type="submit" kind={kinds.WARNING} disabled={hold.isPending || !reason.trim()}>
        {translate('Hold')}
      </Button>
      <Button onClick={() => setAsking(false)}>{translate('Cancel')}</Button>
    </form>
  );
}

function LineRow({ entry, now, canAct }: Readonly<{ entry: DispatchEntry; now: number; canAct: boolean }>) {
  const look = operationLook(entry.state);
  let why = entry.detail ?? '';
  if (entry.held) {
    why = translate('HeldBy', { by: entry.held.by, reason: entry.held.reason });
  }
  return (
    <tr>
      <td>{entry.position}</td>
      <td className={styles.operation}>
        {operationTitle(entry.title, entry.kind)}
        {entry.next ? <Label kind={kinds.INFO}>{translate('NextOfItsLot')}</Label> : null}
      </td>
      <td>{lotOf(entry.repository)}</td>
      <td>{rankName(entry.rank)}</td>
      <td>
        <Priority
          key={`${entry.problemId}-${entry.priority}`}
          problemId={entry.problemId}
          priority={entry.priority}
          canAct={canAct}
        />
      </td>
      <td>{describeAge(now - Date.parse(entry.since))}</td>
      <td>
        <Label kind={look.kind}>
          <look.Icon size={iconSizes.SMALL} aria-hidden="true" />
          {look.text}
        </Label>
        {why ? <p className={styles.why}>{why}</p> : null}
      </td>
      <td>{canAct ? <HoldControl entry={entry} /> : null}</td>
    </tr>
  );
}

function InProcessRow({ entry, canAct }: Readonly<{ entry: InProcessEntry; canAct: boolean }>) {
  return (
    <tr>
      <td className={styles.operation}>{operationTitle(entry.title, entry.kind)}</td>
      <td>{lotOf(entry.repository)}</td>
      <td>{placeName(entry.place)}</td>
      <td>
        <Priority
          key={`${entry.problemId}-${entry.priority}`}
          problemId={entry.problemId}
          priority={entry.priority}
          canAct={canAct}
        />
      </td>
    </tr>
  );
}

// The dispatch list of the fab (ADR 0056): the operations that wait, in the order they will go, why each waits, and the two levers of a person, the priority and the hold
export default function QueuePage() {
  const { data, error, isPending } = useDispatch();
  const { data: session } = useSession();
  const now = useNow(5_000);
  const canAct = can(session, approvalsCreate);
  return (
    <PageContent title={translate('Queue')}>
      <PageContentBody>
        <PageHeading title={translate('Queue')} description={translate('QueueDescription')} />
        {isPending ? <CapabilityHint>{translate('Loading')}</CapabilityHint> : null}
        {error ? <Alert kind={kinds.DANGER}>{translate('CannotReadQueue', { error: error.message })}</Alert> : null}
        {data ? (
          <>
            <h2 className={styles.section}>{translate('QueueLine')}</h2>
            {data.line.length === 0 ? (
              <CapabilityHint>{translate('QueueLineEmpty')}</CapabilityHint>
            ) : (
              <div className={styles.wrap}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th>{translate('ColumnPosition')}</th>
                      <th>{translate('ColumnOperation')}</th>
                      <th>{translate('FlowLot')}</th>
                      <th>{translate('ColumnRoute')}</th>
                      <th>{translate('ColumnPriority')}</th>
                      <th>{translate('ColumnWaits')}</th>
                      <th>{translate('ColumnWhy')}</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {data.line.map((entry) => (
                      <LineRow key={entry.problemId} entry={entry} now={now} canAct={canAct} />
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <h2 className={styles.section}>{translate('QueueInProcess')}</h2>
            {data.inProcess.length === 0 ? (
              <CapabilityHint>{translate('QueueInProcessEmpty')}</CapabilityHint>
            ) : (
              <div className={styles.wrap}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th>{translate('ColumnOperation')}</th>
                      <th>{translate('FlowLot')}</th>
                      <th>{translate('ColumnWhere')}</th>
                      <th>{translate('ColumnPriority')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.inProcess.map((entry) => (
                      <InProcessRow key={entry.problemId} entry={entry} canAct={canAct} />
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}
