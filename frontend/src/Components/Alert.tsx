import classNames from 'classnames';
import type { ReactNode } from 'react';

import { type Kind, kinds } from 'Helpers/Props/kinds';

import styles from './Alert.module.css';

export default function Alert({ kind = kinds.INFO, children }: Readonly<{ kind?: Kind; children: ReactNode }>) {
  return (
    <div role={kind === kinds.DANGER ? 'alert' : 'status'} className={classNames(styles.alert, styles[kind])}>
      {children}
    </div>
  );
}
