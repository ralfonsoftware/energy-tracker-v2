// Auto-applies the .dark class from the OS color-scheme preference. No manual
// toggle yet (product decision, not a DESIGN.md requirement) — DESIGN.md just
// treats Dark and Light as equal citizens, both fully designed and neither
// a fallback of the other.
// index.html carries a synchronous inline copy of the initial check, applied
// before first paint to avoid a flash of the wrong theme; this function is
// what keeps it live after mount.
export function initColorScheme(): void {
  const query = window.matchMedia('(prefers-color-scheme: dark)')

  const apply = () => {
    document.documentElement.classList.toggle('dark', query.matches)
  }

  apply()
  query.addEventListener('change', apply)
}
