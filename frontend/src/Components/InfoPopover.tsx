import { type ReactNode, useEffect, useRef, useState } from 'react';

import * as icons from 'Helpers/Props/icons';
import { iconSizes } from 'Helpers/Props/sizes';

import styles from './InfoPopover.module.css';

// A small (i) that opens a bubble with the long text, so that a row of a table stays one line. The bubble is fixed to the window, not to the
// table: a table that scrolls would cut it. It closes on Escape, on a click elsewhere, and when the page moves under it.
export default function InfoPopover({ label, children }: Readonly<{ label: string; children: ReactNode }>) {
  const [position, setPosition] = useState<{ top: number; left: number } | null>(null);
  const container = useRef<HTMLSpanElement>(null);
  const open = position !== null;

  useEffect(() => {
    if (!open) {
      return undefined;
    }
    const close = (event: Event) => {
      if (
        event instanceof KeyboardEvent ? event.key === 'Escape' : !container.current?.contains(event.target as Node)
      ) {
        setPosition(null);
      }
    };
    const closeNow = () => setPosition(null);
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', close);
    window.addEventListener('scroll', closeNow, true);
    window.addEventListener('resize', closeNow);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', close);
      window.removeEventListener('scroll', closeNow, true);
      window.removeEventListener('resize', closeNow);
    };
  }, [open]);

  function toggle() {
    if (open) {
      setPosition(null);
      return;
    }
    const box = container.current?.getBoundingClientRect();
    if (box) {
      setPosition({ top: box.bottom + 6, left: Math.max(8, Math.min(box.left, window.innerWidth - 320)) });
    }
  }

  return (
    <span className={styles.container} ref={container}>
      <button type="button" className={styles.button} aria-label={label} aria-expanded={open} onClick={toggle}>
        <icons.INFO size={iconSizes.SMALL} aria-hidden="true" />
      </button>
      {position ? (
        <span className={styles.bubble} role="note" style={{ top: position.top, left: position.left }}>
          {children}
        </span>
      ) : null}
    </span>
  );
}
