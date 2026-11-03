import Button from 'Components/Button';
import { signOutPath } from 'Helpers/signIn';
import translate from 'Utilities/translate';

import styles from './SignedInUser.module.css';
import useSession from './useSession';

// Who is signed in, and the way out, for any session, including one the API refuses. Nothing where people do not sign in: the sandbox
// operator has nothing to leave. Signing out is a form, a POST that the browser navigates with: no link or image can do it.
export default function SignedInUser() {
  const { data } = useSession();
  if (!data) {
    return null;
  }

  return (
    <form method="post" action={signOutPath} className={styles.user}>
      <span>{translate('SignedInAs', { name: data.name ?? '' })}</span>
      <Button type="submit">{translate('SignOut')}</Button>
    </form>
  );
}
