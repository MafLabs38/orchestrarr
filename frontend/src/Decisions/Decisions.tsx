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
import useSystemStatus from 'System/useSystemStatus';
import type { Ceiling, ProblemRow, ProblemsReport, ProblemTab, WorkshopVerdict } from 'typings/api';
import {
  describeCeiling,
  describeError,
  describeProblem,
  describeProblemKind,
  describeProblemKindShort,
  describeWhyShort,
  describeWorkshop,
} from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './Decisions.module.css';
import History from './History';
import ProblemTable, { isActionable } from './ProblemTable';
import SelectionBar from './SelectionBar';
import { type Sort, sortRows } from './sortRows';
import useProblems, { useRefreshProblems, useSkipProblems } from './useProblems';

const ceilingOrder: Ceiling[] = ['production', 'mergeIntoDevelop', 'repair', 'qualification', 'noAction'];
const workshopOrder: WorkshopVerdict[] = ['fails', 'passes', 'repaired', 'unknown'];
const tabs: {
  id: ProblemTab | 'history';
  label: Parameters<typeof translate>[0];
  hint: Parameters<typeof translate>[0];
}[] = [
  { id: 'act', label: 'TabAct', hint: 'TabActHint' },
  { id: 'configure', label: 'TabConfigure', hint: 'TabConfigureHint' },
  { id: 'know', label: 'TabKnow', hint: 'TabKnowHint' },
  { id: 'history', label: 'TabHistory', hint: 'TabHistoryHint' },
];

export default function Decisions() {
  const { data: report, error, isPending } = useProblems();
  const refresh = useRefreshProblems();

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
        {report ? <Table report={report} /> : null}
      </PageContentBody>
    </PageContent>
  );
}

interface Filters {
  application: string;
  type: string;
  goesUp: string;
  workshop: string;
  text: string;
}

const noFilters: Filters = { application: '', type: '', goesUp: '', workshop: '', text: '' };

function matches(row: ProblemRow, filters: Filters): boolean {
  return (
    (!filters.application || row.application === filters.application) &&
    (!filters.type || row.kind === filters.type) &&
    (!filters.goesUp || row.goesUpTo === filters.goesUp) &&
    (!filters.workshop || row.workshop === filters.workshop) &&
    (!filters.text || `${row.application} ${describeProblem(row)}`.toLowerCase().includes(filters.text.toLowerCase()))
  );
}

const emptyTabs = { act: 'NothingToDecideNow', configure: 'NothingToConfigure', know: 'NothingToKnow' } as const;

function emptyText(report: ProblemsReport, tab: ProblemTab): string {
  if (report.sourceKind === 'none') {
    return translate('NoSourceConfigured');
  }
  return translate(report.rows.length === 0 ? 'NothingToDecide' : emptyTabs[tab]);
}

// Four views of the same table: what can be decided now (the default), what needs a setting, what is only to know, and what was decided
function TabBar({
  rows,
  tab,
  onTab,
}: Readonly<{ rows: ProblemRow[]; tab: ProblemTab | 'history'; onTab: (tab: ProblemTab | 'history') => void }>) {
  return (
    <div role="tablist" className={styles.tabs}>
      {tabs.map((entry) => (
        <button
          key={entry.id}
          type="button"
          role="tab"
          aria-selected={tab === entry.id}
          title={translate(entry.hint)}
          className={tab === entry.id ? styles.tabOn : styles.tab}
          onClick={() => onTab(entry.id)}
        >
          {translate(entry.label)}
          {entry.id === 'history' ? null : (
            <span className={styles.tabCount}>{rows.filter((row) => row.tab === entry.id).length}</span>
          )}
        </button>
      ))}
    </div>
  );
}

function Content({ report, tab }: Readonly<{ report: ProblemsReport; tab: ProblemTab | 'history' }>) {
  const { data: status } = useSystemStatus();
  if (tab === 'history') {
    return <History canDecide={status?.approvals.isOperator ?? false} />;
  }
  const inTab = report.rows.filter((row) => row.tab === tab);
  if (inTab.length === 0) {
    return <CapabilityHint>{emptyText(report, tab)}</CapabilityHint>;
  }
  return <Problems key={tab} report={report} inTab={inTab} />;
}

function Table({ report }: Readonly<{ report: ProblemsReport }>) {
  const [tab, setTab] = useState<ProblemTab | 'history'>('act');

  if (report.error && report.rows.length === 0) {
    return <Alert kind={kinds.DANGER}>{translate('CannotReadFleet', { error: describeError(report.error) })}</Alert>;
  }

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
      <TabBar rows={report.rows} tab={tab} onTab={setTab} />
      <Content report={report} tab={tab} />
    </>
  );
}

function Problems({ report, inTab }: Readonly<{ report: ProblemsReport; inTab: ProblemRow[] }>) {
  const { data: status } = useSystemStatus();
  const [filters, setFilters] = useState<Filters>(noFilters);
  // The selection remembers the commit that was shown: a row whose pull request moved since is no longer what was chosen
  const [chosenAt, setChosenAt] = useState<Record<string, string>>({});
  const [stops, setStops] = useState<Record<string, boolean>>({});
  const [sort, setSort] = useState<Sort | null>(null);
  const skip = useSkipProblems();

  const numbers = new Map(report.rows.map((row, index) => [row.id, index + 1]));
  const shown = sortRows(
    inTab.filter((row) => matches(row, filters)),
    numbers,
    sort,
    (row) => ({
      type: describeProblemKindShort(row.kind),
      problem: `${describeProblem(row)}`,
      why: describeWhyShort(row.why),
    }),
  );
  const chosen = report.rows.filter((row) => isActionable(row) && chosenAt[row.id] === row.headSha);
  const selected = new Set(chosen.map((row) => row.id));
  const applications = [...new Set(inTab.map((row) => row.application))].sort((a, b) => a.localeCompare(b));
  const types = [...new Set(inTab.map((row) => row.kind))];
  const set = (change: Partial<Filters>) => setFilters((current) => ({ ...current, ...change }));

  // The commit shown when the row was chosen is what the approval is bound to
  const choose = (rowsToChange: ProblemRow[], on: boolean) =>
    setChosenAt((current) => {
      const changed = new Set(rowsToChange.map((row) => row.id));
      const next = Object.fromEntries(Object.entries(current).filter(([id]) => !changed.has(id)));
      for (const row of rowsToChange) {
        if (on && row.headSha) {
          next[row.id] = row.headSha;
        }
      }
      return next;
    });
  const select = (row: ProblemRow, on: boolean) => choose([row], on);
  const selectAll = (on: boolean) => choose(shown.filter(isActionable), on);

  return (
    <>
      {inTab.length === 0 ? null : (
        <>
          <div className={styles.controls}>
            <label className={styles.filter}>
              {translate('FilterApplication')}
              <select value={filters.application} onChange={(event) => set({ application: event.target.value })}>
                <option value="">{translate('FilterAnyApplication')}</option>
                {applications.map((application) => (
                  <option key={application} value={application}>
                    {application}
                  </option>
                ))}
              </select>
            </label>
            <label className={styles.filter}>
              {translate('FilterType')}
              <select value={filters.type} onChange={(event) => set({ type: event.target.value })}>
                <option value="">{translate('FilterAnyType')}</option>
                {types.map((type) => (
                  <option key={type} value={type}>
                    {describeProblemKind(type)}
                  </option>
                ))}
              </select>
            </label>
            <label className={styles.filter}>
              {translate('FilterWorkshop')}
              <select value={filters.workshop} onChange={(event) => set({ workshop: event.target.value })}>
                <option value="">{translate('FilterAnyWorkshop')}</option>
                {workshopOrder.map((verdict) => (
                  <option key={verdict} value={verdict}>
                    {describeWorkshop(verdict)}
                  </option>
                ))}
              </select>
            </label>
            <label className={styles.filter}>
              {translate('FilterGoesUp')}
              <select value={filters.goesUp} onChange={(event) => set({ goesUp: event.target.value })}>
                <option value="">{translate('FilterAnyGoesUp')}</option>
                {ceilingOrder.map((ceiling) => (
                  <option key={ceiling} value={ceiling}>
                    {describeCeiling(ceiling)}
                  </option>
                ))}
              </select>
            </label>
            <input
              className={styles.search}
              type="search"
              placeholder={translate('SearchProblems')}
              aria-label={translate('SearchProblems')}
              value={filters.text}
              onChange={(event) => set({ text: event.target.value })}
            />
            <span className={styles.count}>
              {translate('ProblemsShown', { shown: shown.length, total: inTab.length, selected: chosen.length })}
              {report.skipped > 0 ? ` · ${translate('SkippedCount', { count: report.skipped })}` : ''}
            </span>
          </div>
          {skip.isError ? <Alert kind={kinds.DANGER}>{translate('SkipRefused')}</Alert> : null}
          {shown.length === 0 ? (
            <CapabilityHint>{translate('NoProblemMatches')}</CapabilityHint>
          ) : (
            <ProblemTable
              rows={shown}
              numbers={numbers}
              selected={selected}
              stops={stops}
              sort={sort}
              onSort={setSort}
              onSelect={select}
              onSelectAll={selectAll}
              onStop={(row, stop) => setStops((current) => ({ ...current, [row.id]: stop }))}
              onSkip={
                status?.approvals.isOperator
                  ? (row) => {
                      if (!skip.isPending) {
                        skip.mutate([row]);
                      }
                    }
                  : undefined
              }
            />
          )}
          <SelectionBar
            chosen={chosen}
            descriptionsVersion={report.descriptionsVersion}
            stops={stops}
            requiresSecondFactor={status?.approvals.requiresSecondFactor ?? true}
            isOperator={status?.approvals.isOperator ?? false}
            onDone={() => setChosenAt({})}
          />
        </>
      )}
    </>
  );
}
