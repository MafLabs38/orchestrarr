import { useId, useState } from 'react';

import Button from 'Components/Button';
import CapabilityHint from 'Components/CapabilityHint';
import Card from 'Components/Card';
import StatusLabel, { SeverityIcon, severityKind } from 'Components/StatusLabel';
import { kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import type { Finding, PlanStep, WorkItem } from 'typings/api';
import { describeFinding, describeReason, describeStep } from 'Utilities/describe';
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

// One work item as the operator decides it: what is wrong, and the plan as checkboxes, all ticked by default (SPEC D-04, D-16).
// Only what the operator changed is kept, so a plan that changes under the card keeps their choices and shows the server's defaults.
export default function DecisionCard({ item, showHeader = true }: Readonly<{ item: WorkItem; showHeader?: boolean }>) {
  const headingId = useId();
  const hintId = useId();
  const [choices, setChoices] = useState<Record<string, boolean>>({});

  const isChecked = (step: PlanStep) => step.available && (choices[stepKey(step)] ?? step.ticked);
  const tickedCount = item.plan.filter(isChecked).length;
  const toggle = (step: PlanStep) => setChoices((current) => ({ ...current, [stepKey(step)]: !isChecked(step) }));

  return (
    <Card kind={severityKind(item.severity)} aria-labelledby={headingId}>
      {showHeader ? (
        <>
          <div className={styles.header}>
            <h2 id={headingId} className={styles.title}>
              {item.title}
            </h2>
            <StatusLabel severity={item.severity} />
          </div>
          {item.appIds.length > 0 ? (
            <p className={styles.apps}>{translate('Apps', { apps: item.appIds.join(', ') })}</p>
          ) : null}
        </>
      ) : null}

      <h3 className={styles.section}>{translate('Findings')}</h3>
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
              <label key={stepKey(step)} className={styles.step}>
                <input
                  type="checkbox"
                  checked={isChecked(step)}
                  disabled={!step.available}
                  onChange={() => toggle(step)}
                />
                <span className={step.available ? undefined : styles.unavailable}>{describeStep(step)}</span>
                {!step.available && step.reason ? (
                  <span className={styles.reason}>
                    {translate('StepUnavailable', { reason: describeReason(step.reason) })}
                  </span>
                ) : null}
              </label>
            ))}
          </fieldset>

          <div className={styles.footer}>
            <Button kind={kinds.PRIMARY} disabled aria-describedby={hintId}>
              {translate('ApproveAndRun')}
            </Button>
            <span className={styles.count}>
              {translate('StepsTicked', { ticked: tickedCount, total: item.plan.length })}
            </span>
          </div>
          <CapabilityHint id={hintId}>{translate('ApprovalNotAvailable')}</CapabilityHint>
        </>
      )}
    </Card>
  );
}
