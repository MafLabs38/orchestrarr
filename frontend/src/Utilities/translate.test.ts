import translate from './translate';

describe('translate', () => {
  it('fills the tokens of a text', () => {
    expect(translate('StepsTicked', { ticked: 2, total: 3 })).toBe('2 of 3 steps ticked');
  });

  it('fills a token that appears twice, each occurrence', () => {
    expect(translate('ShowingLastRead', { time: '10:00', error: 'x' })).toContain('10:00');
    expect(translate('FindingOutdatedImage', { reference: 'app', move: 'a → b', change: '' })).toBe('app: a → b');
  });

  it('leaves a token it was not given, visible rather than silent', () => {
    expect(translate('StepsTicked', { ticked: 2 })).toBe('2 of {total} steps ticked');
  });
});
