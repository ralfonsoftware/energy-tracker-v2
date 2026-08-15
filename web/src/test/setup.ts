import '@testing-library/jest-dom/vitest'
import '@/i18n'
// jsdom has no native IndexedDB implementation. App.tsx now calls registerOfflineSync() (and
// therefore indexedDB.open) on mount whenever a Household session is ready, so every test that
// renders App needs this available globally, not just the offline-queue-specific test files.
import 'fake-indexeddb/auto'
