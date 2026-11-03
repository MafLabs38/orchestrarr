import classNames from 'classnames';
import type { ReactNode } from 'react';

import { type Kind, kinds } from 'Helpers/Props/kinds';

import styles from './Alert.module.css';

export default function Alert({ kind = kinds.INFO, children }: Readonly<{ kind?: Kind; children: ReactNode }>) {
  const className = classNames(styles.alert, styles[kind]);
  return kind === kinds.DANGER ? (
    <div role="alert" className={className}>
      {children}
    </div>
  ) : (
    <output className={className}>{children}</output>
  );
}
