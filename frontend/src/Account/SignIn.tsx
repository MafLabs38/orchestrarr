import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert';
import BrandLogo from 'Components/BrandLogo';
import Button from 'Components/Button';
import form from 'Components/Form/Form.module.css';
import { ApiRefusal } from 'Helpers/Hooks/sendJson';
import { kinds } from 'Helpers/Props/kinds';
import translate from 'Utilities/translate';

import styles from './SignIn.module.css';
import { useSignIn } from './useAccountApi';

function failureText(error: unknown): string {
  const code = error instanceof ApiRefusal ? error.code : null;
  if (code === 'invalidCredentials') {
    return translate('SignInWrongCredentials');
  }
  if (code === 'locked' || code === 'busy') {
    return translate('SignInLocked');
  }
  return translate('SignInFailed');
}

// What the person sees when nobody is signed in: Orchestrarr has its own accounts, like the other products of the constellation.
export default function SignIn() {
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const signIn = useSignIn();

  function submit(event: FormEvent) {
    event.preventDefault();
    signIn.mutate({ name: name.trim(), password });
  }

  return (
    <main className={styles.page}>
      <form className={styles.card} onSubmit={submit}>
        <div className={styles.brand}>
          <BrandLogo size={48} />
          <h1>{translate('AppName')}</h1>
        </div>
        <label className={form.field}>
          <span className={form.label}>{translate('AccountName')}</span>
          <input
            className={form.input}
            value={name}
            onChange={(event) => setName(event.target.value)}
            autoComplete="username"
            required
          />
        </label>
        <label className={form.field}>
          <span className={form.label}>{translate('Password')}</span>
          <input
            className={form.input}
            type="password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            autoComplete="current-password"
            required
          />
        </label>
        {signIn.isError ? <Alert kind={kinds.DANGER}>{failureText(signIn.error)}</Alert> : null}
        <Button type="submit" kind={kinds.PRIMARY} disabled={signIn.isPending || !name.trim() || !password}>
          {translate('SignIn')}
        </Button>
      </form>
    </main>
  );
}
