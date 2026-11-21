import Alert from 'Components/Alert';
import CapabilityHint from 'Components/CapabilityHint';
import Label from 'Components/Label';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import type { MissionView } from 'typings/api';
import translate from 'Utilities/translate';

import styles from './Missions.module.css';
import { useMissions } from './useMissions';

// Where a mission is, as a coloured word (the word says it too)
function stateOf(mission: MissionView): { text: string; kind: Kind } {
  if (mission.end === 'Done' || mission.state === 'Accepted') {
    return {
      text: mission.state === 'Accepted' ? translate('MissionAccepted') : translate('MissionDone'),
      kind: kinds.SUCCESS,
    };
  }
  if (mission.state === 'Ended') {
    return { text: mission.endReason ? `${mission.end}` : translate('MissionEnded'), kind: kinds.DANGER };
  }
  if (mission.state === 'HandingOver') {
    return { text: translate('MissionControllerReading'), kind: kinds.INFO };
  }
  if (mission.state === 'Running') {
    return { text: translate('MissionRunning'), kind: kinds.WARNING };
  }
  return { text: translate('MissionWaiting'), kind: kinds.DEFAULT };
}

// What the conductor does with each approved problem: the mission of each world, what its runner reports, the dossier it hands over and what the controller decided.
export default function Missions() {
  const { data: missions, error, isPending } = useMissions();

  return (
    <PageContent title={translate('Missions')}>
      <PageContentBody>
        <PageHeading title={translate('Missions')} description={translate('MissionsDescription')} />
        {isPending ? <CapabilityHint>{translate('Loading')}</CapabilityHint> : null}
        {error ? <Alert kind={kinds.DANGER}>{translate('CannotReadMissions', { error: error.message })}</Alert> : null}
        {missions?.length === 0 ? <CapabilityHint>{translate('MissionsEmpty')}</CapabilityHint> : null}
        {missions && missions.length > 0 ? (
          <ul className={styles.list} aria-label={translate('Missions')}>
            {missions.map((mission) => {
              const state = stateOf(mission);
              return (
                <li key={mission.id} className={styles.mission}>
                  <div className={styles.head}>
                    <Label kind={kinds.DEFAULT}>{mission.world}</Label>
                    <Label kind={state.kind}>{state.text}</Label>
                    <span className={styles.objective}>{mission.objective}</span>
                  </div>
                  <p className={styles.meta}>
                    {mission.applicationId}
                    {mission.mandate
                      ? ` · ${translate('MissionMandate', { runner: mission.mandate.runnerId, acts: mission.mandate.acts.join(', ') || '-' })}`
                      : ''}
                  </p>
                  {mission.dossier ? <p className={styles.claim}>{mission.dossier.claim}</p> : null}
                  {mission.endReason ? <p className={styles.claim}>{mission.endReason}</p> : null}
                  {mission.progress && mission.progress.length > 0 ? (
                    <pre className={styles.progress}>{mission.progress.join('\n')}</pre>
                  ) : null}
                </li>
              );
            })}
          </ul>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}
