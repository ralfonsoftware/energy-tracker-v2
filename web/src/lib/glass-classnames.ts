// Shared glass-panel classname constants for DialogContent (centered modal) and SheetContent
// (bottom sheet), consumed wherever this story's retrofits use Dialog/Sheet, and reused as-is by
// Story 2.3's regression-prompt Dialog. Dark values are verbatim from
// mockups/key-room-management.html's `.modal-card` and key-log-reading-flow.html's `.sheet`/
// `.modal-card` (both dark-only mocks); light values are derived from the same frosted-white
// degradation pattern already established for the Status card (DESIGN/elevation-depth.md), since
// no light frame was rendered for these two surfaces.

export const GLASS_MODAL_CLASSNAME =
  'rounded-glass-lg border border-[rgba(40,70,50,0.14)] bg-[rgba(255,255,255,0.92)] shadow-[0_20px_40px_rgba(40,70,30,0.16)] ring-0 ' +
  'backdrop-blur-[20px] backdrop-saturate-[1.4] ' +
  'dark:border-[rgba(210,235,220,0.18)] dark:bg-[rgba(24,38,31,0.94)] dark:shadow-[0_30px_60px_rgba(0,0,0,0.55)] ' +
  'dark:backdrop-blur-[24px] dark:backdrop-saturate-[1.5]'

export const GLASS_SHEET_CLASSNAME =
  'rounded-t-glass-lg rounded-b-none border border-b-0 border-[rgba(40,70,50,0.14)] bg-[rgba(255,255,255,0.92)] shadow-[0_-20px_50px_rgba(40,70,30,0.16)] ' +
  'backdrop-blur-[20px] backdrop-saturate-[1.4] ' +
  'dark:border-[rgba(210,235,220,0.16)] dark:bg-[rgba(20,32,26,0.92)] dark:shadow-[0_-20px_50px_rgba(0,0,0,0.5)] ' +
  'dark:backdrop-blur-[28px] dark:backdrop-saturate-[1.6]'
