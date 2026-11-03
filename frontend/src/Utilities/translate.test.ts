import translate from './translate';

describe('translate', () => {
  it('returns the English text of a key', () => {
    expect(translate('Refresh')).toBe('Refresh');
  });

  it('fills the tokens of a text, each occurrence', () => {
    expect(translate('StepsTicked', { ticked: 2, total: 3 })).toBe('2 of 3 steps ticked');
  });

  it('leaves a token it was not given, visible rather than silent', () => {
    expect(translate('StepsTicked', { ticked: 2 })).toBe('2 of {total} steps ticked');
  });
});
