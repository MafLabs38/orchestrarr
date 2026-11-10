import { type FormEvent, useState } from 'react';

import useSession, { can, optionsManage } from 'Account/useSession';
import Alert from 'Components/Alert';
import Button from 'Components/Button';
import form from 'Components/Form/Form.module.css';
import Label from 'Components/Label';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { ApiRefusal } from 'Helpers/Hooks/sendJson';
import { kinds } from 'Helpers/Props/kinds';
import type { OptionState } from 'typings/api';
import translate from 'Utilities/translate';

import styles from './Options.module.css';
import { useOptions, useSetOption } from './useOptions';

// The words of each option; one the interface does not know is shown under its identifier.
const labels: Record<string, { title: Parameters<typeof translate>[0]; description: Parameters<typeof translate>[0] }> =
  {
    'approvals.require-second-factor': {
      title: 'OptionSecondFactorTitle',
      description: 'OptionSecondFactorDescription',
    },
  };

function failureText(error: unknown): string {
  const code = error instanceof ApiRefusal ? error.code : null;
  switch (code) {
    case 'invalidCredentials':
      return translate('OptionPasswordWrong');
    case 'passwordRequired':
      return translate('OptionPasswordRequired');
    case 'lockedByHost':
      return translate('OptionLockedHost');
    case 'setByConfiguration':
      return translate('OptionLockedConfiguration');
    default:
      return translate('OptionChangeFailed');
  }
}

function lockedText(option: OptionState): string | null {
  if (option.lockedReason === 'host') {
    return translate('OptionLockedHost');
  }
  return option.lockedReason === 'configuration' ? translate('OptionLockedConfiguration') : null;
}

function OptionCard({ option, canManage }: Readonly<{ option: OptionState; canManage: boolean }>) {
  const change = useSetOption();
  const [confirming, setConfirming] = useState(false);
  const [password, setPassword] = useState('');
  const words = labels[option.id];
  const locked = lockedText(option);

  function turnOff(event: FormEvent) {
    event.preventDefault();
    change.mutate(
      { id: option.id, value: false, password },
      {
        onSuccess: () => {
          setConfirming(false);
          setPassword('');
        },
      },
    );
  }

  return (
    <li className={styles.option}>
      <div className={styles.head}>
        <h2>{words ? translate(words.title) : option.id}</h2>
        <Label kind={option.value ? kinds.SUCCESS : kinds.WARNING}>
          {option.value ? translate('On') : translate('Off')}
        </Label>
      </div>
      {words ? <p className={styles.description}>{translate(words.description)}</p> : null}
      {locked ? <p className={styles.note}>{locked}</p> : null}
      {option.value && option.lowersSecurityWhenOff && option.canTurnOff && canManage && !confirming ? (
        <div className={styles.actions}>
          <Button onClick={() => setConfirming(true)}>{translate('OptionTurnOff')}</Button>
        </div>
      ) : null}
      {!option.value && !locked && canManage ? (
        <div className={styles.actions}>
          <Button
            kind={kinds.PRIMARY}
            disabled={change.isPending}
            onClick={() => change.mutate({ id: option.id, value: true })}
          >
            {translate('OptionTurnOn')}
          </Button>
        </div>
      ) : null}
      {confirming ? (
        <form className={styles.confirm} onSubmit={turnOff}>
          <Alert kind={kinds.WARNING}>{translate('OptionTurnOffWarning')}</Alert>
          <label className={form.field}>
            <span className={form.label}>{translate('OptionYourPassword')}</span>
            <input
              className={form.input}
              type="password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              autoComplete="current-password"
              required
            />
          </label>
          <div className={styles.actions}>
            <Button type="submit" kind={kinds.DANGER} disabled={!password || change.isPending}>
              {translate('OptionTurnOffConfirm')}
            </Button>
            <Button
              onClick={() => {
                setConfirming(false);
                setPassword('');
                change.reset();
              }}
            >
              {translate('Cancel')}
            </Button>
          </div>
        </form>
      ) : null}
      {change.isError ? <Alert kind={kinds.DANGER}>{failureText(change.error)}</Alert> : null}
    </li>
  );
}

// The switches of the product that are meant for development and tests. Everybody reads them; an administrator changes them, and what lowers security
// needs the host to allow it and the administrator's password (ADR 0023).
export default function Options() {
  const { data: session } = useSession();
  const { data: options, isError } = useOptions();

  return (
    <PageContent title={translate('Options')}>
      <PageContentBody>
        <PageHeading
          scope={translate('System')}
          title={translate('Options')}
          description={translate('OptionsDescription')}
        />
        {isError ? <Alert kind={kinds.DANGER}>{translate('OptionsNotRead')}</Alert> : null}
        {options ? (
          <ul className={styles.list}>
            {options.map((option) => (
              <OptionCard key={option.id} option={option} canManage={can(session, optionsManage)} />
            ))}
          </ul>
        ) : null}
      </PageContentBody>
    </PageContent>
  );
}
