import Alert from 'Components/Alert';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { sourceName } from 'Components/Page/SourceBadge';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import { kinds } from 'Helpers/Props/kinds';
import type { SystemStatus } from 'typings/api';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';
import styles from './Status.module.css';

export default function Status() {
  const { data } = useApiQuery<SystemStatus>('/api/v1/system/status', { refetchInterval: 30_000 });

  return (
    <PageContent title={translate('Status')}>
      <PageContentBody>
        <PageHeading scope={translate('System')} title={translate('Status')} />
        {data?.source.error ? <Alert kind={kinds.DANGER}>{translate('CannotReadFleet', { error: data.source.error })}</Alert> : null}
        {data ? (
          <dl className={styles.list}>
            <dt>{translate('Source')}</dt>
            <dd>{sourceName(data.source.kind)}</dd>
            <dt>{translate('LastRead')}</dt>
            <dd>{formatTime(data.source.readAt) ?? translate('Never')}</dd>
            <dt>{translate('Version')}</dt>
            <dd className={styles.mono}>{data.version}</dd>
          </dl>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}
