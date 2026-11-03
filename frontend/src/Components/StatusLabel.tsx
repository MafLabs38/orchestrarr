import * as icons from 'Helpers/Props/icons';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import type { Severity } from 'typings/api';
import translate from 'Utilities/translate';

import Label from './Label';

const byseverity = {
  danger: { kind: kinds.DANGER, Icon: icons.DANGER, text: () => translate('SeverityDanger') },
  warning: { kind: kinds.WARNING, Icon: icons.WARNING, text: () => translate('SeverityWarning') },
  info: { kind: kinds.INFO, Icon: icons.INFO, text: () => translate('SeverityInfo') },
} satisfies Record<Severity, { kind: Kind; Icon: unknown; text: () => string }>;

export function severityKind(severity: Severity): Kind {
  return byseverity[severity].kind;
}

// A status is never conveyed by color alone: icon plus text.
export default function StatusLabel({ severity }: { severity: Severity }) {
  const { kind, Icon, text } = byseverity[severity];
  return (
    <Label kind={kind}>
      <Icon size={14} aria-hidden="true" />
      {text()}
    </Label>
  );
}
