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
  if (!status.executors.some((executor) => executor.connected)) {
    notifications.push({
      id: 'no-executor',
      important: false,
      title: translate('NotificationNoExecutorTitle'),
      text: translate('NotificationNoExecutorText'),
    });
  }
  return notifications;
}
