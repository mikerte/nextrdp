import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  build: {
    // The single administration shell is 504 kB minified and 136 kB gzip; keep the
    // warning budget tight while allowing this measured initial bundle.
    chunkSizeWarningLimit: 525
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://127.0.0.1:5080',
      '/auth': 'http://127.0.0.1:5080',
      '/connect': 'http://127.0.0.1:5080',
      '/health': 'http://127.0.0.1:5080',
      '/openapi': 'http://127.0.0.1:5080'
    }
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: './src/test-setup.ts'
  }
});
