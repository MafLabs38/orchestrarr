import * as icons from 'Helpers/Props/icons';
import { iconSizes } from 'Helpers/Props/sizes';
import type { ProblemRow } from 'typings/api';
import translate from 'Utilities/translate';

import styles from './Steps.module.css';

type StepKey = 'validate' | 'repair' | 'merge' | 'production';

// What selecting the row will do, step by step. It mirrors what the server approves for this ceiling; the server computes it again.
export function stepsOf(row: ProblemRow, stops: boolean): StepKey[] {
  switch (row.goesUpTo) {
    case 'qualification':
      return ['validate'];
    case 'repair':
      return ['validate', 'repair'];
    case 'mergeIntoDevelop':
      return ['validate', 'merge'];
    case 'production':
      return stops ? ['validate', 'merge'] : ['validate', 'merge', 'production'];
    default:
      return [];
  }
}

const short: Record<StepKey, Parameters<typeof translate>[0]> = {
  validate: 'PlanStepValidate',
  repair: 'PlanStepRepair',
  merge: 'PlanStepMerge',
  production: 'PlanStepProduction',
};

const long: Record<StepKey, Parameters<typeof translate>[0]> = {
  validate: 'StepChipValidate',
  repair: 'StepChipRepair',
  merge: 'StepChipMerge',
  production: 'StepChipProduction',
};

// The plan of a row on one line: numbered steps, the one that calls an agent marked. The full sentence of each is its tooltip.
export default function Steps({ row, stops }: Readonly<{ row: ProblemRow; stops: boolean }>) {
  const steps = stepsOf(row, stops);
  if (steps.length === 0) {
    return <span className={styles.none}>{translate('StepsNone')}</span>;
  }
  return (
    <ol className={styles.steps}>
      {steps.map((step, index) => (
        <li key={step} className={step === 'repair' ? styles.ai : styles.step} title={translate(long[step])}>
          <span className={styles.number}>{index + 1}</span>
          {step === 'repair' ? <icons.AI size={iconSizes.SMALL} aria-hidden="true" /> : null}
          {translate(short[step])}
        </li>
      ))}
    </ol>
  );
}
