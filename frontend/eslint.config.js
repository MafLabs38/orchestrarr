import js from '@eslint/js';
import jsxA11y from 'eslint-plugin-jsx-a11y';
import reactHooks from 'eslint-plugin-react-hooks';
import simpleImportSort from 'eslint-plugin-simple-import-sort';
import sonarjs from 'eslint-plugin-sonarjs';
import unicorn from 'eslint-plugin-unicorn';
import globals from 'globals';
import tseslint from 'typescript-eslint';

// The same rules as Portarr's frontend: the SonarQube profile runs in the editor and in `yarn lint`.
export default tseslint.config(
  { ignores: ['coverage/**', 'dist/**', '../src/Orchestrarr.Host/wwwroot/**'] },
  js.configs.recommended,
  ...tseslint.configs.strict,
  sonarjs.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      globals: { ...globals.browser, ...globals.node },
    },
    plugins: {
      'jsx-a11y': jsxA11y,
      'react-hooks': reactHooks,
      'simple-import-sort': simpleImportSort,
      unicorn,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'unicorn/no-negated-condition': 'error', // S7735
      'unicorn/no-useless-spread': 'error', // S7747
      'unicorn/prefer-at': 'error', // S7755
      'unicorn/prefer-code-point': 'error', // S7758
      'unicorn/prefer-global-this': 'error', // S7764
      'unicorn/prefer-string-raw': 'error', // S7780
      'unicorn/prefer-string-replace-all': 'error', // S7781
      'unicorn/prefer-top-level-await': 'error', // S7785
      ...jsxA11y.flatConfigs.recommended.rules,
      'simple-import-sort/imports': [
        'error',
        // Side effects, packages, absolute imports from src, then relative imports.
        {
          groups: [
            [String.raw`^\u0000`],
            [String.raw`^@?\w`],
            ['^(App|Components|Decisions|Helpers|Styles|System|Utilities|testUtils|typings)(/|$)'],
            [String.raw`^\.`],
          ],
        },
      ],
      'simple-import-sort/exports': 'error',
      'no-restricted-syntax': [
        'error',
        {
          selector: 'ExportDefaultDeclaration > ArrowFunctionExpression',
          message: 'Use a named default export: export default function Name() (DESIGN.md §1.3).',
        },
      ],
    },
  },
  {
    files: ['**/*.test.{ts,tsx}'],
    rules: {
      'sonarjs/no-hardcoded-ip': 'off',
      'sonarjs/no-clear-text-protocols': 'off',
    },
  },
);
