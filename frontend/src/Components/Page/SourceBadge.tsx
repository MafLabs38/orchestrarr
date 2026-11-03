import Label from 'Components/Label';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import * as icons from 'Helpers/Props/icons';
import { type Kind, kinds } from 'Helpers/Props/kinds';
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
  state: string;
}

function present(source: SystemStatus['source']): Presentation {
  if (source.error !== null) {
    return { kind: kinds.DANGER, Icon: icons.DANGER, state: translate('ConnectionFailed') };
  }
  if (source.configured) {
    return { kind: kinds.SUCCESS, Icon: icons.SUCCESS, state: translate('ConnectionOk') };
  }
  return { kind: kinds.DISABLED, Icon: icons.UNKNOWN, state: translate('SourceNone') };
}

// The source of findings, in the header: where what is on screen comes from, and whether it can be read.
export default function SourceBadge() {
  const { data } = useApiQuery<SystemStatus>('/api/v1/system/status', { refetchInterval: 30_000 });
  if (!data) {
    return null;
  }

  const { kind, Icon, state } = present(data.source);
  return (
    <Label kind={kind}>
      <Icon size={14} aria-hidden="true" />
      {sourceName(data.source.kind)}
      {' · '}
      {state}
    </Label>
  );
}
