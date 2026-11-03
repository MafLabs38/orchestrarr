import * as icons from 'Helpers/Props/icons';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import type { Severity } from 'typings/api';
import translate from 'Utilities/translate';

import Label from './Label';

const bySeverity = {
  danger: { kind: kinds.DANGER, Icon: icons.DANGER, text: () => translate('SeverityDanger') },
  warning: { kind: kinds.WARNING, Icon: icons.WARNING, text: () => translate('SeverityWarning') },
  info: { kind: kinds.INFO, Icon: icons.INFO, text: () => translate('SeverityInfo') },
} satisfies Record<Severity, { kind: Kind; Icon: unknown; text: () => string }>;

export function severityKind(severity: Severity): Kind {
  return bySeverity[severity].kind;
}

export function SeverityIcon({ severity, size }: Readonly<{ severity: Severity; size: number }>) {
  const { Icon } = bySeverity[severity];
  return <Icon size={size} aria-hidden="true" />;
}

// A status is never conveyed by color alone: icon plus text.
export default function StatusLabel({ severity }: Readonly<{ severity: Severity }>) {
  const { kind, Icon, text } = bySeverity[severity];
  return (
    <Label kind={kind}>
      <Icon size={iconSizes.SMALL} aria-hidden="true" />
      {text()}
    </Label>
  );
}
