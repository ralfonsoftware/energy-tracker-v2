// Shared between a Room's suppressed-row `<div>` wrapper and its normal `<details>` wrapper so the
// two branches can't visually drift apart independently.
export const ROOM_ROW_BORDER_CLASS = 'border-b border-[rgba(40,70,50,0.09)] last:border-b-0 dark:border-[rgba(210,235,220,0.1)]'

// At >=660px collapsed Rooms/Power Points become tiles in an auto-fill grid and an open one spans the
// whole row (UX-DR29). `wide:border` re-asserts all four sides (the row classes only set one) and the
// `wide:dark:` colour beats the row's `dark:border-[...]`. Never add `grid-auto-flow: dense` — it would
// reorder tiles relative to DOM/tab order.
export const WIDE_TILE_CLASS =
  'wide:open:col-span-full wide:rounded-glass-sm wide:border wide:last:border-b wide:border-surface-quiet-border wide:dark:border-surface-quiet-border wide:bg-surface-quiet'
export const WIDE_TILE_GRID_CLASS = 'wide:grid wide:grid-cols-[repeat(auto-fill,minmax(220px,1fr))] wide:gap-2.5'
