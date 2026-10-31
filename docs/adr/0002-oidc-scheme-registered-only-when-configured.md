# 0002: The OIDC scheme exists only when it is fully configured

Source: Stories 3.6, 8.1, 11.8 and 11.12, AD-17, AD-3, `deferred-work.md` (entry on `oidcConfigured`).
Code: `src/EnergyTracker.Api/Composition/AuthRegistration.cs`.

## Context

ASP.NET Core's authentication middleware initializes every `IAuthenticationRequestHandler` scheme on every request, to check whether the request matches the scheme's callback path. `OpenIdConnectOptions.Validate()` throws on a blank `Authority` or `MetadataAddress`. A half configured OIDC scheme would therefore answer 500 on every route, `/health` included. An unconfigured provider is an expected self-host state.

Other facts the registration encodes:

- Auth0 omits the `email` claim from both the ID token and the userinfo response unless the `email` scope is requested, so `/api/session` returned a null email regardless of the claim key handling (found in a live Auth0 check, Story 8.1).
- Issuer and subject are the whole basis of tenant isolation. `GetClaimsFromUserInfoEndpoint` merges claims after validation and does not promise to keep the original `Claim.Issuer` on each claim.
- Providers validate `post_logout_redirect_uri` against the registered Allowed Logout URLs and need `id_token_hint` or `client_id` to know the application. With `SaveTokens = false` there is no ID token, and the handler does not set `ClientId` on the end-session request.

## Decision

- `AddOpenIdConnect` runs only when `Oidc:ClientId` and `Oidc:Authority` are both set. `AddAuth` returns that as `oidcConfigured`; `/login` answers 503 "not configured" when it is false. Do not simplify the condition to the client id alone.
- The `Oidc:*` values are read once, in this method.
- `OnTokenValidated` copies the validated issuer into one dedicated claim (`HouseholdClaimTypes.ValidatedIssuer`) before the claim merge. No other claim is added: the Household is resolved per request (Story 11.12) and a guard counts `new Claim(`.
- `OnRedirectToIdentityProviderForSignOut` sets `ClientId` and hands the end-session URL to `POST /logout` instead of writing a 302 (Story 11.8).
- The `email` scope is requested explicitly.

## Consequences

- Adding a second provider or a claim needs a decision here and in `HouseholdMembershipIsResolvedOncePerRequestTests`.
- Local development without a provider still starts and serves everything except sign-in.
