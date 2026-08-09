# 2. Target User

## 2.1 Jobs To Be Done

Household members think in kWh and euros, not raw voltage/current curves — every product-facing number, label, and insight stays in those terms.

- **Emotional/early-warning:** "Tell me early if I'm heading for a surprise invoice, so the annual settlement doesn't blindside me."
- **Decision:** "Help me decide whether switching tariffs would actually save money, once switching bonuses are stripped out."
- **Functional/habit:** "Let me log a meter reading in under a minute, standing at the meter, without breaking the habit."
- **Functional/trust:** "Give me a plausible reason for a spike without requiring me to audit my own data."
- **Social (secondary persona):** "Let me run this against my own flat without forking the code first."

## 2.2 Non-Users (v1)

- Households wanting real-time or near-real-time monitoring — this is a manual-read, file-upload-driven tool by design, not a live dashboard.
- Anyone expecting a hosted service rather than self-deployment.
- Anyone who wants the tool to perform an automated room-by-room energy audit for them — that promise is the one v2 deliberately walked back from v1.

## 2.3 Key User Journeys

- **UJ-1. Sam logs a reading on the way out the door.**
  - **Persona + context:** Sam tracks their own flat's electricity with no smart main meter, folding the reading into a daily routine — taking out the trash, leaving for work, back from the gym.
  - **Entry state:** Already authenticated (stays logged in on their phone), opens the app standing right at the meter.
  - **Path:** Opens the app → reading-entry screen with today's date/time pre-selected → types the meter's current number → taps save → sees a quick confirmation.
  - **Climax:** Confirmation lands in under a minute — reading captured, habit reinforced, nothing to break the streak.
  - **Resolution:** Sam continues on their way. No dashboard, no chart, no detour.
  - **Edge case:** Sam enters a second reading later the same day (different timestamp) — accepted as a distinct entry, not rejected as a duplicate or silently overwritten.

- **UJ-2. Sam checks the dashboard after new data lands.**
  - **Persona + context:** A fresh reading just went in, or a smart-plug file import finished — Sam opens the app deliberately, not the daily habit-tap, to see what changed.
  - **Entry state:** Authenticated, navigates to the main dashboard.
  - **Path:** Opens dashboard → sees the primary pace-vs-baseline status (within / below / trending) → sees a tariff-check prompt *only when due* (starting 3 months before the earliest contract exit, then recurring at a customizable cadence after the minimum period passes) → taps into either for detail.
  - **Climax:** Both questions — "am I on track" and "is my tariff still worth it" — answered together, without hunting for either.
  - **Resolution:** Sam dismisses (all fine) or taps through into Tariff Radar / Pattern Detective detail.
  - **Edge case:** No tariff check is currently due (mid-contract, more than 3 months out) — the insight area shows a neutral/empty state, not a fabricated recommendation.

- **UJ-3. Sam browses trends on a calm evening.**
  - **Persona + context:** No urgent task — self-initiated curiosity, distinct from the daily habit-tap and the "new data arrived" check.
  - **Entry state:** Authenticated, opens the app with no specific trigger.
  - **Path:** Opens dashboard/trends view → reviews the general consumption trend over time → drills into smart-plug/device-level data for anything notable worth being aware of.
  - **Climax:** Nothing needs fixing, but Sam either learns something about their pattern or spots something minor worth remembering.
  - **Resolution:** Closes the app. Low-stakes browsing, satisfied.
