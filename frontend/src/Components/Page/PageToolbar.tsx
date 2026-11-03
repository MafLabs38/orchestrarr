import type { ReactNode } from 'react';

import styles from './PageToolbar.module.css';

export default function PageToolbar({ children }: Readonly<{ children: ReactNode }>) {
  return <div className={styles.toolbar}>{children}</div>;
}
