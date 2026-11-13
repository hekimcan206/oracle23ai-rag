# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within 5 working days. If
the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Authentication | None in the service; run it behind a gateway that authenticates. Compose binds every port to 127.0.0.1 |
| Database access | Separate owner and application accounts. The Oracle application account writes documents only through the duality view and may update nothing in the base tables except the embedding column (integration-tested) |
| Concurrency | Conditional writes with `If-Match` return `412` on a stale ETag; concurrent unconditional writes never leave a mixed document |
| SQL | Bind variables for every value; the integer search knobs that Oracle's `FETCH APPROX` cannot bind are range-checked; identifiers pass an allow-list |
| Input limits | 1 MB request bodies, 200,000-character documents, 1,000-character questions, `k ≤ 100` |
| Logs | Ids and timings only, no document text or questions |
| Container | Chiseled .NET runtime, non-root user, no shell |
| Secrets | None in the repository; `.env.example` holds demo-only values |
| Data | Everything in the repository is synthetic |
