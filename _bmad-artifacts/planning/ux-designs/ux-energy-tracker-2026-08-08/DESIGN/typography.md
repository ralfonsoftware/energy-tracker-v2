# Typography

System font stack throughout (`{typography.font-family}`) — no webfont dependency, consistent with the self-hosted/no-frills ethos (nothing to fetch, nothing to license, nothing to fail to load). There is exactly one typeface family in this product; every role below is a size/weight/spacing variation of it, not a second typeface.

- **`{typography.status-headline}`** — the Status card's headline sentence ("Quiet week." / "Worth a look."). 700-weight, tight tracking, largest text in the product outside of a sheet's own field.
- **`{typography.status-figure}`** — applies `tabular-nums` specifically to Status and kWh figures (the Status card's supporting number, the Log Reading kWh field, Trend chart axis values, per-device kWh figures). This is the one deliberate typographic delta beyond size/weight: it keeps digits from jittering in width as they update, a steady "instrument" rhythm appropriate to a number a household is meant to trust. It is not a separate typeface — same family and weight as body text, tabular-nums is the only rule.
- **`{typography.body}`** / **`{typography.body-secondary}`** — standard running text and secondary/quiet copy (status body sentence, tariff-check microcopy, footer text).
- **`{typography.label-badge}`** — the uppercase status badge label ("WITHIN RANGE", "TRENDING") and similar small caps labels.
- **`{typography.wordmark}`** — the app wordmark in the top bar.

Everything else (form labels, settings rows, dialog titles) inherits shadcn's standard type scale unmodified.
