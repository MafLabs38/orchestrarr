import Alert from 'Components/Alert';
import { kinds } from 'Helpers/Props/kinds';
import { signInFailed, signInPath } from 'Helpers/signIn';
import translate from 'Utilities/translate';

// The server sent the browser back here because signing in failed: say so, and offer to try again.
export default function SignInFailed() {
  if (!signInFailed()) {
    return null;
  }

  return (
    <Alert kind={kinds.DANGER}>
      {translate('SignInFailed')} <a href={signInPath}>{translate('SignInAgain')}</a>
    </Alert>
  );
}
