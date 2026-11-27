import Alert from 'Components/Alert';
import CapabilityHint from 'Components/CapabilityHint';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import useNow from 'Helpers/Hooks/useNow';
import { kinds } from 'Helpers/Props/kinds';
import translate from 'Utilities/translate';

import styles from './FlowPage.module.css';
import LotMap from './LotMap';
import { useFlowBoard } from './useFlowBoard';
import Workshops from './Workshops';

// The flow as a fab (ADR 0056): the workshops and their equipment, the buffers in front of them, and each lot with its operations placed where they are, live
export default function FlowPage() {
  const { data: board, error, isPending } = useFlowBoard();
  const now = useNow(5_000);
  return (
    <PageContent title={translate('Flow')}>
      <PageContentBody>
        <PageHeading title={translate('Flow')} description={translate('FlowDescription')} />
        {isPending ? <CapabilityHint>{translate('Loading')}</CapabilityHint> : null}
        {error ? <Alert kind={kinds.DANGER}>{translate('CannotReadFlow', { error: error.message })}</Alert> : null}
        {board ? (
          <div className={styles.board}>
            <Workshops workshops={board.workshops} counters={board.counters} now={now} />
            {board.lots.length > 0 ? (
              <LotMap lots={board.lots} places={board.places} />
            ) : (
              <CapabilityHint>{translate('FlowEmpty')}</CapabilityHint>
            )}
          </div>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}
