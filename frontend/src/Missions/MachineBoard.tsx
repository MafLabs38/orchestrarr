import Alert from 'Components/Alert';
import Button from 'Components/Button';
import Label from 'Components/Label';
import useNow from 'Helpers/Hooks/useNow';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import type { MachineStateName, MachineView } from 'typings/api';
import { formatDuration } from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './MachineBoard.module.css';
import { useMachines, useRequalify } from './useMachines';

// The state of a machine as a coloured word (the word says it too)
function stateOf(state: MachineStateName): { text: string; kind: Kind } {
  switch (state) {
    case 'Idle':
      return { text: translate('MachineStateIdle'), kind: kinds.SUCCESS };
    case 'InProcess':
      return { text: translate('MachineStateInProcess'), kind: kinds.INFO };
    case 'Finished':
      return { text: translate('MachineStateFinished'), kind: kinds.WARNING };
    case 'Qualifying':
      return { text: translate('MachineStateQualifying'), kind: kinds.WARNING };
    default:
      return { text: translate('MachineStateDown'), kind: kinds.DANGER };
  }
}

function roleOf(role: string): string {
  return role === 'Release' ? translate('MachineRoleRelease') : translate('MachineRoleStaging');
}

function Machine({ machine }: Readonly<{ machine: MachineView }>) {
  const requalify = useRequalify();
  const state = stateOf(machine.state);
  const now = useNow(15_000);
  const sinceSeconds = (now - Date.parse(machine.since)) / 1000;
  const { stats } = machine;
  const canQualifyAgain = machine.state === 'Down' || machine.state === 'Finished';
  return (
    <li className={styles.machine} aria-label={machine.id}>
      <div className={styles.head}>
        <span className={styles.name}>{machine.id}</span>
        <Label kind={kinds.DEFAULT}>{roleOf(machine.role)}</Label>
        <Label kind={state.kind}>{state.text}</Label>
        <span className={styles.meta}>{translate('MachineSince', { duration: formatDuration(sinceSeconds) })}</span>
      </div>
      {machine.missionId ? (
        <p className={styles.meta}>{translate('MachineHeldBy', { mission: machine.missionId })}</p>
      ) : null}
      {machine.state === 'Down' && machine.reason ? (
        <p className={styles.reason}>{translate('MachineDownBecause', { reason: machine.reason })}</p>
      ) : null}
      {machine.checks.length > 0 ? (
        <ul
          className={styles.checks}
          aria-label={translate('MachineQualifiedAt', {
            time: machine.qualifiedAt ? new Date(machine.qualifiedAt).toLocaleTimeString() : '',
          })}
        >
          {machine.checks.map((check) => (
            <li key={check.name} className={check.passed ? undefined : styles.failed}>
              {translate(check.passed ? 'MachineCheckPassed' : 'MachineCheckFailed', {
                name: check.name,
                detail: check.detail,
              })}
            </li>
          ))}
        </ul>
      ) : (
        <p className={styles.meta}>{translate('MachineNeverQualified')}</p>
      )}
      <p className={styles.stats}>
        {translate('MachineStats', {
          duration: formatDuration((Date.parse(stats.to) - Date.parse(stats.from)) / 1000),
          availability: Math.round(stats.availability * 100),
          utilisation: Math.round(stats.utilisation * 100),
          lots: stats.lots,
          downs: stats.downEvents,
        })}
        {stats.meanTimeToRepairSeconds > 0
          ? `, ${translate('MachineStatsRepair', { duration: formatDuration(stats.meanTimeToRepairSeconds) })}`
          : ''}
      </p>
      {canQualifyAgain ? (
        <Button onClick={() => requalify.mutate(machine.id)} disabled={requalify.isPending}>
          {translate('MachineRequalify')}
        </Button>
      ) : null}
      {requalify.isError ? (
        <Alert kind={kinds.DANGER}>{translate('MachineRequalifyFailed', { error: requalify.error.message })}</Alert>
      ) : null}
    </li>
  );
}

// The lab machines, live: where each one is (idle, in process, finished, being qualified, down and why), so that a bottleneck or a machine that is out of order is seen at once
export default function MachineBoard() {
  const { data, error } = useMachines();
  if (error) {
    return <Alert kind={kinds.DANGER}>{translate('CannotReadMachines', { error: error.message })}</Alert>;
  }
  if (!data || data.machines.length === 0) {
    return null;
  }
  return (
    <section aria-label={translate('Machines')}>
      <ul className={styles.board}>
        {data.machines.map((machine) => (
          <Machine key={machine.id} machine={machine} />
        ))}
      </ul>
    </section>
  );
}
