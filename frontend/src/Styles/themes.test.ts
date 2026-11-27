import { readFileSync } from 'node:fs';
import { join } from 'node:path';

// The dark theme is the default and defines every colour token; the light theme overrides some of them and inherits the rest. A token defined in the light theme only would leave a state told by its colour with no colour at all
// in the default one (DESIGN §5)
function tokensOf(block: string): Set<string> {
  return new Set(
    block
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.startsWith('--'))
      .map((line) => line.slice(0, line.indexOf(':'))),
  );
}

describe('themes', () => {
  it('defines in the default theme every token that the light theme sets', () => {
    const css = readFileSync(join(process.cwd(), 'src', 'Styles', 'themes.css'), 'utf8');
    const [dark, light] = css.split("[data-theme='light']");
    const defaults = tokensOf(dark);

    expect([...tokensOf(light)].filter((token) => !defaults.has(token))).toEqual([]);
    expect(defaults.has('--purple') && defaults.has('--pink')).toBe(true);
  });
});
