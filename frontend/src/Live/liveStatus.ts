// Whether the screens are told what changes as it changes (ADR 0025): the state of the WebSocket, for the badge of the header.
export type LiveState = 'connecting' | 'live' | 'reconnecting' | 'off';

let current: LiveState = 'connecting';
const listeners = new Set<() => void>();

export const liveStatus = {
  get: (): LiveState => current,
  set(next: LiveState) {
    if (next !== current) {
      current = next;
      listeners.forEach((listener) => listener());
    }
  },
  subscribe(listener: () => void): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
};
