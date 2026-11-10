import { Link } from 'react-router-dom';

import { useSignOut } from 'Account/useAccountApi';
import useSession from 'Account/useSession';
import Button from 'Components/Button';
import translate from 'Utilities/translate';

import styles from './SignedInUser.module.css';

// Who is signed in, with a link to their account, and the way out.
export default function SignedInUser() {
  const { data } = useSession();
  const signOut = useSignOut();
  if (!data) {
    return null;
  }

  return (
    <div className={styles.user}>
      <Link to="/account">{translate('SignedInAs', { name: data.name, role: data.role })}</Link>
      <Button onClick={() => signOut.mutate()} disabled={signOut.isPending}>
        {translate('SignOut')}
      </Button>
    </div>
  );
}
