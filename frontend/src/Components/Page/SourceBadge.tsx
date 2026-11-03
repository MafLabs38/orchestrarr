import Label from 'Components/Label';
import * as icons from 'Helpers/Props/icons';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import useSystemStatus from 'System/useSystemStatus';
import type { SystemStatus } from 'typings/api';
import translate from 'Utilities/translate';

const sourceNames: Record<string, () => string> = {
  portarr: () => translate('SourcePortarr'),
  sample: () => translate('SourceSample'),
  none: () => translate('SourceNone'),
};

export function sourceName(kind: string): string {
  return (sourceNames[kind] ?? (() => kind))();
}

interface Presentation {
  kind: Kind;
  Icon: typeof icons.SUCCESS;
  // Said only when it adds to the name of the source
  state: string | null;
}

function present(source: SystemStatus['source']): Presentation {
  if (source.error !== null) {
    return { kind: kinds.DANGER, Icon: icons.DANGER, state: translate('ConnectionFailed') };
  }
  if (source.configured) {
    return { kind: kinds.SUCCESS, Icon: icons.SUCCESS, state: translate('ConnectionOk') };
  }
  return { kind: kinds.DISABLED, Icon: icons.UNKNOWN, state: null };
}

// The source of findings, in the header: where what is on screen comes from, and whether it can be read.
export default function SourceBadge() {
  const { data } = useSystemStatus();
  if (!data) {
    return null;
  }

  const { kind, Icon, state } = present(data.source);
  return (
    <Label kind={kind}>
      <Icon size={iconSizes.SMALL} aria-hidden="true" />
      {state ? `${sourceName(data.source.kind)} · ${state}` : sourceName(data.source.kind)}
    </Label>
  );
}
