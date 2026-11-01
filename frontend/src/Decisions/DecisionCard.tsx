import { useId, useState } from 'react';
import Button from 'Components/Button';
import Card from 'Components/Card';
import CapabilityHint from 'Components/CapabilityHint';
import StatusLabel, { severityKind } from 'Components/StatusLabel';
import { kinds } from 'Helpers/Props/kinds';
import * as icons from 'Helpers/Props/icons';
import type { Finding, WorkItem } from 'typings/api';
import translate from 'Utilities/translate';
import styles from './DecisionCard.module.css';

const findingIcons = { danger: icons.DANGER, warning: icons.WARNING, info: icons.INFO } as const;

function FindingRow({ finding }: { finding: Finding }) {
  const Icon = findingIcons[finding.severity];
  return (
    <li className={`${styles.finding} ${styles[finding.severity]}`}>
      <Icon size={16} aria-hidden="true" />
      <span>{finding.summary}</span>
    </li>
  );
}

// One work item as the operator decides it: what is wrong, and the plan as checkboxes, all ticked by default (SPEC D-04, D-16).
export default function DecisionCard({ item }: { item: WorkItem }) {
  const headingId = useId();
  const hintId = useId();
  const [checked, setChecked] = useState(() => item.plan.map((step) => step.ticked && step.available));
  const tickedCount = checked.filter(Boolean).length;

  const toggle = (index: number) => setChecked((current) => current.map((value, i) => (i === index ? !value : value)));

  return (
    <Card kind={severityKind(item.severity)} aria-labelledby={headingId}>
      <div className={styles.header}>
        <h2 id={headingId} className={styles.title}>
          {item.title}
        </h2>
        <StatusLabel severity={item.severity} />
      </div>
      {item.appIds.length > 0 ? <p className={styles.apps}>{translate('Apps', { apps: item.appIds.join(', ') })}</p> : null}

      <h3 className={styles.section}>{translate('Findings')}</h3>
      <ul className={styles.findings}>
        {item.findings.map((finding) => (
          <FindingRow key={`${finding.kind}-${finding.summary}`} finding={finding} />
        ))}
      </ul>

      <fieldset className={styles.plan}>
        <legend className={styles.section}>{translate('Plan')}</legend>
        {item.plan.map((step, index) => (
          <label key={`${step.kind}-${step.environmentId ?? index}`} className={styles.step}>
            <input type="checkbox" checked={checked[index]} disabled={!step.available} onChange={() => toggle(index)} />
            <span className={step.available ? undefined : styles.unavailable}>{step.label}</span>
            {!step.available && step.reason ? <span className={styles.reason}>{translate('StepUnavailable', { reason: step.reason })}</span> : null}
          </label>
        ))}
      </fieldset>

      <div className={styles.footer}>
        <Button kind={kinds.PRIMARY} disabled aria-describedby={hintId}>
          {translate('ApproveAndRun')}
        </Button>
        <span className={styles.count}>{translate('StepsTicked', { ticked: tickedCount, total: item.plan.length })}</span>
      </div>
      <CapabilityHint id={hintId}>{translate('ApprovalNotAvailable')}</CapabilityHint>
    </Card>
  );
}
