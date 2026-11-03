import classNames from 'classnames';
import type { ReactNode } from 'react';

import { type Kind, kinds } from 'Helpers/Props/kinds';

import styles from './Card.module.css';

interface CardProps {
  kind?: Kind;
  children: ReactNode;
  'aria-labelledby'?: string;
}

export default function Card({ kind = kinds.DEFAULT, children, ...rest }: Readonly<CardProps>) {
  return (
    <section className={classNames(styles.card, styles[kind])} {...rest}>
      {children}
    </section>
  );
}
