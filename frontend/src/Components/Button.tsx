import classNames from 'classnames';
import type { ButtonHTMLAttributes } from 'react';

import { type Kind, kinds } from 'Helpers/Props/kinds';

import styles from './Button.module.css';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  kind?: Kind;
}

export default function Button({ kind = kinds.DEFAULT, className, type = 'button', ...rest }: Readonly<ButtonProps>) {
  return <button type={type} className={classNames(styles.button, styles[kind], className)} {...rest} />;
}
