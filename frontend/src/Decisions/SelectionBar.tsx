import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert';
import Button from 'Components/Button';
import form from 'Components/Form/Form.module.css';
import { ApiRefusal } from 'Helpers/Hooks/sendJson';
import { kinds } from 'Helpers/Props/kinds';
import { usePlanStatus, useValidatePlan } from 'Missions/useMissions';
import type { ProblemRow, ValidatedProblem } from 'typings/api';
import { describeApprovalError, describePlanRefusal } from 'Utilities/describe';
import translate from 'Utilities/translate';

import styles from './SelectionBar.module.css';
import { type SelectedRow, useApproveProblems } from './useProblems';

interface SelectionBarProps {
  chosen: ProblemRow[];
  descriptionsVersion: string;
  stops: Readonly<Record<string, boolean>>;
  requiresSecondFactor: boolean;
  isOperator: boolean;
  onDone: () => void;
}

// What the validation of the selection will do, application by application: one batch each, built and qualified once
function planLines(chosen: ProblemRow[], stops: Readonly<Record<string, boolean>>): string[] {
  const apps = [...new Set(chosen.map((row) => row.application))];
  return apps.flatMap((app) => {
    const all = chosen.filter((row) => row.application === app);
    const repairing = all.filter((row) => row.goesUpTo === 'repair');
    const rows = all.filter((row) => row.goesUpTo !== 'repair');
    const lines = repairing.length > 0 ? [translate('PlanRepair', { app, count: repairing.length })] : [];
    if (rows.length === 0) {
      return lines;
    }
    const promotes = rows.some((row) => row.goesUpTo === 'production' && !stops[row.id]);
    const merges = rows.some((row) => row.goesUpTo === 'mergeIntoDevelop' || row.goesUpTo === 'production');
    let key: 'PlanQualifyOnly' | 'PlanMerge' | 'PlanPromote' = 'PlanQualifyOnly';
    if (promotes) {
      key = 'PlanPromote';
    } else if (merges) {
      key = 'PlanMerge';
    }
    return [...lines, translate(key, { app, count: rows.length })];
  });
}

function started(problems: { missionId: string | null }[]): number {
  return problems.filter((problem) => problem.missionId).length;
}

function queued(problems: { queued?: boolean }[]): number {
  return problems.filter((problem) => problem.queued).length;
}

// What came of the validation through the plan: how many problems started, and why each of the others did not
function PlanResult({ problems }: Readonly<{ problems: ValidatedProblem[] }>) {
  const count = started(problems);
  const waiting = queued(problems);
  return (
    <>
      <Alert kind={count + waiting > 0 ? kinds.SUCCESS : kinds.WARNING}>
        {count > 0 ? translate('PlanValidated', { count }) : null}
        {count + waiting === 0 ? translate('PlanNothingStarted') : null}
        {waiting > 0 ? ` ${translate('PlanQueued', { count: waiting })}` : null}
      </Alert>
      {problems
        .filter((problem) => problem.refusal)
        .map((problem) => (
          <div key={problem.rowId} className={styles.muted}>
            {problem.rowId}: {describePlanRefusal(problem.refusal ?? '')}
          </div>
        ))}
    </>
  );
}

function summary(problems: number, apps: number): string {
  if (apps === 1) {
    return problems === 1 ? translate('SelectionSummaryOne') : translate('SelectionSummaryOneApp', { problems });
  }
  return translate('SelectionSummary', { problems, apps });
}

// The selection and the one code that validates it: the server computes the plan again, this only says what was chosen.
export default function SelectionBar({
  chosen,
  descriptionsVersion,
  stops,
  requiresSecondFactor,
  isOperator,
  onDone,
}: Readonly<SelectionBarProps>) {
  const approve = useApproveProblems();
  const plan = useValidatePlan();
  const planStatus = usePlanStatus();
  const viaMissions = planStatus.data?.available === true;
  const [code, setCode] = useState('');
  const apps = new Set(chosen.map((row) => row.application)).size;
  const canSubmit =
    isOperator &&
    chosen.length > 0 &&
    !approve.isPending &&
    !plan.isPending &&
    !planStatus.isPending &&
    (viaMissions || !requiresSecondFactor || code.replaceAll(' ', '').length === 6);

  function submit(event: FormEvent) {
    event.preventDefault();
    if (viaMissions) {
      plan.mutate(
        chosen.map((row) => ({ id: row.id, headSha: row.headSha ?? '', stopBeforeProduction: stops[row.id] ?? false })),
        { onSuccess: onDone },
      );
      return;
    }
    const rows: SelectedRow[] = chosen.map((row) => ({
      id: row.id,
      headSha: row.headSha ?? '',
      goesUpTo: row.goesUpTo,
      stopBeforeProduction: stops[row.id] ?? false,
    }));
    approve.mutate(
      { rows, secondFactorCode: requiresSecondFactor ? code : undefined, descriptionsVersion },
      {
        onSuccess: () => {
          setCode('');
          onDone();
        },
      },
    );
  }

  return (
    <form className={styles.bar} onSubmit={submit} aria-label={translate('ValidateSelection')}>
      <div className={styles.plan}>
        {chosen.length === 0 ? (
          <span className={styles.muted}>{translate('SelectionNone')}</span>
        ) : (
          <>
            <strong>{summary(chosen.length, apps)}</strong>
            {planLines(chosen, stops).map((line) => (
              <div key={line} className={styles.muted}>
                {line}
              </div>
            ))}
          </>
        )}
        {isOperator ? null : <div className={styles.muted}>{translate('OnlyOperatorsValidate')}</div>}
        {approve.isSuccess ? (
          <Alert kind={kinds.SUCCESS}>{translate('SelectionApproved', { count: approve.data.approvals.length })}</Alert>
        ) : null}
        {plan.isSuccess ? <PlanResult problems={plan.data.problems} /> : null}
        {plan.isError ? <Alert kind={kinds.DANGER}>{translate('PlanNotValidated')}</Alert> : null}
        {approve.isError ? (
          <Alert kind={kinds.DANGER}>
            {describeApprovalError(approve.error instanceof ApiRefusal ? approve.error.code : null)}
          </Alert>
        ) : null}
      </div>
      {requiresSecondFactor && !viaMissions ? (
        <label className={styles.code}>
          <span className={form.label}>{translate('AuthenticatorCode')}</span>
          <input
            className={form.input}
            value={code}
            onChange={(event) => setCode(event.target.value)}
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={7}
            disabled={chosen.length === 0 || !isOperator}
          />
        </label>
      ) : null}
      <Button type="submit" kind={kinds.PRIMARY} disabled={!canSubmit}>
        {translate('ValidateSelection')}
      </Button>
    </form>
  );
}
