import classNames from 'classnames';
import type { ReactNode } from 'react';
import { NavLink } from 'react-router-dom';

import styles from './SidebarLink.module.css';

interface SidebarLinkProps {
  to: string;
  end?: boolean;
  icon: ReactNode;
  label: string;
  badge?: number;
}

// The text stays in the page, hidden visually on a narrow screen, so that the link keeps its accessible name.
export default function SidebarLink({ to, end, icon, label, badge }: Readonly<SidebarLinkProps>) {
  return (
    <NavLink to={to} end={end} className={({ isActive }) => classNames(styles.link, isActive && styles.active)}>
      {icon}
      <span className={styles.text}>{label}</span>
      {badge ? <span className={styles.badge}>{badge}</span> : null}
    </NavLink>
  );
}
