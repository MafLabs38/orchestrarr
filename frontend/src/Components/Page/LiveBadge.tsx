import { useSyncExternalStore } from 'react';

import Label from 'Components/Label';
import * as icons from 'Helpers/Props/icons';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import { type LiveState, liveStatus } from 'Live/liveStatus';
import translate from 'Utilities/translate';

const presentation: Record<LiveState, { kind: Kind; Icon: typeof icons.SUCCESS; text: () => string }> = {
  live: { kind: kinds.SUCCESS, Icon: icons.SUCCESS, text: () => translate('LiveOn') },
  connecting: { kind: kinds.DISABLED, Icon: icons.UNKNOWN, text: () => translate('LiveConnecting') },
  reconnecting: { kind: kinds.WARNING, Icon: icons.WARNING, text: () => translate('LiveReconnecting') },
  off: { kind: kinds.DISABLED, Icon: icons.UNKNOWN, text: () => translate('LiveOff') },
};

// In the header: whether what is on screen follows what happens on its own (the WebSocket), or is read again every thirty seconds.
export default function LiveBadge() {
  const state = useSyncExternalStore(liveStatus.subscribe, liveStatus.get);
  const { kind, Icon, text } = presentation[state];
  return (
    <span title={translate(state === 'live' ? 'LiveOnHint' : 'LiveOffHint')}>
      <Label kind={kind}>
        <Icon size={iconSizes.SMALL} /> {text()}
      </Label>
    </span>
  );
}
