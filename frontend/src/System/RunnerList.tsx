import CapabilityHint from 'Components/CapabilityHint';
import Label from 'Components/Label';
import { kinds } from 'Helpers/Props/kinds';
import type { RunnerStatus } from 'typings/api';
import { describeTaskKind } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './Status.module.css';

// The runners that asked for work: whether they did lately, what they accept to do and the standing delegations they hold.
export default function RunnerList({ runners }: Readonly<{ runners: RunnerStatus[] }>) {
  if (runners.length === 0) {
    return <CapabilityHint>{translate('RunnerNone')}</CapabilityHint>;
  }
  return (
    <ul className={styles.runners}>
      {runners.map((runner) => (
        <li key={runner.name}>
          <strong>{runner.name}</strong>{' '}
          <Label kind={runner.connected ? kinds.SUCCESS : kinds.WARNING}>
            {runner.connected ? translate('RunnerConnected') : translate('RunnerGone')}
          </Label>{' '}
          {runner.secondFactorRequired ? null : <Label kind={kinds.WARNING}>{translate('RunnerNoCodeCheck')}</Label>}{' '}
          <span className={styles.meta}>
            v{runner.version} · {translate('RunnerSeen', { time: formatTime(runner.lastSeen) ?? '' })}
          </span>
          <div className={styles.meta}>
            {translate('RunnerKinds', {
              kinds:
                runner.kinds.length > 0
                  ? runner.kinds.map((kind) => describeTaskKind(kind)).join(', ')
                  : translate('RunnerNoRights'),
            })}
          </div>
          <div className={styles.meta}>
            {translate('RunnerRepositories', {
              repositories:
                runner.repositories.length > 0 ? runner.repositories.join(', ') : translate('RunnerNoRights'),
            })}
          </div>
          <div className={styles.meta}>
            {translate('RunnerDelegations')}:{' '}
            {runner.delegations.length > 0
              ? runner.delegations
                  .map((delegation) =>
                    translate('RunnerDelegation', {
                      kind: describeTaskKind(delegation.kind),
                      repository: delegation.repository,
                      date: delegation.expiresAt.slice(0, 10),
                      quota: delegation.dailyQuota,
                    }),
                  )
                  .join('; ')
              : translate('RunnerNoDelegations')}
          </div>
        </li>
      ))}
    </ul>
  );
}
