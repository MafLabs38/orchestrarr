import classNames from 'classnames';
import { Link, useSearchParams } from 'react-router-dom';

import Alert from 'Components/Alert';
import CapabilityHint from 'Components/CapabilityHint';
import Label from 'Components/Label';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import type { BoardOperation, TravelerStep } from 'typings/api';
import translate from 'Utilities/translate';

import styles from './HistoryPage.module.css';
import { operationLook, operationTitle, placeName } from './operationState';
import { describeStep } from './travelerEvents';
import { useFlowBoard } from './useFlowBoard';
import { useTraveler } from './useTraveler';

export const historyOf = (problemId: string) => `/history?problem=${encodeURIComponent(problemId)}`;

function time(at: string): string {
  return new Date(at).toLocaleTimeString();
}

function Step({ step }: Readonly<{ step: TravelerStep }>) {
  const look = describeStep(step.event);
  return (
    <li className={classNames(styles.step, styles[look.kind])}>
      <span className={styles.time}>{time(step.at)}</span>
      <span className={styles.what}>
        <Label kind={look.kind}>{look.text}</Label>
        {step.world ? <span className={styles.world}>{placeName(step.world)}</span> : null}
        {step.times > 1 ? <span className={styles.times}>{translate('StepTimes', { count: step.times })}</span> : null}
      </span>
      {step.text ? <span className={styles.text}>{step.text}</span> : null}
    </li>
  );
}

// The traveler of one operation (ADR 0056): its steps one after the other, live
function Traveler({ problemId }: Readonly<{ problemId: string }>) {
  const { data, error, isPending } = useTraveler(problemId);
  if (isPending) {
    return <CapabilityHint>{translate('Loading')}</CapabilityHint>;
  }
  if (error || !data) {
    return <Alert kind={kinds.DANGER}>{translate('CannotReadTraveler')}</Alert>;
  }
  return (
    <section className={styles.traveler} aria-label={translate('Traveler')}>
      <h2 className={styles.title}>{operationTitle(data.title, data.kind)}</h2>
      <p className={styles.meta}>
        {data.repository} · {translate('ColumnPriority')} {data.priority}
        {data.note ? ` · ${data.note}` : ''}
      </p>
      {data.steps.length === 0 ? (
        <CapabilityHint>{translate('TravelerEmpty')}</CapabilityHint>
      ) : (
        <ol className={styles.steps}>
          {data.steps.map((step, index) => (
            <Step key={`${step.at}-${step.event}-${index}`} step={step} />
          ))}
        </ol>
      )}
    </section>
  );
}

function order(operation: BoardOperation): number {
  if (operation.place === 'out') {
    return 2;
  }
  return operation.place === 'queue' ? 1 : 0;
}

// The History of the fab (ADR 0056): the operations of the flow, and the traveler of the one chosen
export default function HistoryPage() {
  const [search] = useSearchParams();
  const chosen = search.get('problem');
  const { data: board } = useFlowBoard();
  const operations = (board?.lots ?? [])
    .flatMap((lot) => lot.operations)
    .sort((a, b) => order(a) - order(b) || Date.parse(b.since) - Date.parse(a.since));
  return (
    <PageContent title={translate('HistoryTitle')}>
      <PageContentBody>
        <PageHeading title={translate('HistoryTitle')} description={translate('HistoryTravelerDescription')} />
        <div className={styles.layout}>
          <nav className={styles.list} aria-label={translate('Operations')}>
            {operations.length === 0 ? <CapabilityHint>{translate('DecisionsTakenEmpty')}</CapabilityHint> : null}
            <ul>
              {operations.map((operation) => {
                const look = operationLook(operation.state);
                return (
                  <li key={operation.problemId}>
                    <Link
                      to={historyOf(operation.problemId)}
                      className={classNames(styles.entry, operation.problemId === chosen && styles.chosen)}
                      aria-current={operation.problemId === chosen ? 'page' : undefined}
                    >
                      <span className={styles.entryTitle}>{operationTitle(operation.title, operation.kind)}</span>
                      <span className={styles.entryMeta}>
                        <look.Icon size={iconSizes.SMALL} aria-hidden="true" />
                        {operation.repository.split('/').pop()} · {look.text}
                      </span>
                    </Link>
                  </li>
                );
              })}
            </ul>
          </nav>
          {chosen ? <Traveler problemId={chosen} /> : <CapabilityHint>{translate('TravelerChoose')}</CapabilityHint>}
        </div>
      </PageContentBody>
    </PageContent>
  );
}
