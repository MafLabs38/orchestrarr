import { auditPath } from 'Approvals/useApprovals';
import Alert from 'Components/Alert';
import CapabilityHint from 'Components/CapabilityHint';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import { kinds } from 'Helpers/Props/kinds';
import type { ActivityEntry } from 'typings/api';
import { describeEvent } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './Activity.module.css';

// The audit trail: who decided what, what was refused and why, and every stop and release of the global stop. Newest first.
export default function Activity() {
  const {
    data: entries,
    error,
    isPending,
  } = useApiQuery<ActivityEntry[]>(auditPath, { refetchInterval: refreshIntervalMs });

  return (
    <PageContent title={translate('Activity')}>
      <PageContentBody>
        <PageHeading
          scope={translate('System')}
          title={translate('Activity')}
          description={translate('ActivityDescription')}
        />
        {isPending ? <CapabilityHint>{translate('Loading')}</CapabilityHint> : null}
        {error ? <Alert kind={kinds.DANGER}>{translate('CannotReadActivity', { error: error.message })}</Alert> : null}
        {entries?.length === 0 ? <CapabilityHint>{translate('ActivityEmpty')}</CapabilityHint> : null}
        {entries && entries.length > 0 ? (
          <table className={styles.table}>
            <thead>
              <tr>
                <th scope="col">{translate('ActivityTime')}</th>
                <th scope="col">{translate('ActivityEvent')}</th>
                <th scope="col">{translate('ActivityWho')}</th>
                <th scope="col">{translate('ActivityDetail')}</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((entry) => (
                <tr key={entry.id}>
                  <td className={styles.mono}>{formatTime(entry.at)}</td>
                  <td>{describeEvent(entry.event)}</td>
                  <td>{entry.subject}</td>
                  <td className={styles.mono}>{entry.detail}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}
