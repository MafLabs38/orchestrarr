import Button from 'Components/Button';
import { signOutPath } from 'Helpers/signIn';
import useSystemStatus from 'System/useSystemStatus';
import translate from 'Utilities/translate';

import styles from './SignedInUser.module.css';

// Who is signed in, and the way out. Only when people sign in through the identity provider: the sandbox operator has nothing to leave.
// Signing out is a form, a POST that the browser navigates with: no link or image can do it.
export default function SignedInUser() {
  const { data } = useSystemStatus();
  if (data?.approvals.mode !== 'openidconnect' || !data.approvals.operator) {
    return null;
  }

  return (
    <form method="post" action={signOutPath} className={styles.user}>
      <span>{translate('SignedInAs', { name: data.approvals.operatorName ?? data.approvals.operator })}</span>
      <Button type="submit">{translate('SignOut')}</Button>
    </form>
  );
}
