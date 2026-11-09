import Alert from 'Components/Alert';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { sourceName } from 'Components/Page/SourceBadge';
import { kinds } from 'Helpers/Props/kinds';
import type { ApprovalStatus, StorageKind } from 'typings/api';
import { describeError } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import ExecutorList from './ExecutorList';
import KillSwitch from './KillSwitch';
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

const storageNames: Record<StorageKind, () => string> = {
  sqlite: () => translate('StorageSqlite'),
  memory: () => translate('StorageMemory'),
};

// A kind this interface does not know is shown as the server names it
function storageName(kind: string): string {
  return Object.hasOwn(storageNames, kind) ? storageNames[kind as StorageKind]() : kind;
}

function approvalsText(approvals: ApprovalStatus): string {
  if (!approvals.enabled) {
    return translate('ApprovalsOff');
  }
  const operator = approvals.operatorName ?? approvals.operator ?? '';
  return approvals.mode === 'openidconnect'
    ? translate('ApprovalsSignedIn', { operator })
    : translate('ApprovalsSandbox', { operator });
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
            <dt>{translate('Approvals')}</dt>
            <dd>{approvalsText(data.approvals)}</dd>
            <dt>{translate('SecondFactor')}</dt>
            <dd>
              {data.approvals.requiresSecondFactor
                ? translate('SecondFactorRequired')
                : translate('SecondFactorNotRequired')}
            </dd>
            <dt>{translate('Executors')}</dt>
            <dd>
              <ExecutorList executors={data.executors} />
            </dd>
            <dt>{translate('Storage')}</dt>
            <dd>{storageName(data.storage.kind)}</dd>
            <dt>{translate('KillSwitch')}</dt>
            <dd>
              <KillSwitch approvals={data.approvals} storageKind={data.storage.kind} />
            </dd>
            <dt>{translate('Version')}</dt>
            <dd className={styles.mono}>{data.version}</dd>
          </dl>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}
