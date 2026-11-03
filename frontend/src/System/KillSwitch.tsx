import { useKillSwitch } from 'Approvals/useApprovals';
import Alert from 'Components/Alert';
import Button from 'Components/Button';
import { kinds } from 'Helpers/Props/kinds';
import type { ApprovalStatus } from 'typings/api';
import translate from 'Utilities/translate';

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
        <p>{translate(approvals.enabled ? 'KillSwitchOperatorsOnly' : 'KillSwitchUnavailable')}</p>
      )}
      {killSwitch.isError ? <Alert kind={kinds.DANGER}>{translate('KillSwitchFailed')}</Alert> : null}
    </section>
  );
}
