import { useKillSwitch } from 'Approvals/useApprovals';
import Alert from 'Components/Alert';
import Button from 'Components/Button';
import { ApiRefusal } from 'Helpers/Hooks/sendJson';
import { kinds } from 'Helpers/Props/kinds';
import type { ApprovalStatus, StorageKind } from 'typings/api';
import translate from 'Utilities/translate';

// Why there is no button: the person is not an operator
function whyNoButton(): string {
  return translate('KillSwitchOperatorsOnly');
}

const releaseFailureKeys = {
  killSwitchUnavailable: 'KillSwitchCannotBeReleased',
  auditUnavailable: 'KillSwitchCannotBeTraced',
} as const;

// Why the change failed: a stop whose release cannot be saved or traced stays engaged, and says so; anything else is a plain failure
function failureText(error: unknown): string {
  const code = error instanceof ApiRefusal ? error.code : null;
  return translate(
    code !== null && Object.hasOwn(releaseFailureKeys, code)
      ? releaseFailureKeys[code as keyof typeof releaseFailureKeys]
      : 'KillSwitchFailed',
  );
}

// The global stop (SPEC D-43): engaged, nothing is approved and nothing launches. Only an operator can engage or release it.
export default function KillSwitch({
  approvals,
  storageKind,
}: Readonly<{ approvals: ApprovalStatus; storageKind: StorageKind }>) {
  const killSwitch = useKillSwitch();

  return (
    <section aria-label={translate('KillSwitch')}>
      {approvals.killSwitchEngaged ? <Alert kind={kinds.DANGER}>{translate('KillSwitchEngaged')}</Alert> : null}
      {/* In memory, nothing is kept by choice and the storage line says so: the warning is for a file that failed */}
      {approvals.killSwitchEngaged && !approvals.killSwitchSaved && storageKind === 'sqlite' ? (
        <Alert kind={kinds.WARNING}>{translate('KillSwitchNotSaved')}</Alert>
      ) : null}
      {approvals.isOperator ? (
        <Button
          kind={approvals.killSwitchEngaged ? kinds.DEFAULT : kinds.DANGER}
          disabled={killSwitch.isPending}
          onClick={() => killSwitch.mutate(!approvals.killSwitchEngaged)}
        >
          {approvals.killSwitchEngaged ? translate('KillSwitchRelease') : translate('KillSwitchEngage')}
        </Button>
      ) : (
        <p>{whyNoButton()}</p>
      )}
      {killSwitch.isError ? <Alert kind={kinds.DANGER}>{failureText(killSwitch.error)}</Alert> : null}
    </section>
  );
}
