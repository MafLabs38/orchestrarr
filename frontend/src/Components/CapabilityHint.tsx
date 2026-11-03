import type { ReactNode } from 'react';

import styles from './CapabilityHint.module.css';

// What is not available, and why: never a silent gap.
export default function CapabilityHint({ id, children }: Readonly<{ id?: string; children: ReactNode }>) {
  return (
    <p id={id} className={styles.hint}>
      {children}
    </p>
  );
}
