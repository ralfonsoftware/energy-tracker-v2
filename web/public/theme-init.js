// Applied synchronously, before CSS paints, to avoid a flash of the wrong
// theme. Mirrors getThemePreference() in src/lib/color-scheme.ts — keep the
// storage key and values ('light' | 'dark'; anything else = System) in sync
// with it. The theme-color literals are the same as THEME_COLOR_* there.
// src/lib/color-scheme.ts takes over once the app has mounted.
// This is an external file, not an inline <script> in index.html, because the
// production CSP is script-src 'self' (AD-27). The 'public/theme-init.js
// mirrors the theme preference logic' tests in src/lib/color-scheme.test.ts
// run this file and fail if it drifts from color-scheme.ts.
try {
  var stored = null
  try {
    stored = window.localStorage.getItem('energy-tracker-theme')
  } catch {}
  var dark =
    stored === 'dark' ||
    (stored !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches)
  document.documentElement.classList.toggle('dark', dark)
  document
    .querySelector('meta[name="theme-color"]')
    .setAttribute('content', dark ? '#12201A' : '#F3F8ED')
} catch {}
