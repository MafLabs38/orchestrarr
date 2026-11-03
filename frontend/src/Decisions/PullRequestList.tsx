import CapabilityHint from 'Components/CapabilityHint';
import type { WorkItem } from 'typings/api';
import { describeError } from 'Utilities/describe';
import translate from 'Utilities/translate';

import styles from './PullRequestList.module.css';
import PullRequestRow from './PullRequestRow';

// What to say next to the list: the forge answered less than Portarr counts, or nothing, or it did not answer. Never a silent empty list.
function notice(item: Pick<WorkItem, 'pullRequests'>, expected: number): string | null {
  const read = item.pullRequests;
  switch (read.status) {
    case 'notConnected':
      return translate('PullRequestsNotConnected');
    case 'pending':
      return translate('PullRequestsPending');
    case 'failed':
      return read.error ? translate('PullRequestsFailed', { error: describeError(read.error) }) : null;
    case 'read':
      if (read.items.length === 0) {
        return translate('PullRequestsNoneFound', { count: expected });
      }
      if (read.truncated) {
        return translate('PullRequestsTruncated', { shown: read.items.length });
      }
      return read.items.length === expected
        ? null
        : translate('PullRequestsCountMismatch', { count: expected, found: read.items.length });
    case 'notNeeded':
      return null;
  }
}

// The pull requests behind a "pull requests waiting" finding. Read-only: the approval and the merge come with a later vertical.
export default function PullRequestList({ item }: Readonly<{ item: Pick<WorkItem, 'findings' | 'pullRequests'> }>) {
  const finding = item.findings.find((f) => f.kind === 'openDependencyPullRequests');
  if (!finding || item.pullRequests.status === 'notNeeded') {
    return null;
  }

  const { items, stale } = item.pullRequests;
  const note = notice(item, finding.count);
  const ready = items.filter((pullRequest) => pullRequest.readyToMerge).length;
  return (
    <>
      {items.length > 0 ? (
        <h4 className={styles.section}>
          {translate('PullRequests')} · {translate('PullRequestsReady', { ready, total: items.length })}
        </h4>
      ) : null}
      {note ? (
        <div className={styles.notice}>
          <CapabilityHint>{note}</CapabilityHint>
        </div>
      ) : null}
      {stale && items.length > 0 ? <CapabilityHint>{translate('PullRequestsStale')}</CapabilityHint> : null}
      {items.length > 0 ? (
        <ul className={styles.list}>
          {items.map((pullRequest) => (
            <PullRequestRow key={pullRequest.number} pullRequest={pullRequest} />
          ))}
        </ul>
      ) : null}
    </>
  );
}
