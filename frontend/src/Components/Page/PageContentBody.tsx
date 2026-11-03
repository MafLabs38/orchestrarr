import type { ReactNode } from 'react';

import styles from './PageContentBody.module.css';

export default function PageContentBody({ children }: { children: ReactNode }) {
  return <div className={styles.body}>{children}</div>;
}
