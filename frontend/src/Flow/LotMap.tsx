import classNames from 'classnames';

import { iconSizes } from 'Helpers/Props/sizes';
import type { BoardLot, BoardOperation } from 'typings/api';
import translate from 'Utilities/translate';

import styles from './LotMap.module.css';
import { operationLook, operationTitle, placeName } from './operationState';

const urgentFrom = 70;

// What is over stays in sight for a while, the newest first: the earlier ones are counted, not drawn
const outShown = 3;

function Operation({ operation }: Readonly<{ operation: BoardOperation }>) {
  const look = operationLook(operation.state);
  const title = operationTitle(operation.title, operation.kind);
  const where = operation.equipment ? ` · ${operation.equipment}` : '';
  return (
    <li
      className={classNames(styles.operation, styles[look.kind], look.waiting && styles.waiting)}
      title={operation.detail ? `${title}\n${operation.detail}` : title}
    >
      <span className={styles.operationHead}>
        <look.Icon size={iconSizes.SMALL} aria-hidden="true" />
        <span className={styles.operationTitle}>{title}</span>
      </span>
      <span className={styles.operationState}>
        <span className={styles.place}>{placeName(operation.place)} · </span>
        {look.text}
        {where}
        {operation.priority >= urgentFrom ? ` · ${translate('FlowUrgent', { priority: operation.priority })}` : ''}
      </span>
    </li>
  );
}

// The lots of the fab (ADR 0056): one row per repository, one column per place, each operation where it is now; it moves to the next column as it goes
export default function LotMap({ lots, places }: Readonly<{ lots: BoardLot[]; places: string[] }>) {
  return (
    <div
      className={styles.map}
      style={{ gridTemplateColumns: `minmax(120px, 0.8fr) repeat(${places.length}, minmax(150px, 1fr))` }}
      role="table"
      aria-label={translate('FlowLots')}
    >
      <div className={styles.row} role="row">
        <span className={styles.header} role="columnheader">
          {translate('FlowLot')}
        </span>
        {places.map((place) => (
          <span key={place} className={styles.header} role="columnheader">
            {placeName(place)}
          </span>
        ))}
      </div>
      {lots.map((lot) => (
        <div key={lot.repository} className={styles.row} role="row">
          <span className={styles.lot} role="rowheader">
            {lot.repository.split('/').pop()}
          </span>
          {places.map((place) => {
            const all = lot.operations.filter((operation) => operation.place === place);
            const here = place === 'out' ? all.slice(0, outShown) : all;
            return (
              <div
                key={place}
                className={styles.cell}
                role="cell"
                aria-label={`${lot.repository} · ${placeName(place)}`}
              >
                {here.length > 0 ? (
                  <ul className={styles.operations}>
                    {here.map((operation) => (
                      <Operation key={operation.problemId} operation={operation} />
                    ))}
                  </ul>
                ) : null}
                {all.length > here.length ? (
                  <p className={styles.more}>{translate('FlowEarlier', { count: all.length - here.length })}</p>
                ) : null}
              </div>
            );
          })}
        </div>
      ))}
    </div>
  );
}
