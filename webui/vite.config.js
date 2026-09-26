import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  publicDir: 'public',
  server: {
    proxy: {
      '/api': { target: 'http://127.0.0.1:6769' },
      '/ws': { target: 'ws://127.0.0.1:6769', ws: true },
    },
  },
  build: {
    outDir: '../Web',
    emptyOutDir: true,
    sourcemap: false,
  },
});
