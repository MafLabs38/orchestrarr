import { useState } from 'react';

import Alert from 'Components/Alert';
import Button from 'Components/Button';
import CapabilityHint from 'Components/CapabilityHint';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import PageToolbar from 'Components/Page/PageToolbar';
import * as icons from 'Helpers/Props/icons';
import { kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import type { Severity, WorkItemReport } from 'typings/api';
import { describeError } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import DecisionRow from './DecisionRow';
import styles from './Decisions.module.css';
import useRefreshWorkItems from './useRefreshWorkItems';
import useWorkItems from './useWorkItems';

type Filter = 'all' | Severity;

const groups: { severity: Severity; label: () => string }[] = [
  { severity: 'danger', label: () => translate('SeverityDanger') },
  { severity: 'warning', label: () => translate('SeverityWarning') },
  { severity: 'info', label: () => translate('SeverityInfo') },
];

// With a few applications the cards are open, with many they are lines to open one by one
const openByDefaultUpTo = 3;

export default function Decisions() {
  const { data: report, error, isPending } = useWorkItems();
  const refresh = useRefreshWorkItems();

  return (
    <PageContent title={translate('Decisions')}>
      <PageToolbar>
        <Button onClick={() => refresh.mutate()} disabled={refresh.isPending}>
          <icons.REFRESH size={iconSizes.MEDIUM} aria-hidden="true" />
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

function Report({ report }: Readonly<{ report: WorkItemReport }>) {
  const [filter, setFilter] = useState<Filter>('all');
  const [opened, setOpened] = useState<Record<string, boolean>>({});

  if (report.error && report.items.length === 0) {
    return <Alert kind={kinds.DANGER}>{translate('CannotReadFleet', { error: describeError(report.error) })}</Alert>;
  }

  const defaultOpen = report.items.length <= openByDefaultUpTo;
  const isOpen = (id: string) => opened[id] ?? defaultOpen;
  const shown = report.items.filter((item) => filter === 'all' || item.severity === filter);
  const setAll = (open: boolean) => setOpened(Object.fromEntries(shown.map((item) => [item.id, open])));
  const count = (severity: Severity) => report.items.filter((item) => item.severity === severity).length;

  return (
    <>
      {report.stale && report.error ? (
        <Alert kind={kinds.WARNING}>
          {translate('ShowingLastRead', {
            time: formatTime(report.readAt) ?? translate('Never'),
            error: describeError(report.error),
          })}
        </Alert>
      ) : null}
      {report.items.length === 0 ? (
        <CapabilityHint>
          {report.sourceKind === 'none' ? translate('NoSourceConfigured') : translate('NothingToDecide')}
        </CapabilityHint>
      ) : (
        <>
          <div className={styles.controls}>
            <label className={styles.filter}>
              {translate('FilterSeverity')}
              <select value={filter} onChange={(event) => setFilter(event.target.value as Filter)}>
                <option value="all">{translate('FilterAll')}</option>
                {groups.map(({ severity, label }) => (
                  <option key={severity} value={severity} disabled={count(severity) === 0}>
                    {translate('GroupCount', { label: label(), count: count(severity) })}
                  </option>
                ))}
              </select>
            </label>
            <Button onClick={() => setAll(true)}>{translate('ExpandAll')}</Button>
            <Button onClick={() => setAll(false)}>{translate('CollapseAll')}</Button>
            <span className={styles.count}>
              {translate('ShowingCount', { shown: shown.length, total: report.items.length })}
            </span>
          </div>
          {shown.length === 0 ? <CapabilityHint>{translate('NothingMatches')}</CapabilityHint> : null}
          {groups.map(({ severity, label }) => {
            const inGroup = shown.filter((item) => item.severity === severity);
            return inGroup.length === 0 ? null : (
              <section key={severity} className={styles.group} aria-label={label()}>
                <h2 className={styles.groupTitle}>
                  {translate('GroupCount', { label: label(), count: inGroup.length })}
                </h2>
                <ul className={styles.list}>
                  {inGroup.map((item) => (
                    <DecisionRow
                      key={item.id}
                      item={item}
                      expanded={isOpen(item.id)}
                      onToggle={() => setOpened((current) => ({ ...current, [item.id]: !isOpen(item.id) }))}
                    />
                  ))}
                </ul>
              </section>
            );
          })}
        </>
      )}
    </>
  );
}
