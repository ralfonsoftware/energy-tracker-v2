# 0003: Forwarded headers are trusted without a `KnownProxies` allowlist

Source: Stories 1.7 and 11.7, AD-27, `docs/self-hosting.md`.
Code: `src/EnergyTracker.Api/Program.cs` (`ForwardedHeadersOptions`).

## Context

Azure Container Apps terminates TLS at its ingress and forwards plain HTTP to the container. Without `UseForwardedHeaders` the OIDC handler builds an `http://` `redirect_uri`, and login fails with any provider that requires an exact match.

Container Apps' external ingress is the only way into the container. A client cannot reach it past the ingress, so trusting `X-Forwarded-*` without `KnownProxies` is safe there. A self-hosted container is different: it must sit behind a reverse proxy, otherwise this trust extends to any direct caller.

What a lying client can reach: the OIDC `redirect_uri` and the cookie `Secure` decision read `Request.Scheme` and gain nothing from a forged scheme. The `/login` rate limit (AD-27) keys on the client IP resolved here. On Azure that key is not spoofable: the ingress supplies the rightmost `X-Forwarded-For` entry and `ForwardLimit` stays at its default of 1 (confirmed with a live probe, Story 11.7).

## Decision

- `UseForwardedHeaders` runs before anything reads `Request.Scheme` or `Host`.
- The known networks are not narrowed and `KnownProxies` is not set. Narrowing them to "fix" the self-host case breaks Story 1.7 again.
- `docs/self-hosting.md` states the reverse proxy requirement.

## Consequences

- A deployment target other than Container Apps needs its own review of the `/login` key before it is supported.
- `ForwardedHeadersTests` pin the behaviour.
