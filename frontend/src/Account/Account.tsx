import { type FormEvent, useState } from 'react';

import Alert from 'Components/Alert';
import Button from 'Components/Button';
import form from 'Components/Form/Form.module.css';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { ApiRefusal } from 'Helpers/Hooks/sendJson';
import { kinds } from 'Helpers/Props/kinds';
import translate from 'Utilities/translate';

import styles from './Account.module.css';
import { useChangePassword } from './useAccountApi';
import useSession from './useSession';

function failureText(error: unknown): string {
  if (error instanceof ApiRefusal) {
    if (error.code === 'invalidCredentials') {
      return translate('CurrentPasswordWrong');
    }
    if (error.code === 'locked' || error.code === 'busy') {
      return translate('SignInLocked');
    }
    if (error.code === 'invalid') {
      return translate('PasswordRefused');
    }
  }
  return translate('PasswordNotChanged');
}

// The signed-in person: who they are, what their role allows, the identifier the authenticator's enrolment uses, and their own password.
export default function Account() {
  const { data } = useSession();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [again, setAgain] = useState('');
  const change = useChangePassword();
  if (!data) {
    return null;
  }

  const mismatch = again.length > 0 && next !== again;

  function submit(event: FormEvent) {
    event.preventDefault();
    change.mutate(
      { currentPassword: current, newPassword: next },
      {
        onSuccess: () => {
          setCurrent('');
          setNext('');
          setAgain('');
        },
      },
    );
  }

  return (
    <PageContent title={translate('Account')}>
      <PageContentBody>
        <PageHeading scope={translate('Account')} title={data.name} />
        <dl className={styles.list}>
          <dt>{translate('Role')}</dt>
          <dd>{data.role}</dd>
          <dt>{translate('Permissions')}</dt>
          <dd>{data.permissions.join(', ')}</dd>
          <dt>{translate('Identifier')}</dt>
          <dd className={styles.mono}>{data.subject}</dd>
        </dl>
        <p className={styles.hint}>{translate('IdentifierHint')}</p>
        <form className={styles.form} onSubmit={submit}>
          <h2>{translate('ChangePassword')}</h2>
          <label className={form.field}>
            <span className={form.label}>{translate('CurrentPassword')}</span>
            <input
              className={form.input}
              type="password"
              value={current}
              onChange={(e) => setCurrent(e.target.value)}
              autoComplete="current-password"
              required
            />
          </label>
          <label className={form.field}>
            <span className={form.label}>{translate('NewPassword')}</span>
            <input
              className={form.input}
              type="password"
              value={next}
              onChange={(e) => setNext(e.target.value)}
              autoComplete="new-password"
              required
            />
          </label>
          <label className={form.field}>
            <span className={form.label}>{translate('RepeatNewPassword')}</span>
            <input
              className={form.input}
              type="password"
              value={again}
              onChange={(e) => setAgain(e.target.value)}
              autoComplete="new-password"
              required
            />
          </label>
          {mismatch ? <Alert kind={kinds.WARNING}>{translate('PasswordsDiffer')}</Alert> : null}
          {change.isError ? <Alert kind={kinds.DANGER}>{failureText(change.error)}</Alert> : null}
          {change.isSuccess ? <Alert kind={kinds.SUCCESS}>{translate('PasswordChanged')}</Alert> : null}
          <Button
            type="submit"
            kind={kinds.PRIMARY}
            disabled={change.isPending || mismatch || !current || !next || !again}
          >
            {translate('ChangePassword')}
          </Button>
        </form>
      </PageContentBody>
    </PageContent>
  );
}
