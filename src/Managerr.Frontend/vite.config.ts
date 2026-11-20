/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    outDir: '../Managerr.Host/wwwroot',
    emptyOutDir: true,
    sourcemap: false,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes('node_modules')) {
            if (
              id.includes('/react/') ||
              id.includes('/react-dom/') ||
              id.includes('/react-router/') ||
              id.includes('/scheduler/') ||
              id.includes('/cookie/') ||
              id.includes('/set-cookie-parser/')
            ) {
              return 'vendor-react';
            }
            if (id.includes('/@tanstack/')) {
              return 'vendor-query';
            }
            if (id.includes('/lucide-react/')) {
              return 'vendor-icons';
            }
          }
          return undefined;
        },
      },
    },
  },
  test: {
    environment: 'jsdom',
    globals: false,
    setupFiles: ['./src/__tests__/setup.ts'],
    include: ['src/__tests__/**/*.test.{ts,tsx}'],
    css: false,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:5080',
        changeOrigin: true,
        ws: true,
      },
      '/compat': {
        target: 'http://127.0.0.1:5080',
        changeOrigin: true,
      },
    },
  },
});
