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
import type { Ceiling, ProblemRow, ProblemsReport } from 'typings/api';
import { describeCeiling, describeError, describeProblem, describeProblemKind } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './Decisions.module.css';
import ProblemTable, { isActionable } from './ProblemTable';
import SelectionBar from './SelectionBar';
import useProblems, { useRefreshProblems } from './useProblems';

const ceilingOrder: Ceiling[] = ['production', 'mergeIntoDevelop', 'qualification', 'noAction'];

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
  text: string;
}

const noFilters: Filters = { application: '', type: '', goesUp: '', text: '' };

function matches(row: ProblemRow, filters: Filters): boolean {
  return (
    (!filters.application || row.application === filters.application) &&
    (!filters.type || row.kind === filters.type) &&
    (!filters.goesUp || row.goesUpTo === filters.goesUp) &&
    (!filters.text || `${row.application} ${describeProblem(row)}`.toLowerCase().includes(filters.text.toLowerCase()))
  );
}

function Table({ report }: Readonly<{ report: ProblemsReport }>) {
  const { data: status } = useSystemStatus();
  const [filters, setFilters] = useState<Filters>(noFilters);
  // The selection remembers the commit that was shown: a row whose pull request moved since is no longer what was chosen
  const [chosenAt, setChosenAt] = useState<Record<string, string>>({});
  const [stops, setStops] = useState<Record<string, boolean>>({});

  if (report.error && report.rows.length === 0) {
    return <Alert kind={kinds.DANGER}>{translate('CannotReadFleet', { error: describeError(report.error) })}</Alert>;
  }

  const shown = report.rows.filter((row) => matches(row, filters));
  const chosen = report.rows.filter((row) => isActionable(row) && chosenAt[row.id] === row.headSha);
  const selected = new Set(chosen.map((row) => row.id));
  const applications = [...new Set(report.rows.map((row) => row.application))].sort((a, b) => a.localeCompare(b));
  const types = [...new Set(report.rows.map((row) => row.kind))];
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
      {report.stale && report.error ? (
        <Alert kind={kinds.WARNING}>
          {translate('ShowingLastRead', {
            time: formatTime(report.readAt) ?? translate('Never'),
            error: describeError(report.error),
          })}
        </Alert>
      ) : null}
      {report.rows.length === 0 ? (
        <CapabilityHint>
          {report.sourceKind === 'none' ? translate('NoSourceConfigured') : translate('NothingToDecide')}
        </CapabilityHint>
      ) : (
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
              {translate('ProblemsShown', { shown: shown.length, total: report.rows.length, selected: chosen.length })}
            </span>
          </div>
          {shown.length === 0 ? (
            <CapabilityHint>{translate('NoProblemMatches')}</CapabilityHint>
          ) : (
            <ProblemTable
              rows={shown}
              selected={selected}
              stops={stops}
              onSelect={select}
              onSelectAll={selectAll}
              onStop={(row, stop) => setStops((current) => ({ ...current, [row.id]: stop }))}
            />
          )}
          <SelectionBar
            chosen={chosen}
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
