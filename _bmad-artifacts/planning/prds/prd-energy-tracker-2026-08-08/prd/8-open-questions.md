# 8. Open Questions

1. Is generic Smart-Plug data-source column mapping (FR-20) actually achievable with reasonable engineering effort given real-world export-format variance, or should it be dropped entirely? `[NOTE FOR PM]`
2. What delivery channel(s) will ambient/push notification of Status use once built (native push, email, ntfy/webhook, etc.), and when does it get prioritized relative to the other Could-haves?
3. Should Pattern Detective / Tariff Radar eventually support multiple threshold profiles (seasonal, per-room) instead of one tunable number? Idea captured in `addendum.md`; revisit once FR-6/FR-21 are live and there's real usage signal.
4. What's the concrete target architecture/hosting shape? A candidate shape is captured in `addendum.md` for the architecture phase — not yet a locked decision.
