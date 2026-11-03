import { useEffect, useId, useState } from 'react';
import { Link } from 'react-router-dom';

import { useApprove } from 'Approvals/useApprovals';
import Alert from 'Components/Alert';
import Button from 'Components/Button';
import CapabilityHint from 'Components/CapabilityHint';
import { SeverityIcon } from 'Components/StatusLabel';
import { ApiRefusal } from 'Helpers/Hooks/sendJson';
import { kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import useSystemStatus from 'System/useSystemStatus';
import type { ApprovalStatus, Finding, PlanStep, StepKind, WorkItem } from 'typings/api';
import { describeApprovalError, describeFinding, describeReason, describeStep } from 'Utilities/describe';
import translate from 'Utilities/translate';

import styles from './DecisionCard.module.css';
import PullRequestList from './PullRequestList';

function FindingRow({ finding }: Readonly<{ finding: Finding }>) {
  return (
    <li className={`${styles.finding} ${styles[finding.severity]}`}>
      <SeverityIcon severity={finding.severity} size={iconSizes.MEDIUM} />
      <span>{describeFinding(finding)}</span>
    </li>
  );
}

// A step is identified by what it does and where, not by its place in the list: the plan can change between two reads.
const stepKey = (step: PlanStep) => `${step.kind}:${step.environmentId ?? ''}`;

// What an approval can record today: building and testing a pull request, and merging those that pass. The other steps are shown and
// can be granted, but run with later versions.
const approvableKinds: StepKind[] = ['validate', 'mergePullRequests'];
const isApprovable = (step: PlanStep) => step.environmentId === null && approvableKinds.includes(step.kind);

// Why a step cannot be approved as it stands, if it cannot: possible at all, granted, and a step that approving records today.
function stepNote(step: PlanStep): string | null {
  if (!step.available) {
    return step.reason ? translate('StepUnavailable', { reason: describeReason(step.reason) }) : null;
  }
  if (!step.granted) {
    return translate('StepNotGranted');
  }
  return isApprovable(step) ? null : translate('StepNotRunYet');
}

// One step of the plan: its checkbox, and its note beside it, not inside the label, so that the name of the checkbox stays the step.
function StepRow({ step, checked, onToggle }: Readonly<{ step: PlanStep; checked: boolean; onToggle: () => void }>) {
  const noteId = useId();
  const note = stepNote(step);
  return (
    <div className={styles.step}>
      <label className={styles.stepLabel}>
        <input
          type="checkbox"
          checked={checked}
          disabled={!step.available}
          aria-describedby={note ? noteId : undefined}
          onChange={onToggle}
        />
        <span className={step.available ? undefined : styles.unavailable}>{describeStep(step)}</span>
      </label>
      {note ? (
        <span id={noteId} className={styles.reason}>
          {note}
        </span>
      ) : null}
    </div>
  );
}

// What to say under the button: why it cannot be pressed, or what pressing it does.
function approvalHint(approvals: ApprovalStatus | undefined, plan: PlanStep[], pullRequestsRead: boolean): string {
  if (!approvals?.enabled) {
    return translate('ApprovalDisabledHint');
  }
  if (approvals.killSwitchEngaged) {
    return translate('ApprovalStoppedHint');
  }
  if (!plan.some((step) => step.available && step.granted && isApprovable(step))) {
    return translate('ApprovalNothingGrantedHint');
  }
  return pullRequestsRead ? translate('ApprovalWaitsHint') : translate('ApprovalPullRequestsNotReadHint');
}

// The detail of one work item, under its line in the list: what is wrong, the pull requests, and the plan as checkboxes, all ticked by
// default (SPEC D-04, D-16). Only what the operator changed is kept, so a plan that changes under the card keeps their choices.
export default function DecisionCard({ item }: Readonly<{ item: WorkItem }>) {
  const hintId = useId();
  const [choices, setChoices] = useState<Record<string, boolean>>({});
  const { data: status } = useSystemStatus();
  const approve = useApprove();
  const approvals = status?.approvals;

  const isChecked = (step: PlanStep) => step.available && (choices[stepKey(step)] ?? step.ticked);
  const tickedCount = item.plan.filter(isChecked).length;
  const toggle = (step: PlanStep) => {
    approve.reset(); // an approval that was recorded is for the ticks that were made then
    setChoices((current) => ({ ...current, [stepKey(step)]: !isChecked(step) }));
  };

  // The approval covers the pull requests, and the commits, that this card displays: they are sent back, and the server refuses if they changed.
  const seen = item.pullRequests.items
    .filter((pullRequest) => !pullRequest.draft)
    .map(({ number, headSha }) => ({ number, headSha }));
  const seenKey = seen.map((pullRequest) => `${pullRequest.number}@${pullRequest.headSha}`).join();
  const { reset } = approve;
  useEffect(() => {
    reset(); // what was said about an approval no longer holds once the pull requests, or their commits, are not the same
  }, [seenKey, reset]);
  const pullRequestsRead = item.pullRequests.status === 'read' && !item.pullRequests.stale && seen.length > 0;

  // Only what the server would accept is sent: ticked, possible, granted, and a step that approving records. The server decides the rest.
  const approvable = item.plan.filter((step) => isChecked(step) && step.granted && isApprovable(step));
  const canApprove =
    approvals?.enabled === true &&
    !approvals.killSwitchEngaged &&
    approvable.length > 0 &&
    pullRequestsRead &&
    !approve.isPending;
  const hint = approvalHint(approvals, item.plan, pullRequestsRead);

  return (
    <section aria-label={item.title}>
      {item.appIds.length > 0 ? (
        <p className={styles.apps}>{translate('Apps', { apps: item.appIds.join(', ') })}</p>
      ) : null}

      <h4 className={styles.section}>{translate('Findings')}</h4>
      <ul className={styles.findings}>
        {item.findings.map((finding) => (
          <FindingRow key={`${finding.kind}-${finding.image?.reference ?? ''}`} finding={finding} />
        ))}
      </ul>

      <PullRequestList item={item} />

      {item.plan.length === 0 ? (
        <CapabilityHint>{translate('NoAutomaticPlan')}</CapabilityHint>
      ) : (
        <>
          <fieldset className={styles.plan}>
            <legend className={styles.section}>{translate('Plan')}</legend>
            {item.plan.map((step) => (
              <StepRow key={stepKey(step)} step={step} checked={isChecked(step)} onToggle={() => toggle(step)} />
            ))}
          </fieldset>

          <div className={styles.footer}>
            <Button
              kind={kinds.PRIMARY}
              disabled={!canApprove}
              aria-describedby={hintId}
              onClick={() =>
                approve.mutate({ workItemId: item.id, steps: approvable.map((step) => step.kind), pullRequests: seen })
              }
            >
              {translate('Approve')}
            </Button>
            <span className={styles.count}>
              {translate('StepsTicked', { ticked: tickedCount, total: item.plan.length })}
            </span>
          </div>
          <CapabilityHint id={hintId}>{hint}</CapabilityHint>
          {approve.isSuccess ? (
            <Alert kind={kinds.SUCCESS}>
              {translate(approve.data.approvals.length === 1 ? 'ApprovalRecordedOne' : 'ApprovalRecordedOther', {
                count: approve.data.approvals.length,
              })}{' '}
              <Link to="/queue">{translate('SeeQueue')}</Link>
            </Alert>
          ) : null}
          {approve.isError ? (
            <Alert kind={kinds.DANGER}>
              {describeApprovalError(approve.error instanceof ApiRefusal ? approve.error.code : null)}
            </Alert>
          ) : null}
        </>
      )}
    </section>
  );
}
