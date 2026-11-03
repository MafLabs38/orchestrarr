import { useId } from 'react';

import StatusLabel from 'Components/StatusLabel';
import * as icons from 'Helpers/Props/icons';
import { iconSizes } from 'Helpers/Props/sizes';
import type { WorkItem } from 'typings/api';
import { summarize, summarizePlan } from 'Utilities/summarize';

import DecisionCard from './DecisionCard';
import styles from './DecisionRow.module.css';

interface DecisionRowProps {
  item: WorkItem;
  expanded: boolean;
  onToggle: () => void;
}

// One application in the list: a line that says what is wrong in a few words, and opens onto the full card (findings, pull requests, plan).
export default function DecisionRow({ item, expanded, onToggle }: Readonly<DecisionRowProps>) {
  const bodyId = useId();
  const Chevron = expanded ? icons.EXPANDED : icons.COLLAPSED;

  return (
    <li className={styles.row}>
      <h3 className={styles.heading}>
        <button
          type="button"
          className={styles.toggle}
          aria-expanded={expanded}
          aria-controls={expanded ? bodyId : undefined}
          onClick={onToggle}
        >
          <Chevron size={iconSizes.LARGE} aria-hidden="true" />
          <span className={styles.title}>{item.title}</span>
          <StatusLabel severity={item.severity} />
          <span className={styles.summary}>{summarize(item).join(' · ')}</span>
          <span className={styles.plan}>{summarizePlan(item)}</span>
        </button>
      </h3>
      {expanded ? (
        <div id={bodyId} className={styles.body}>
          <DecisionCard item={item} />
        </div>
      ) : null}
    </li>
  );
}
