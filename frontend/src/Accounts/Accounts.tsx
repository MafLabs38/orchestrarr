import { type FormEvent, useState } from 'react';

import { useAccountChanges, useAccounts, useRoles } from 'Account/useAccountApi';
import useSession from 'Account/useSession';
import Alert from 'Components/Alert';
import Button from 'Components/Button';
import form from 'Components/Form/Form.module.css';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { ApiRefusal } from 'Helpers/Hooks/sendJson';
import { kinds } from 'Helpers/Props/kinds';
import type { Account, RoleName } from 'typings/api';
import translate from 'Utilities/translate';

import styles from './Accounts.module.css';

function failureText(error: unknown): string {
  const code = error instanceof ApiRefusal ? error.code : null;
  if (code === 'conflict') {
    return translate('AccountConflict');
  }
  if (code === 'invalid') {
    return translate('AccountInvalid');
  }
  return translate('AccountChangeFailed');
}

function AccountRow({ account, isMe }: Readonly<{ account: Account; isMe: boolean }>) {
  const { data: roles } = useRoles();
  const changes = useAccountChanges();
  const [password, setPassword] = useState('');
  const error = changes.update.error ?? changes.remove.error ?? changes.setPassword.error;

  function newPassword(event: FormEvent) {
    event.preventDefault();
    changes.setPassword.mutate({ id: account.id, password }, { onSuccess: () => setPassword('') });
  }

  return (
    <>
      <tr>
        <td>
          {account.name}
          {isMe ? <span className={styles.me}> {translate('You')}</span> : null}
        </td>
        <td>
          <select
            className={form.input}
            aria-label={translate('RoleOf', { name: account.name })}
            value={account.role}
            onChange={(event) =>
              changes.update.mutate({ id: account.id, role: event.target.value, enabled: account.enabled })
            }
          >
            {(roles ?? []).map((role) => (
              <option key={role.name} value={role.name}>
                {role.name}
              </option>
            ))}
          </select>
        </td>
        <td>
          <input
            className={form.input}
            type="checkbox"
            aria-label={translate('EnabledOf', { name: account.name })}
            checked={account.enabled}
            onChange={(event) =>
              changes.update.mutate({ id: account.id, role: account.role, enabled: event.target.checked })
            }
          />
        </td>
        <td>
          <form className={styles.inline} onSubmit={newPassword}>
            <input
              className={form.input}
              type="password"
              aria-label={translate('NewPasswordOf', { name: account.name })}
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              autoComplete="new-password"
            />
            <Button type="submit" disabled={!password || changes.setPassword.isPending}>
              {translate('SetPassword')}
            </Button>
          </form>
        </td>
        <td>
          <Button
            kind={kinds.DANGER}
            onClick={() => {
              if (globalThis.confirm(translate('DeleteAccountConfirm', { name: account.name }))) {
                changes.remove.mutate(account.id);
              }
            }}
          >
            {translate('Delete')}
          </Button>
        </td>
      </tr>
      {error ? (
        <tr>
          <td colSpan={5}>
            <Alert kind={kinds.DANGER}>{failureText(error)}</Alert>
          </td>
        </tr>
      ) : null}
    </>
  );
}

function NewAccount() {
  const { data: roles } = useRoles();
  const { create } = useAccountChanges();
  const [name, setName] = useState('');
  const [role, setRole] = useState<RoleName>('Reader');
  const [password, setPassword] = useState('');

  function submit(event: FormEvent) {
    event.preventDefault();
    create.mutate(
      { name: name.trim(), role, password },
      {
        onSuccess: () => {
          setName('');
          setPassword('');
        },
      },
    );
  }

  return (
    <form className={styles.new} onSubmit={submit}>
      <h2>{translate('NewAccount')}</h2>
      <label className={form.field}>
        <span className={form.label}>{translate('AccountName')}</span>
        <input
          className={form.input}
          value={name}
          onChange={(event) => setName(event.target.value)}
          autoComplete="off"
          required
        />
      </label>
      <label className={form.field}>
        <span className={form.label}>{translate('Role')}</span>
        <select className={form.input} value={role} onChange={(event) => setRole(event.target.value as RoleName)}>
          {(roles ?? []).map((candidate) => (
            <option key={candidate.name} value={candidate.name} title={candidate.description}>
              {candidate.name}
            </option>
          ))}
        </select>
      </label>
      <label className={form.field}>
        <span className={form.label}>{translate('Password')}</span>
        <input
          className={form.input}
          type="password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
          autoComplete="new-password"
          required
        />
      </label>
      {create.isError ? <Alert kind={kinds.DANGER}>{failureText(create.error)}</Alert> : null}
      <Button type="submit" kind={kinds.PRIMARY} disabled={create.isPending || !name.trim() || !password}>
        {translate('CreateAccount')}
      </Button>
    </form>
  );
}

// Who can sign in, with which role, and whether they may: for the administrator (accounts.manage).
export default function Accounts() {
  const { data: session } = useSession();
  const { data: accounts, isError } = useAccounts();

  return (
    <PageContent title={translate('Accounts')}>
      <PageContentBody>
        <PageHeading
          scope={translate('System')}
          title={translate('Accounts')}
          description={translate('AccountsDescription')}
        />
        {isError ? <Alert kind={kinds.DANGER}>{translate('AccountsNotAllowed')}</Alert> : null}
        {accounts ? (
          <table className={styles.table}>
            <thead>
              <tr>
                <th>{translate('AccountName')}</th>
                <th>{translate('Role')}</th>
                <th>{translate('Enabled')}</th>
                <th>{translate('Password')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {accounts.map((account) => (
                <AccountRow key={account.id} account={account} isMe={account.name === session?.name} />
              ))}
            </tbody>
          </table>
        ) : null}
        <NewAccount />
      </PageContentBody>
    </PageContent>
  );
}
