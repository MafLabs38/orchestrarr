import { Link } from 'react-router-dom';

import Alert from 'Components/Alert';
import CapabilityHint from 'Components/CapabilityHint';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { kinds } from 'Helpers/Props/kinds';
import { describeRunRefusal } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './Runs.module.css';
import StateLabel from './StateLabel';
import { useRuns } from './useRuns';

// What the executor did with each approval, newest first. Each run opens on its timeline.
export default function Runs() {
  const { data: runs, error, isPending } = useRuns();

  return (
    <PageContent title={translate('Runs')}>
      <PageContentBody>
        <PageHeading title={translate('Runs')} description={translate('RunsDescription')} />
        {isPending ? <CapabilityHint>{translate('Loading')}</CapabilityHint> : null}
        {error ? <Alert kind={kinds.DANGER}>{translate('CannotReadRuns', { error: error.message })}</Alert> : null}
        {runs?.length === 0 ? <CapabilityHint>{translate('RunsEmpty')}</CapabilityHint> : null}
        {runs && runs.length > 0 ? (
          <table className={styles.table}>
            <thead>
              <tr>
                <th scope="col">{translate('RunTime')}</th>
                <th scope="col">{translate('RunState')}</th>
                <th scope="col">{translate('RunRepository')}</th>
                <th scope="col">{translate('RunApproval')}</th>
                <th scope="col">{translate('RunTaskCount')}</th>
                <th scope="col">
                  <span className={styles.visuallyHidden}>{translate('RunOpen')}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {runs.map((run) => (
                <tr key={run.batchId}>
                  <td className={styles.mono}>{formatTime(run.createdAt)}</td>
                  <td>
                    <StateLabel state={run.state} />
                    {run.refusal ? <div className={styles.reason}>{describeRunRefusal(run.refusal)}</div> : null}
                  </td>
                  <td>{run.workItemId.replace(/^repository:/, '')}</td>
                  <td>{run.approverSubject}</td>
                  <td>{run.taskCount}</td>
                  <td>
                    <Link to={`/runs/${run.batchId}`}>{translate('RunOpen')}</Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}
