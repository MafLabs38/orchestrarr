import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import useSystemStatus from 'System/useSystemStatus';
import translate from 'Utilities/translate';

import History from './History';

// What was decided: the rows approved with what became of them, and the rows set aside (which can be taken back). A screen of its own, like the queue and the activity.
export default function HistoryPage() {
  const { data: status } = useSystemStatus();

  return (
    <PageContent title={translate('HistoryTitle')}>
      <PageContentBody>
        <PageHeading title={translate('HistoryTitle')} description={translate('HistoryDescription')} />
        <History canDecide={status?.approvals.isOperator ?? false} />
      </PageContentBody>
    </PageContent>
  );
}
