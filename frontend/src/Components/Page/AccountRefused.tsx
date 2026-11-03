import Alert from 'Components/Alert';
import { kinds } from 'Helpers/Props/kinds';
import translate from 'Utilities/translate';

import useSession from './useSession';

// The identity provider knows this person, and Orchestrarr does not let them in (not in the reader group, or an identity it cannot
// carry): every screen would fail, so say it once, and the header still offers the way out.
export default function AccountRefused() {
  const { data } = useSession();
  if (!data || data.canRead) {
    return null;
  }

  return <Alert kind={kinds.DANGER}>{translate('AccountRefused')}</Alert>;
}
