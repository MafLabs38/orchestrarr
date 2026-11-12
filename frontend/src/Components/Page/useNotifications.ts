import useSystemStatus from 'System/useSystemStatus';
import translate from 'Utilities/translate';

export interface Notification {
  id: string;
  // An important one is for something that lowers security or stops the work: it gets the exclamation mark and is counted
  important: boolean;
  title: string;
  text: string;
}

// What the header's bell says, from the status of the host. They are not a log (the Activity screen is): each is a state that holds now and goes
// away when it stops holding.
export default function useNotifications(): Notification[] {
  const { data: status } = useSystemStatus();
  if (!status) {
    return [];
  }
  const notifications: Notification[] = [];
  if (!status.approvals.requiresSecondFactor) {
    notifications.push({
      id: 'second-factor-off',
      important: true,
      title: translate('NotificationSecondFactorOffTitle'),
      text: translate('NotificationSecondFactorOffText'),
    });
  }
  if (status.approvals.killSwitchEngaged) {
    notifications.push({
      id: 'global-stop',
      important: true,
      title: translate('NotificationStopTitle'),
      text: translate('NotificationStopText'),
    });
  }
  if (status.source.error !== null) {
    notifications.push({
      id: 'source-error',
      important: false,
      title: translate('NotificationSourceTitle'),
      text: translate('NotificationSourceText'),
    });
  }
  if (!status.runners.some((runner) => runner.connected)) {
    notifications.push({
      id: 'no-runner',
      important: false,
      title: translate('NotificationNoRunnerTitle'),
      text: translate('NotificationNoRunnerText'),
    });
  }
  for (const problem of status.code.problems) {
    notifications.push({
      id: `code-problem:${problem.repository}:${problem.reason}`,
      important: false,
      title: translate('NotificationCodeProblemTitle', { repository: problem.repository }),
      text: translate('NotificationCodeProblemText', { reason: problem.reason }),
    });
  }
  for (const warning of status.code.warnings) {
    notifications.push({
      id: `code-warning:${warning.subject}`,
      important: false,
      title: translate('NotificationCodeWarningTitle', { subject: warning.subject }),
      text: translate('NotificationCodeWarningText', { reason: warning.reason }),
    });
  }
  if (status.code.error !== null) {
    notifications.push({
      id: 'code-error',
      important: false,
      title: translate('NotificationCodeErrorTitle'),
      text: translate('NotificationCodeErrorText', { error: status.code.error }),
    });
  }
  return notifications;
}
