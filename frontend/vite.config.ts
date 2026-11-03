import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

const src = (folder: string) => fileURLToPath(new URL(`./src/${folder}`, import.meta.url));

// The API is the .NET host: in development the Vite server proxies /api to it.
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      App: src('App'),
      Components: src('Components'),
      Decisions: src('Decisions'),
      Helpers: src('Helpers'),
      System: src('System'),
      Utilities: src('Utilities'),
      typings: src('typings'),
      testUtils: src('testUtils.tsx'),
    },
  },
  server: {
    proxy: { '/api': { target: 'http://127.0.0.1:7878', changeOrigin: false } },
  },
  build: {
    outDir: '../src/Orchestrarr.Host/wwwroot',
    emptyOutDir: true,
  },
  test: {
    environment: 'happy-dom',
    globals: true,
    setupFiles: ['./src/setupTests.ts'],
    css: { modules: { classNameStrategy: 'non-scoped' } },
    coverage: {
      provider: 'v8',
      reporter: ['text-summary', 'lcov'],
      reportsDirectory: 'coverage',
      include: ['src/**/*.{ts,tsx}'],
      exclude: ['src/**/*.test.{ts,tsx}', 'src/setupTests.ts', 'src/testUtils.tsx', 'src/main.tsx', 'src/typings/**'],
      // The threshold of docs/TESTING.md: the shared components and the screens
      thresholds: { lines: 70, statements: 70 },
    },
  },
});
