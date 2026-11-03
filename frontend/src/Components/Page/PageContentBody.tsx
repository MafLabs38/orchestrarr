import type { ReactNode } from 'react';

import styles from './PageContentBody.module.css';

export default function PageContentBody({ children }: Readonly<{ children: ReactNode }>) {
  return <div className={styles.body}>{children}</div>;
}
