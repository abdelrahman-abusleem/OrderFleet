# Changelog

## 2026-11-05

- Preserve valid Outbox messages through prolonged broker outages with capped delayed retries.
- Complete reconciled capture/refund audit history without duplicate financial effects.
- Add live privileged-role revocation and restricted audited role/membership administration.
- Add explicit frontend/WebSocket origin boundaries, configurable abuse limits and private courier assignment discovery.
- Require explicit mock deployment mode, restricted hosts and HTTPS issuer; label mock processing and enable HSTS outside local/test.
- Add measured partial active-order recovery indexes in a new migration; preserve the original19 migrations.
- Expand real infrastructure regressions, clean source comments and consolidate technical documentation.

Verification results and retained limitations are recorded in [release verification](docs/16_RELEASE_VERIFICATION.md).
