import CapabilityHint from 'Components/CapabilityHint';
import Label from 'Components/Label';
import { kinds } from 'Helpers/Props/kinds';
import type { ExecutorStatus } from 'typings/api';
import { describeTaskKind } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './Status.module.css';

// The executors that asked for work: whether they did lately, what they accept to do and the standing delegations they hold.
export default function ExecutorList({ executors }: Readonly<{ executors: ExecutorStatus[] }>) {
  if (executors.length === 0) {
    return <CapabilityHint>{translate('ExecutorNone')}</CapabilityHint>;
  }
  return (
    <ul className={styles.executors}>
      {executors.map((executor) => (
        <li key={executor.name}>
          <strong>{executor.name}</strong>{' '}
          <Label kind={executor.connected ? kinds.SUCCESS : kinds.WARNING}>
            {executor.connected ? translate('ExecutorConnected') : translate('ExecutorGone')}
          </Label>{' '}
          <span className={styles.meta}>
            v{executor.version} · {translate('ExecutorSeen', { time: formatTime(executor.lastSeen) ?? '' })}
          </span>
          <div className={styles.meta}>
            {translate('ExecutorKinds', {
              kinds:
                executor.kinds.length > 0
                  ? executor.kinds.map((kind) => describeTaskKind(kind)).join(', ')
                  : translate('ExecutorNoRights'),
            })}
          </div>
          <div className={styles.meta}>
            {translate('ExecutorRepositories', {
              repositories:
                executor.repositories.length > 0 ? executor.repositories.join(', ') : translate('ExecutorNoRights'),
            })}
          </div>
          <div className={styles.meta}>
            {translate('ExecutorDelegations')}:{' '}
            {executor.delegations.length > 0
              ? executor.delegations
                  .map((delegation) =>
                    translate('ExecutorDelegation', {
                      kind: describeTaskKind(delegation.kind),
                      repository: delegation.repository,
                      date: delegation.expiresAt.slice(0, 10),
                      quota: delegation.dailyQuota,
                    }),
                  )
                  .join('; ')
              : translate('ExecutorNoDelegations')}
          </div>
        </li>
      ))}
    </ul>
  );
}
