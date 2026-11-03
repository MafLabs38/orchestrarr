import Alert from 'Components/Alert';
import Button from 'Components/Button';
import CapabilityHint from 'Components/CapabilityHint';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import PageToolbar from 'Components/Page/PageToolbar';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import * as icons from 'Helpers/Props/icons';
import { kinds } from 'Helpers/Props/kinds';
import type { WorkItemReport } from 'typings/api';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import DecisionCard from './DecisionCard';
import styles from './Decisions.module.css';
import useRefreshWorkItems, { workItemsPath } from './useRefreshWorkItems';

export default function Decisions() {
  const { data: report, error, isPending } = useApiQuery<WorkItemReport>(workItemsPath, { refetchInterval: 30_000 });
  const refresh = useRefreshWorkItems();

  return (
    <PageContent title={translate('Decisions')}>
      <PageToolbar>
        <Button onClick={() => refresh.mutate()} disabled={refresh.isPending}>
          <icons.REFRESH size={16} aria-hidden="true" />
          {translate('Refresh')}
        </Button>
      </PageToolbar>
      <PageContentBody>
        <PageHeading title={translate('Decisions')} description={translate('DecisionsDescription')} />
        {isPending ? <CapabilityHint>{translate('Loading')}</CapabilityHint> : null}
        {error ? <Alert kind={kinds.DANGER}>{translate('CannotReadFleet', { error: error.message })}</Alert> : null}
        {report ? <Report report={report} /> : null}
      </PageContentBody>
    </PageContent>
  );
}

function Report({ report }: { report: WorkItemReport }) {
  if (report.error && report.items.length === 0) {
    return <Alert kind={kinds.DANGER}>{translate('CannotReadFleet', { error: report.error })}</Alert>;
  }

  return (
    <>
      {report.stale && report.error ? (
        <Alert kind={kinds.WARNING}>
          {translate('ShowingLastRead', { time: formatTime(report.readAt) ?? translate('Never'), error: report.error })}
        </Alert>
      ) : null}
      {report.items.length === 0 ? (
        <CapabilityHint>
          {report.sourceKind === 'none' ? translate('NoSourceConfigured') : translate('NothingToDecide')}
        </CapabilityHint>
      ) : (
        <div className={styles.list}>
          {report.items.map((item) => (
            <DecisionCard key={item.id} item={item} />
          ))}
        </div>
      )}
    </>
  );
}
