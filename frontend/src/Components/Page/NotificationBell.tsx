import { useEffect, useRef, useState } from 'react';

import * as icons from 'Helpers/Props/icons';
import { iconSizes } from 'Helpers/Props/sizes';
import translate from 'Utilities/translate';

import styles from './NotificationBell.module.css';
import useNotifications from './useNotifications';

// A bell in the header instead of a banner on every screen: what needs attention is counted, and read when asked for. An important one (it lowers
// security or stops the work) is marked with an exclamation mark, and the bell says so before it is opened.
export default function NotificationBell() {
  const notifications = useNotifications();
  const [open, setOpen] = useState(false);
  const container = useRef<HTMLDivElement>(null);
  const important = notifications.filter((notification) => notification.important).length;

  useEffect(() => {
    if (!open) {
      return undefined;
    }
    const close = (event: MouseEvent | KeyboardEvent) => {
      if (
        event instanceof KeyboardEvent ? event.key === 'Escape' : !container.current?.contains(event.target as Node)
      ) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', close);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', close);
    };
  }, [open]);

  return (
    <div className={styles.container} ref={container}>
      <button
        type="button"
        className={styles.bell}
        aria-expanded={open}
        aria-haspopup="true"
        aria-label={translate('NotificationsLabel', { count: notifications.length })}
        onClick={() => setOpen((current) => !current)}
      >
        <icons.BELL size={iconSizes.MEDIUM} />
        {notifications.length > 0 ? (
          <span className={important > 0 ? styles.countImportant : styles.count}>
            {important > 0 ? '!' : notifications.length}
          </span>
        ) : null}
      </button>
      {open ? (
        <div className={styles.panel} role="region" aria-label={translate('NotificationsTitle')}>
          <strong className={styles.heading}>{translate('NotificationsTitle')}</strong>
          {notifications.length === 0 ? (
            <p className={styles.empty}>{translate('NotificationsNone')}</p>
          ) : (
            <ul className={styles.list}>
              {notifications.map((notification) => (
                <li key={notification.id} className={styles.item}>
                  {notification.important ? (
                    <icons.DANGER
                      size={iconSizes.SMALL}
                      className={styles.important}
                      aria-label={translate('NotificationImportant')}
                    />
                  ) : (
                    <icons.WARNING size={iconSizes.SMALL} className={styles.warning} aria-hidden="true" />
                  )}
                  <div>
                    <strong>{notification.title}</strong>
                    <p>{notification.text}</p>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </div>
      ) : null}
    </div>
  );
}
