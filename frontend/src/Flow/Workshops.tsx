import { Fragment } from 'react';

import Label from 'Components/Label';
import * as icons from 'Helpers/Props/icons';
import { iconSizes } from 'Helpers/Props/sizes';
import type { BoardCounters, BoardEquipment, BoardWorkshop } from 'typings/api';
import { describeAge } from 'Utilities/describe';
import translate from 'Utilities/translate';

import { equipmentLook, operationLook, operationTitle, placeName } from './operationState';
import styles from './Workshops.module.css';

function Equipment({ equipment }: Readonly<{ equipment: BoardEquipment }>) {
  const look = equipmentLook(equipment.state);
  const Icon = equipment.kind === 'slot' ? icons.SLOT : icons.MACHINE;
  return (
    <li className={styles.equipment} data-state={equipment.state} aria-label={`${equipment.id}: ${look.text}`}>
      <div className={styles.equipmentHead}>
        <Icon size={iconSizes.MEDIUM} aria-hidden="true" />
        <span className={styles.equipmentName}>{equipment.kind === 'slot' ? translate('FlowSlot') : equipment.id}</span>
        <Label kind={look.kind}>{look.text}</Label>
      </div>
      {equipment.title ? (
        <p className={styles.on} title={equipment.title}>
          {operationTitle(equipment.title, null)}
          {equipment.repository ? <span className={styles.repository}> · {equipment.repository}</span> : null}
        </p>
      ) : null}
      {equipment.reason ? <p className={styles.reason}>{equipment.reason}</p> : null}
    </li>
  );
}

function Workshop({ workshop }: Readonly<{ workshop: BoardWorkshop }>) {
  return (
    <section className={styles.station} aria-label={placeName(workshop.world)}>
      <h3 className={styles.stationName}>{placeName(workshop.world)}</h3>
      <p className={styles.counts}>
        {translate('FlowWorkshopCounts', { inProcess: workshop.inProcess, waiting: workshop.waiting })}
      </p>
      <ul className={styles.equipmentList}>
        {workshop.equipment.map((equipment) => (
          <Equipment key={equipment.id} equipment={equipment} />
        ))}
      </ul>
      {workshop.waiting > 0 ? (
        <p className={styles.buffer}>
          {translate('FlowBuffer', {
            count: workshop.waiting,
            why: workshop.why.map((state) => operationLook(state).text).join(', '),
          })}
        </p>
      ) : null}
    </section>
  );
}

function Arrow() {
  return (
    <span className={styles.arrow} aria-hidden="true">
      <icons.NEXT size={iconSizes.LARGE} />
    </span>
  );
}

// The fab at a glance (ADR 0056): the line before a start, each workshop with its equipment and the buffer in front of it, and what came out
export default function Workshops({
  workshops,
  counters,
  now,
}: Readonly<{ workshops: BoardWorkshop[]; counters: BoardCounters; now: number }>) {
  return (
    <div className={styles.line} aria-label={translate('FlowWorkshops')}>
      <section className={styles.station} aria-label={placeName('queue')}>
        <h3 className={styles.stationName}>{placeName('queue')}</h3>
        <p className={styles.big}>{counters.queued}</p>
        <p className={styles.counts}>
          {counters.oldestQueuedSince
            ? translate('FlowOldestWaits', { age: describeAge(now - Date.parse(counters.oldestQueuedSince)) })
            : translate('FlowLineEmpty')}
        </p>
      </section>
      {workshops.map((workshop) => (
        <Fragment key={workshop.world}>
          <Arrow />
          <Workshop workshop={workshop} />
        </Fragment>
      ))}
      <Arrow />
      <section className={styles.station} aria-label={placeName('out')}>
        <h3 className={styles.stationName}>{placeName('out')}</h3>
        <p className={styles.outcome}>
          <Label kind={operationLook('done').kind}>{translate('FlowDoneCount', { count: counters.done })}</Label>
        </p>
        <p className={styles.outcome}>
          <Label kind={operationLook('putAside').kind}>
            {translate('FlowPutAsideCount', { count: counters.putAside })}
          </Label>
        </p>
      </section>
    </div>
  );
}
