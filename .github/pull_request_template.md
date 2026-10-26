## What and why

<!-- What changed and why. Link the execution-guide step (e.g. C01) or issue. -->

Step / issue:

## How to verify

<!-- Commands, or screenshots for UI changes. -->

## Risks and rollback

<!-- Migrations, config, deploy impact. Write "none" if there are none. -->

## AI session

<!-- Mandatory when the change was produced with AI. The human who opens the PR owns it. -->

- Model:
- Shared session link:

## Definition of done

- [ ] Builds with warnings as errors; formatters report no changes
- [ ] Tests named in the step exist and pass; at least one unit test per new code path
- [ ] Coverage gates and architecture tests pass
- [ ] OpenAPI, README or `.env.example` updated when behaviour or config changed
- [ ] Doc changes made in `docs/pt-br/` (source of truth) and mirrored in `docs/en-us/`
- [ ] AI session link in this PR and a row in the evidence table of both [pt-BR](../docs/pt-br/05-ai-development.md#sessões-de-implementação) and [en-US](../docs/en-us/05-ai-development.md#implementation-sessions)
- [ ] CI green; required approvals in place (two for Domain, messaging, security or CI changes)

## PR acceptance criteria

- [ ] **Scope:** one purpose, linked to its step or issue; under 400 changed lines excluding generated files and lock files
- [ ] **Title and commits:** Conventional Commit title
- [ ] **Contracts:** API changes are backward compatible or versioned; migrations are expand-only
- [ ] **Security:** input validated at the edge, authorization on every new endpoint, no secrets, no personal data in logs
- [ ] **Observability:** new flows emit traces; new metrics documented
- [ ] **Docs:** README, ADR, `.env.example` or runbook updated when behaviour, decisions or config changed; doc changes written in pt-BR first and mirrored in en-US
