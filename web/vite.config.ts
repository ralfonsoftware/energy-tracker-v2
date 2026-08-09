import path from 'node:path'
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, './src'),
    },
  },
  build: {
    outDir: path.resolve(import.meta.dirname, '../src/EnergyTracker.Api/wwwroot'),
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    // Forwards API calls to the local `dotnet run` instance (see docs/local-development.md)
    // so the dev server and the API can be debugged as two separate processes without CORS issues.
    proxy: {
      '/health': 'http://localhost:5133',
      '/api': 'http://localhost:5133',
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    globals: true,
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
  },
})
