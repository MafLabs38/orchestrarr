import classNames from 'classnames';
import type { ReactNode } from 'react';
import { kinds, type Kind } from 'Helpers/Props/kinds';
import styles from './Label.module.css';

interface LabelProps {
  kind?: Kind;
  children: ReactNode;
}

export default function Label({ kind = kinds.DEFAULT, children }: LabelProps) {
  return <span className={classNames(styles.label, styles[kind])}>{children}</span>;
}
