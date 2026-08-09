# 7. Success Metrics

**Primary**
- **SM-1**: Spreadsheet retirement — household stops using a spreadsheet, Energy Tracker becomes sole source of truth. Validates FR-1–FR-9, FR-24–FR-28.
- **SM-2**: Reading habit retention — Meter Reading cadence holds at the honest 1-2 day interval over time rather than trailing off. Validates FR-1.
- **SM-3**: Early trend catch — at least one real instance where the Status signal flags a trend early enough to change behavior before the invoice arrives. Validates FR-6, FR-7. Measured by household self-report (this is a personal self-hosted tool without phone-home telemetry, per Constraints — the household, not the product, is the source of truth on whether this happened).
- **SM-4**: Confident tariff decision — a stay/switch decision made with real confidence in the bonus-normalized Radar figure, at least once. Validates FR-12–FR-14. Measured by household self-report, same basis as SM-3.

**Secondary**
- **SM-5**: External adoption — at least one other self-hoster runs it against their own household without forking/hardcoding. Validates FR-2, i18n/Locale NFR.
- **SM-6**: Extension without code change — new Smart Plug formats or event rules addable via Extension Points without touching core code. Validates FR-19, FR-20 (if retained).

**Counter-metrics (do not optimize)**
- **SM-C1**: Insight/notification volume — a status update that doesn't change what you'd do next is noise. Counterbalances SM-3.
- **SM-C2**: Drill-down engagement/time-in-app — the headline Status succeeding means users *don't* need to check the drill-down to trust it; more drill-down usage is not a win. Counterbalances SM-1/SM-2.
