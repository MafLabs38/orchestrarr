import Alert from 'Components/Alert';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { sourceName } from 'Components/Page/SourceBadge';
import { kinds } from 'Helpers/Props/kinds';
import { describeError } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './Status.module.css';
import useSystemStatus from './useSystemStatus';

const forgeNames: Record<string, () => string> = {
  github: () => translate('ForgeGithub'),
  sample: () => translate('SourceSample'),
  none: () => translate('ForgeNone'),
};

function forgeName(kind: string): string {
  return (forgeNames[kind] ?? (() => kind))();
}

export default function Status() {
  const { data } = useSystemStatus();

  return (
    <PageContent title={translate('Status')}>
      <PageContentBody>
        <PageHeading scope={translate('System')} title={translate('Status')} />
        {data?.source.error ? (
          <Alert kind={kinds.DANGER}>{translate('CannotReadFleet', { error: describeError(data.source.error) })}</Alert>
        ) : null}
        {data ? (
          <dl className={styles.list}>
            <dt>{translate('Source')}</dt>
            <dd>{sourceName(data.source.kind)}</dd>
            <dt>{translate('LastRead')}</dt>
            <dd>{formatTime(data.source.readAt) ?? translate('Never')}</dd>
            <dt>{translate('Forge')}</dt>
            <dd>
              {data.forge.configured
                ? `${forgeName(data.forge.kind)} · ${translate('ForgeConnected')}`
                : translate('ForgeNotConnected')}
            </dd>
            <dt>{translate('Version')}</dt>
            <dd className={styles.mono}>{data.version}</dd>
          </dl>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}
