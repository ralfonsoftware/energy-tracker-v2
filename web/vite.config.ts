import fs from 'node:fs'
import path from 'node:path'
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// The OIDC correlation/nonce cookies (and the session cookie) are all SecurePolicy=Always — the
// browser only ever stores/sends a Secure cookie back over a connection IT considers TLS, no
// matter what scheme the proxied backend uses behind the scenes. Chrome grants http://localhost
// an exception and stores them anyway; Safari (and production, correctly, behind real TLS) does
// not. Serving this dev server itself over HTTPS — using the exported/trusted `dotnet dev-certs`
// cert — is what makes Safari accept these cookies locally, matching production instead of
// relying on Chrome's leniency (docs/local-development.md, "Testing sign-in in Safari").
// Opt-in: falls back to plain HTTP (unaffected, unchanged) when the cert files haven't been
// generated, so this costs nothing for anyone not testing Safari/cookie-strict browsers locally.
const viteCertPath = path.resolve(import.meta.dirname, '../certs/vite-dev-cert.pem')
const viteKeyPath = path.resolve(import.meta.dirname, '../certs/vite-dev-cert.key')
const viteHttps =
  fs.existsSync(viteCertPath) && fs.existsSync(viteKeyPath)
    ? { cert: fs.readFileSync(viteCertPath), key: fs.readFileSync(viteKeyPath) }
    : undefined

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
    https: viteHttps,
    // Forwards API calls to the local `dotnet run` instance (see docs/local-development.md)
    // so the dev server and the API can be debugged as two separate processes without CORS issues.
    proxy: {
      '/health': 'http://localhost:5133',
      '/api': 'http://localhost:5133',
      // Full-page OIDC navigations (App.tsx's window.location.href = '/login'), not fetch calls —
      // without these, they stay on :5173 and hit Vite's SPA fallback instead of the API,
      // silently looping back through the unauthenticated state instead of reaching the provider.
      // These four specifically target the API's https://localhost:7005 (run-api.sh always binds
      // it via the "https" launch profile), not its plain-http port. The API computes its OIDC
      // redirect_uri from this request's scheme + the ORIGINAL Host header (object-form proxy
      // config, unlike the string shorthand above, does NOT rewrite Host by default) — so
      // targeting the https port here makes redirect_uri come out
      // https://localhost:5173/signin-oidc: correct scheme (satisfies Safari's Secure-cookie
      // requirement on Auth0's direct callback POST, which bypasses this proxy) AND still the
      // SPA's own origin (so the post-login redirect lands back on the app, not a bare API port).
      // Must be added as an Allowed Callback/Logout URL in Auth0 (docs/local-development.md,
      // "Testing sign-in in Safari"). `secure: false` skips TLS verification for this one
      // internal Vite-to-API hop — Node doesn't trust the OS-keychain-trusted dotnet dev cert.
      '/login': { target: 'https://localhost:7005', secure: false },
      '/logout': { target: 'https://localhost:7005', secure: false },
      '/signin-oidc': { target: 'https://localhost:7005', secure: false },
      '/signout-callback-oidc': { target: 'https://localhost:7005', secure: false },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    globals: true,
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
  },
})
