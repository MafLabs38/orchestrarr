import { NavLink } from 'react-router-dom';

import useApiQuery from 'Helpers/Hooks/useApiQuery';
import * as icons from 'Helpers/Props/icons';
import type { WorkItemReport } from 'typings/api';
import translate from 'Utilities/translate';

import styles from './PageSidebar.module.css';

export default function PageSidebar() {
  const { data } = useApiQuery<WorkItemReport>('/api/v1/work-items', { refetchInterval: 30_000 });
  return (
    <nav className={styles.sidebar} aria-label="Main">
      <NavLink to="/" end className={({ isActive }) => (isActive ? `${styles.link} ${styles.active}` : styles.link)}>
        <icons.DECISIONS size={18} aria-hidden="true" />
        <span className={styles.text}>{translate('Decisions')}</span>
        {data && data.items.length > 0 ? <span className={styles.badge}>{data.items.length}</span> : null}
      </NavLink>
      <div className={styles.group}>{translate('System')}</div>
      <NavLink
        to="/system/status"
        className={({ isActive }) => (isActive ? `${styles.link} ${styles.active}` : styles.link)}
      >
        <icons.STATUS size={18} aria-hidden="true" />
        <span className={styles.text}>{translate('Status')}</span>
      </NavLink>
    </nav>
  );
}
