import { useEffect, useState } from 'react';

// The current time as a value of the screen, read again every `everyMs`: what says "for 5 min" keeps counting without a read of the host
export default function useNow(everyMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), everyMs);
    return () => clearInterval(timer);
  }, [everyMs]);
  return now;
}
