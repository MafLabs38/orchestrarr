import Alert from 'Components/Alert';
import CapabilityHint from 'Components/CapabilityHint';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { kinds } from 'Helpers/Props/kinds';
import translate from 'Utilities/translate';

import Flow from './Flow';
import MachineBoard from './MachineBoard';
import { useFlow } from './useFlow';
import { useMissions } from './useMissions';

// What the conductor does with each approved problem, followed live: where it stands in each world, what it does now or what it waits for, and, behind each, the missions, the dossier and what the controller decided.
export default function Missions() {
  const { data: problems, error, isPending } = useFlow();
  const { data: missions } = useMissions();

  return (
    <PageContent title={translate('Missions')}>
      <PageContentBody>
        <PageHeading title={translate('Missions')} description={translate('MissionsDescription')} />
        {isPending ? <CapabilityHint>{translate('Loading')}</CapabilityHint> : null}
        {error ? <Alert kind={kinds.DANGER}>{translate('CannotReadFlow', { error: error.message })}</Alert> : null}
        {problems ? <Flow problems={problems} missions={missions ?? []} /> : null}
        <MachineBoard />
      </PageContentBody>
    </PageContent>
  );
}
