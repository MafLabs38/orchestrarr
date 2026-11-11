import { Link } from 'react-router-dom';

import Alert from 'Components/Alert';
import CapabilityHint from 'Components/CapabilityHint';
import Label from 'Components/Label';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import useSystemStatus from 'System/useSystemStatus';
import type { Approval, ApprovalState } from 'typings/api';
import { describeApprovalState, describeStepKind } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './Queue.module.css';
import { useApprovals } from './useApprovals';

const stateKinds: Record<ApprovalState, Kind> = {
  waiting: kinds.INFO,
  taken: kinds.WARNING,
  // The run ended: whether it passed or failed is in the run (a failed run must not look like a success here)
  dryRunDone: kinds.DEFAULT,
  refused: kinds.DANGER,
  expired: kinds.DEFAULT,
};

function ApprovalRow({ approval }: Readonly<{ approval: Approval }>) {
  return (
    <li className={styles.row}>
      <div className={styles.main}>
        <span className={styles.repository}>{approval.repository}</span>
        <span className={styles.pullRequest}>#{approval.pullRequest}</span>
        <span className={styles.commit}>{translate('ShortCommit', { sha: approval.headSha.slice(0, 7) })}</span>
        <Label kind={stateKinds[approval.state]}>{describeApprovalState(approval.state)}</Label>
        {approval.batchId ? <Link to={`/runs/${approval.batchId}`}>{translate('RunOpen')}</Link> : null}
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

// The decisions the operator approved, and where each is: waiting for the executor, with it, or ended. A dry run starts nothing.
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
            {status && !status.executors.some((executor) => executor.connected) ? (
              <CapabilityHint>{translate('QueueNoExecutor')}</CapabilityHint>
            ) : null}
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
