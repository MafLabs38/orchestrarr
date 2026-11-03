import classNames from 'classnames';
import type { ReactNode } from 'react';

import { type Kind, kinds } from 'Helpers/Props/kinds';

import styles from './Label.module.css';

interface LabelProps {
  kind?: Kind;
  children: ReactNode;
}

export default function Label({ kind = kinds.DEFAULT, children }: Readonly<LabelProps>) {
  return <span className={classNames(styles.label, styles[kind])}>{children}</span>;
}
