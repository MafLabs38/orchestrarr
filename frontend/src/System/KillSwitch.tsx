import { useKillSwitch } from 'Approvals/useApprovals';
import Alert from 'Components/Alert';
import Button from 'Components/Button';
import { ApiRefusal } from 'Helpers/Hooks/sendJson';
import { kinds } from 'Helpers/Props/kinds';
import type { ApprovalStatus } from 'typings/api';
import translate from 'Utilities/translate';

// Why there is no button: approving is off, or the person is not an operator
function whyNoButton(approvals: ApprovalStatus): string {
  return translate(approvals.enabled ? 'KillSwitchOperatorsOnly' : 'KillSwitchUnavailable');
}

// Why the change failed: a stop that cannot be released for certain stays engaged, and says so; anything else is a plain failure
function failureText(error: unknown): string {
  return error instanceof ApiRefusal && error.code === 'killSwitchUnavailable'
    ? translate('KillSwitchCannotBeReleased')
    : translate('KillSwitchFailed');
}

// The global stop (SPEC D-43): engaged, nothing is approved and nothing launches. Only an operator can engage or release it.
export default function KillSwitch({ approvals }: Readonly<{ approvals: ApprovalStatus }>) {
  const killSwitch = useKillSwitch();

  return (
    <section aria-label={translate('KillSwitch')}>
      {approvals.killSwitchEngaged ? <Alert kind={kinds.DANGER}>{translate('KillSwitchEngaged')}</Alert> : null}
      {approvals.enabled && approvals.isOperator ? (
        <Button
          kind={approvals.killSwitchEngaged ? kinds.DEFAULT : kinds.DANGER}
          disabled={killSwitch.isPending}
          onClick={() => killSwitch.mutate(!approvals.killSwitchEngaged)}
        >
          {approvals.killSwitchEngaged ? translate('KillSwitchRelease') : translate('KillSwitchEngage')}
        </Button>
      ) : (
        <p>{whyNoButton(approvals)}</p>
      )}
      {killSwitch.isError ? <Alert kind={kinds.DANGER}>{failureText(killSwitch.error)}</Alert> : null}
    </section>
  );
}
