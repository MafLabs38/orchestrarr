import useApiQuery from 'Helpers/Hooks/useApiQuery';
import { kinds } from 'Helpers/Props/kinds';
import * as icons from 'Helpers/Props/icons';
import type { SystemStatus } from 'typings/api';
import translate from 'Utilities/translate';
import Label from 'Components/Label';

const sourceNames: Record<string, () => string> = {
  portarr: () => translate('SourcePortarr'),
  sample: () => translate('SourceSample'),
  none: () => translate('SourceNone'),
};

export function sourceName(kind: string): string {
  return (sourceNames[kind] ?? (() => kind))();
}

// The source of findings, in the header: where what is on screen comes from, and whether it can be read.
export default function SourceBadge() {
  const { data } = useApiQuery<SystemStatus>('/api/v1/system/status', { refetchInterval: 30_000 });
  if (!data) {
    return null;
  }

  const failing = data.source.error !== null;
  const Icon = failing ? icons.DANGER : data.source.configured ? icons.SUCCESS : icons.UNKNOWN;
  const kind = failing ? kinds.DANGER : data.source.configured ? kinds.SUCCESS : kinds.DISABLED;
  return (
    <Label kind={kind}>
      <Icon size={14} aria-hidden="true" />
      {sourceName(data.source.kind)}
      {' · '}
      {failing ? translate('ConnectionFailed') : data.source.configured ? translate('ConnectionOk') : translate('SourceNone')}
    </Label>
  );
}
