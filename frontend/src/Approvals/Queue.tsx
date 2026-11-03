import Alert from 'Components/Alert';
import CapabilityHint from 'Components/CapabilityHint';
import Label from 'Components/Label';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import * as icons from 'Helpers/Props/icons';
import { kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import useSystemStatus from 'System/useSystemStatus';
import type { Approval } from 'typings/api';
import { describeStepKind } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './Queue.module.css';
import { useApprovals } from './useApprovals';

function ApprovalRow({ approval }: Readonly<{ approval: Approval }>) {
  return (
    <li className={styles.row}>
      <div className={styles.main}>
        <span className={styles.repository}>{approval.repository}</span>
        <span className={styles.pullRequest}>#{approval.pullRequest}</span>
        <span className={styles.commit}>{translate('ShortCommit', { sha: approval.headSha.slice(0, 7) })}</span>
        <Label kind={kinds.INFO}>
          <icons.PENDING size={iconSizes.SMALL} aria-hidden="true" />
          {translate('WaitingForLauncher')}
        </Label>
      </div>
      <div className={styles.meta}>
        <span>{approval.steps.map((step) => describeStepKind(step)).join(' · ')}</span>
        <span>
          {translate('ApprovedBy', { approver: approval.approver, time: formatTime(approval.approvedAt) ?? '' })}
        </span>
      </div>
    </li>
  );
}

// The decisions the operator approved, waiting for a launcher. Nothing runs yet: there is no launcher to take them.
export default function Queue() {
  const { data: approvals, error, isPending } = useApprovals();
  const { data: status } = useSystemStatus();

  return (
    <PageContent title={translate('Queue')}>
      <PageContentBody>
        <PageHeading title={translate('Queue')} description={translate('QueueDescription')} />
        {status?.approvals.killSwitchEngaged ? (
          <Alert kind={kinds.DANGER}>{translate('KillSwitchEngaged')}</Alert>
        ) : null}
        {isPending ? <CapabilityHint>{translate('Loading')}</CapabilityHint> : null}
        {error ? <Alert kind={kinds.DANGER}>{translate('CannotReadQueue', { error: error.message })}</Alert> : null}
        {approvals?.length === 0 ? <CapabilityHint>{translate('QueueEmpty')}</CapabilityHint> : null}
        {approvals && approvals.length > 0 ? (
          <>
            <CapabilityHint>{translate('QueueNoLauncher')}</CapabilityHint>
            <ul className={styles.list}>
              {approvals.map((approval) => (
                <ApprovalRow key={approval.id} approval={approval} />
              ))}
            </ul>
          </>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}
