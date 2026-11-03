import CapabilityHint from 'Components/CapabilityHint';
import type { SourceError, WorkItem } from 'typings/api';
import { describeError, isReadyToMerge } from 'Utilities/describe';
import translate from 'Utilities/translate';

import styles from './DecisionCard.module.css';
import PullRequestRow from './PullRequestRow';

// The pull requests behind a "pull requests waiting" finding, or why they are not shown. It says what was not read, never an empty list.
// Read-only: the approval and the merge come with a later vertical.
export default function PullRequestList({
  item,
}: Readonly<{
  item: Pick<WorkItem, 'findings' | 'pullRequests' | 'pullRequestsError'>;
}>) {
  const expected = item.findings.some((finding) => finding.kind === 'openDependencyPullRequests');
  if (!expected) {
    return null;
  }

  if (item.pullRequestsError) {
    return (
      <CapabilityHint>
        {translate('PullRequestsFailed', { error: describeError(item.pullRequestsError satisfies SourceError) })}
      </CapabilityHint>
    );
  }
  if (item.pullRequests === null) {
    return <CapabilityHint>{translate('PullRequestsNotRead')}</CapabilityHint>;
  }
  if (item.pullRequests.length === 0) {
    return null;
  }

  const ready = item.pullRequests.filter(isReadyToMerge).length;
  return (
    <>
      <h3 className={styles.section}>
        {translate('PullRequests')} · {translate('PullRequestsReady', { ready, total: item.pullRequests.length })}
      </h3>
      <ul className={styles.findings}>
        {item.pullRequests.map((pullRequest) => (
          <PullRequestRow key={pullRequest.number} pullRequest={pullRequest} />
        ))}
      </ul>
    </>
  );
}
