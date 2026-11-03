import formatTime from './formatTime';

describe('formatTime', () => {
  it('has no time to show when nothing was read', () => {
    expect(formatTime(null)).toBeNull();
  });

  it('shows the time of day of a date, to the second', () => {
    expect(formatTime('2026-11-01T08:15:30Z')).toMatch(/\d{1,2}.\d{2}.\d{2}/);
  });
});
