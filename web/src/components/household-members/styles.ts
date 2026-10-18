// Story 11.9: colour tokens of the approved mock (mockups/key-household-members-2026-10-17.html). The error red is the
// Profile menu's (#A5382D light, #EB958C dark) rather than the theme's `destructive`, which measured 4.26:1 on the
// dark glass card in the locale/theme sweep. The light accent text is the pill's dark end (#145A47): #1E7A61 on the
// avatar tint measured 4.23:1.
export const ACCENT_TEXT = 'text-[#145A47] dark:text-[#6FD1B1]'
export const ACCENT_SURFACE = 'bg-[rgba(30,122,97,0.14)] dark:bg-[rgba(111,209,177,0.16)]'
export const ERR_TEXT = 'text-[#A5382D] dark:text-[#EB958C]'
export const ERR_BORDER = 'border-[rgba(176,50,40,0.3)] dark:border-[rgba(235,149,140,0.35)]'
export const ERR_SURFACE = 'bg-[rgba(176,50,40,0.07)] dark:bg-[rgba(235,149,140,0.1)]'
// A boxed error or warning line: tinted surface, tinted border, readable text.
export const ERR_BOX = `${ERR_TEXT} ${ERR_BORDER} ${ERR_SURFACE} border`
// The destructive confirm button of the two dialogs and the Leave button.
export const ERR_BUTTON = `${ERR_TEXT} border-[#A5382D] dark:border-[#EB958C] bg-[rgba(176,50,40,0.1)] dark:bg-[rgba(235,149,140,0.16)] hover:bg-[rgba(176,50,40,0.18)] dark:hover:bg-[rgba(235,149,140,0.24)]`
