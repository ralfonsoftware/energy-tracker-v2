# 0004: The raised body size limit applies to `/api/household-import` only

Source: Stories 7.1, 7.2 and 11.10.
Code: `src/EnergyTracker.Api/Program.cs` (the body size middleware, `FormOptions`), `HouseholdImportEndpoints.cs`.

## Context

A restore upload can be larger than Kestrel's default `MaxRequestBodySize` (about 28.6 MB): the export of one small real Household was already 32 MB (Story 7.1), so the 20 MB cap of the Smart Plug upload does not transfer. 250 MB bounds what one upload can occupy on temp disk.

The raw multipart body (boundaries, headers) is always a little larger than `IFormFile.Length`. Without headroom above the app level limit, Kestrel rejects an upload near the limit before the endpoint runs, and the friendly 400 can hardly ever fire.

`IFormFile` model binding reads the whole body while the endpoint is invoked, before the lambda runs. An override inside the handler is too late. A process wide Kestrel raise was the first version. It also widened the exposure of `SmartPlugImportEndpoints`: Kestrel would buffer up to the raised size for every route before that endpoint's own lower 20 MB check could run.

`FormOptions.MultipartBodyLengthLimit` (128 MB default) is a second, form parsing stage limit. It is only reached after Kestrel's gate let the body through, so raising it globally does no harm while the gate stays scoped.

## Decision

- Middleware sets `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` to `HouseholdImportEndpoints.MaxFileSizeBytes + BodySizeHeadroomBytes` (5 MB) for paths under `/api/household-import` only. It runs before routing.
- Every other route keeps Kestrel's default.
- `FormOptions.MultipartBodyLengthLimit` is raised once, globally, to `MaxFileSizeBytes`.
- The upload is streamed to a temp file first and validated from the file (Story 11.10), never buffered whole in memory.

## Consequences

- A second large upload route has to be added to the middleware, not raised globally.
- The temp file lives in this process (AD-24, one instance).
