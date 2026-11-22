import translate from './translate';

export default function formatTime(iso: string | null): string | null {
  if (!iso) {
    return null;
  }
  return new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

// A length of time in the largest units that say it: seconds, minutes, or hours and minutes
export function formatDuration(totalSeconds: number): string {
  const seconds = Math.max(0, Math.round(totalSeconds));
  if (seconds < 60) {
    return translate('DurationSeconds', { n: seconds });
  }
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return translate('DurationMinutes', { n: minutes });
  }
  return translate('DurationHours', { n: Math.floor(minutes / 60), m: minutes % 60 });
}
