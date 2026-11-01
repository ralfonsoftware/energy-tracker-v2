# Architecture decision records

An ADR here keeps implementation reasoning that is too long for a code comment. The code keeps a one-line pointer (`// ADR 0003: ...`) and the record holds the context, the decision and what follows from it.

`AD-nn` decisions live in the architecture spine (`_bmad-artifacts/planning`). An ADR does not replace one. It explains why a piece of code is shaped the way it is, often because of a production incident.

## Template

File name: `NNNN-short-title.md`, numbered in order. Keep each record under about 60 lines and name its source (AD, story, incident).

```markdown
# NNNN: Title

Source: ...
Code: ...

## Context
## Decision
## Consequences
```

A record is written only when a comment was shortened and its reasoning is worth keeping. Do not write one for a decision nobody will ask about.

## Index

| No. | Title | Code |
| --- | --- | --- |
| [0001](0001-serilog-and-default-logging-providers.md) | Serilog, default logging providers and `writeToProviders` | `Api/Composition/ObservabilityRegistration.cs` |
| [0002](0002-oidc-scheme-registered-only-when-configured.md) | The OIDC scheme exists only when it is fully configured | `Api/Composition/AuthRegistration.cs` |
| [0003](0003-forwarded-headers-without-known-proxies.md) | Forwarded headers are trusted without a `KnownProxies` allowlist | `Api/Program.cs` |
| [0004](0004-route-scoped-request-body-limit.md) | The raised body size limit applies to `/api/household-import` only | `Api/Program.cs` |
| [0005](0005-smart-plug-import-write-path.md) | Smart Plug import write path | `SmartPlugImportRepository.Import.cs` |
| [0006](0006-smart-plug-mapping-conflict-classification.md) | Mapping an import to a Power Point | `SmartPlugImportRepository.Mapping.cs` |
| [0007](0007-smart-plug-cleanup-chunking-and-sweep-lock.md) | Smart Plug cleanup, chunked deletes and the sweep lock | `SmartPlugImportRepository.Cleanup.cs` |
| [0008](0008-invite-token-redaction-in-logs-and-traces.md) | Invite tokens are redacted in request logs and server spans | `Api/InviteTokenRedactor.cs`, `Api/Composition/ObservabilityRegistration.cs` |
| [0009](0009-failed-startup-exits-non-zero.md) | A host that fails to start exits with code 1 | `Api/StartupFailureExit.cs`, `Api/Program.cs` |
