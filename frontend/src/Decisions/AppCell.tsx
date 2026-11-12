import { useState } from 'react';

import styles from './AppCell.module.css';

// The colour of an application is its name's: the same name is always the same colour, so that the eye finds its rows when the table is sorted
const tones = ['--accent', '--secondary', '--infoColor', '--purple', '--pink', '--successColor', '--warningColor'];

function toneOf(name: string): string {
  let hash = 0;
  for (const character of name) {
    hash = (hash * 31 + (character.codePointAt(0) ?? 0)) % 997;
  }
  return tones[hash % tones.length] ?? '--accent';
}

// The repository without its owner, and two letters to recognise it: a compact cell, the full name when the pointer rests on it
export function shortName(application: string): string {
  return application.includes('/') ? application.slice(application.indexOf('/') + 1) : application;
}

export function initialsOf(application: string): string {
  const words = shortName(application)
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean);
  const letters = words.length > 1 ? `${words[0]?.[0] ?? ''}${words[1]?.[0] ?? ''}` : (words[0] ?? '?').slice(0, 2);
  return letters.toUpperCase();
}

export const iconPath = '/api/v1/icons';

// The icon of the application when the source has one (it is relayed by the host); the two letters when it has none or the picture cannot be read
export default function AppCell({
  application,
  icon,
  source,
}: Readonly<{ application: string; icon?: string | null; source?: string | null }>) {
  const [broken, setBroken] = useState(false);
  return (
    <span className={styles.cell} title={source && source !== application ? `${application} · ${source}` : application}>
      {icon && !broken ? (
        <img
          className={styles.icon}
          src={`${iconPath}/${encodeURIComponent(icon)}`}
          alt=""
          width={22}
          height={22}
          loading="lazy"
          onError={() => setBroken(true)}
        />
      ) : (
        <span className={styles.avatar} style={{ background: `var(${toneOf(application)})` }} aria-hidden="true">
          {initialsOf(application)}
        </span>
      )}
      <span className={styles.name}>{shortName(application)}</span>
    </span>
  );
}
