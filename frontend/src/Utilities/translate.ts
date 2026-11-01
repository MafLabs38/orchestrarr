import en from './en.json';

type Key = keyof typeof en;

// English is the source language. The keys will move to the backend (as in the *arr apps) when there are two languages.
export default function translate(key: Key, tokens: Record<string, string | number> = {}): string {
  const template: string = en[key];
  return template.replace(/\{(\w+)\}/g, (match, token: string) => (token in tokens ? String(tokens[token]) : match));
}
