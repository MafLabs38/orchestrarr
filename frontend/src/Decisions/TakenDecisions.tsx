import { operationLook, operationTitle, placeName } from 'Flow/operationState';
import { useFlowBoard } from 'Flow/useFlowBoard';

import CapabilityHint from 'Components/CapabilityHint';
import Label from 'Components/Label';
import useNow from 'Helpers/Hooks/useNow';
import { iconSizes } from 'Helpers/Props/sizes';
import type { BoardOperation } from 'typings/api';
import { describeAge } from 'Utilities/describe';
import translate from 'Utilities/translate';

import styles from './TakenDecisions.module.css';

// What is going on first (in process, then in the line), then what is over, the newest first
function order(operation: BoardOperation): number {
  if (operation.place === 'out') {
    return 2;
  }
  return operation.place === 'queue' ? 1 : 0;
}

// The decisions taken (ADR 0056): each row that was validated, with what became of it, live, from the same reading as the Flow screen
export default function TakenDecisions() {
  const { data: board } = useFlowBoard();
  const now = useNow(15_000);
  const operations = (board?.lots ?? [])
    .flatMap((lot) => lot.operations)
    .sort((a, b) => order(a) - order(b) || Date.parse(b.since) - Date.parse(a.since));
  return (
    <section className={styles.taken} aria-label={translate('DecisionsTaken')}>
      <h2 className={styles.title}>{translate('DecisionsTaken')}</h2>
      <p className={styles.description}>{translate('DecisionsTakenDescription')}</p>
      {operations.length === 0 ? (
        <CapabilityHint>{translate('DecisionsTakenEmpty')}</CapabilityHint>
      ) : (
        <div className={styles.wrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>{translate('ColumnOperation')}</th>
                <th>{translate('FlowLot')}</th>
                <th>{translate('ColumnWhere')}</th>
                <th>{translate('ColumnState')}</th>
                <th>{translate('ColumnSince')}</th>
              </tr>
            </thead>
            <tbody>
              {operations.map((operation) => {
                const look = operationLook(operation.state);
                return (
                  <tr key={operation.problemId}>
                    <td className={styles.operation}>{operationTitle(operation.title, operation.kind)}</td>
                    <td>{operation.repository.split('/').pop()}</td>
                    <td>{placeName(operation.place)}</td>
                    <td>
                      <Label kind={look.kind}>
                        <look.Icon size={iconSizes.SMALL} aria-hidden="true" />
                        {look.text}
                      </Label>
                      {operation.detail ? <p className={styles.detail}>{operation.detail}</p> : null}
                    </td>
                    <td>{describeAge(now - Date.parse(operation.since))}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
