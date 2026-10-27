# Code review and governance guide

One short rulebook for a multi-stack squad: what a PR must have, what blocks it, and how artifacts move through the pipelines. How AI-generated work fits this process is described in [AI-assisted development](05-ai-development.md).

## Principles for every stack

These ten rules apply equally to C#, TypeScript, SQL, YAML, Dockerfiles and shell scripts; stack checklists below only add specifics.

1. **Names tell intent.** A reader understands a function from its name and signature without opening it. No abbreviations except domain terms (SKU, DLQ).
2. **One reason to change.** A function does one thing at one level of abstraction; a class or module has one responsibility. Around 20 lines per function is a smell threshold, not a law.
3. **Dependencies point inward.** Domain code knows nothing about frameworks, databases, HTTP or the DOM. The same layering applies in `web/` (`ui` → `domain` → nothing).
4. **Make illegal states unrepresentable.** Value objects and types over primitives (`Money`, `Sku`, `OrderStatus`); validate at the edge, trust inside.
5. **No hidden side effects.** Queries do not change state; commands say what they change. Time, randomness and IO come through injected abstractions.
6. **Fail loudly and early.** No swallowed exceptions, no empty `catch`, no silent defaults on bad input. Errors carry context, never secrets or personal data.
7. **Tests are production code.** One behaviour per test, named `Method_Condition_Result` (C#) or `it('does X when Y')` (TS), arrange-act-assert, builders for data, no logic in tests.
8. **Delete, don't comment out.** No dead code, no commented code, no TODO without an issue link.
9. **Comments explain why, not what.** If code needs a "what" comment, rename or extract instead.
10. **Leave it cleaner, inside scope.** Small cleanups in touched code are welcome; unrelated refactors get their own PR.

## PR acceptance criteria

A PR is mergeable when every item holds; the PR template carries this list as checkboxes and the author ticks it before requesting review.

- [ ] **Scope:** one purpose, linked to its issue or execution-guide step; under 400 changed lines excluding generated files and lock files (larger needs a reason in the description).
- [ ] **Title and commits:** Conventional Commit title (`feat(orders): ...`); squash message explains why.
- [ ] **Description:** what changed, why, how to verify (commands or screenshots for UI), risks and rollback notes for migrations or config.
- [ ] **Tests:** new behaviour covered, one test per new code path, bug fixes start with a failing test; coverage gates green (today only the front-end one, 80%, is enforced; the .NET ones depend on a coverage package, see [Quality](quality.md)).
- [ ] **Pipeline:** `gate` (displayed as `ci / gate`) and `CodeQL` green on the latest commit; no re-run-until-green on flaky tests (flaky tests get an issue and a fix).
- [ ] **Contracts:** API changes are backward compatible or versioned; OpenAPI and event contract snapshots updated; migrations are expand-only.
- [ ] **Security:** input validated at the edge, authorization on every new endpoint, no secrets, no personal data in logs.
- [ ] **Observability:** new flows emit traces; errors logged once with context; new metrics documented.
- [ ] **Docs:** README, ADR, `.env.example` or runbook updated when behaviour, decisions or config changed; every doc change is made in `docs/pt-br/` (source of truth) and mirrored in `docs/en-us/` in the same PR.
- [ ] **Approvals:** 1 reviewer; 2 (one from CODEOWNERS) for Domain, messaging, security, migrations, `.github/**` and `deploy/**`.

## What blocks a PR

Any one of these is a hard stop: the reviewer requests changes and the PR does not merge until it is fixed, whatever the deadline.

| Blocker | Example | Why it blocks |
| --- | --- | --- |
| Red or skipped pipeline | `gate` failing, tests marked skip, coverage threshold lowered | The gate is the only proof `main` stays deployable |
| Security flaw | SQL built by string concatenation, missing authorization, secret in code, HIGH/CRITICAL vulnerable dependency | Cannot be undone once deployed or leaked |
| Broken idempotency or money maths | Create path without the idempotency key, `double` for money, rounding outside `Money` | Core business guarantees (AC3) |
| Architecture violation | Domain referencing EF Core, BFF containing pricing logic, UI calling `fetch` directly | Erodes Clean Architecture; architecture tests should catch it, reviewers back them up |
| Breaking contract | Removed or renamed API field or event property without a new version | Breaks other services and the front-end |
| Destructive migration | `DROP COLUMN` in the same release that stops using it | Blocks rollback |
| Missing tests for new paths | New branch in a handler with no test | AC4 |
| Unreviewable size | 1,500 changed lines mixing refactor and feature | Reviews stop finding bugs above \~400 lines |
| Accessibility regression | axe violation, control without label, keyboard trap | AC12 |
| Unlicensed or unapproved dependency | Package outside the tech stack table, commercial licence | Legal and supply-chain risk |

Not blockers (comment as `nit:` or `suggestion:`): naming preferences within the conventions, alternative designs of equal quality, formatting the tools accept.

## Review workflow

First review within 4 working hours, merge within 1 working day; small PRs make this possible.

1. **Author self-review first.** Read the diff in the PR view, tick the template, make sure CI is green before requesting review. AI-generated PRs follow the same rule: the human who opens it owns it.
2. **Reviewer order:** run or read the tests first, then the domain and contracts, then infrastructure, then style. Check the change against its step or issue, not against personal preference.
3. **Comment labels:** `blocker:` (must fix, from the table above), `question:`, `suggestion:`, `nit:`. Only `blocker:` prevents approval.
4. **Tone:** comment on the code, not the person; explain why and propose an alternative; praise good solutions too.
5. **Resolution:** the author resolves threads they fixed; the reviewer resolves questions answered; disagreements longer than two rounds go to a 10-minute call, and the outcome is written in the thread.
6. **Cross-stack reviews:** a back-end engineer may approve front-end PRs only for API contract and security aspects, and the reverse; the stack checklist owner gives the second approval when CODEOWNERS requires it.
7. **After merge:** the author watches `cd` to staging and the smoke test; a failure is the author's to fix or revert within the hour.

## Stack checklists

Use the checklist of each stack the PR touches, on top of the acceptance criteria.

**.NET (C#)**

- [ ] `sealed` by default, records for DTOs and value objects, no public setters on aggregates
- [ ] `async` all the way with `CancellationToken`; no `.Result`, `.Wait()` or `async void`
- [ ] Money as `decimal` via `Money`; time via `TimeProvider`; no static mutable state
- [ ] EF Core: `AsNoTracking` for reads, projections instead of loading graphs, no queries in loops, `FromSql` interpolation only
- [ ] Endpoints return `TypedResults`, validate input, map errors to ProblemDetails, declare authorization
- [ ] Commands and queries are `public sealed record`s with a FluentValidation validator; no hand-written required-field or format checks ([design rules](02-execution-guide.md#design-rules-for-every-step))
- [ ] Mapping through Mapperly `[Mapper]` classes; no hand-written member-by-member copies; no AutoMapper
- [ ] Logging through source-generated `LoggerMessage`; no string interpolation in log calls

**TypeScript front-end**

- [ ] `strict` types, no `any`, no non-null assertions without a comment
- [ ] All HTTP through `shared/api`; no `fetch` in `ui/`
- [ ] DOM built with `textContent` or templates, never `innerHTML` with data
- [ ] Every control has a label, focus is visible, keyboard path works, `aria-live` for async updates
- [ ] Bootstrap utilities over custom CSS; no inline styles
- [ ] No inline script and no script, style or font from another origin: nginx's `Content-Security-Policy` (`deploy/nginx/nginx.conf`) blocks them

**SQL and MongoDB**

- [ ] Parameterized always; no string-built queries or filters
- [ ] Every new query has a supporting index; `EXPLAIN` attached for list or search queries
- [ ] Migrations expand-only, idempotent, reviewed as SQL script
- [ ] Mongo filters via `Builders<T>`; writes needing two documents use a session transaction

**Infrastructure (Docker, compose, GitHub Actions, shell)**

- [ ] Images non-root, pinned base tags, multi-stage, no secrets in layers
- [ ] Actions pinned to commit SHAs, least-privilege `permissions`, no `pull_request_target` with checkout of PR code
- [ ] Shell scripts `set -euo pipefail`, LF endings, pass `shellcheck`
- [ ] Compose healthchecks for every service; config through env vars documented in `.env.example`

## Artifacts and pipelines

Build once, promote the same bytes, and never edit a pipeline or artifact by hand. Pipeline files are code: they go through PRs with two approvals.

| Artifact | Produced by | Identity | Stored in | Retention | Rule |
| --- | --- | --- | --- | --- | --- |
| Container images | `cd.yml` `images` job on `main` | `sha-<commit>`, then `vX.Y.Z` on release | GHCR | Releases forever; `sha-*` 90 days | Deployed by digest; never rebuilt for a release; must carry provenance attestation and SBOM |
| Migration bundle / migrator image | `cd.yml` | Same commit tag as services | GHCR | Same as images | Runs before services; expand-only |
| Test and coverage reports | `ci.yml` | Run id | Actions artifacts | 14 days | Read in review; never committed. .NET does not produce a coverage report yet and the front-end one is not uploaded as an artifact |
| OpenAPI documents | Build | File in repo | Git (`*/openapi/*.json`) | Git history | Generated, committed, drift fails CI |
| Mutation and load reports | `mutation.yml` (not created, C57), load profile | Run id / date | Actions artifacts; `docs/perf/` holds only the `EXPLAIN` plans today | 30 days | Trends reviewed monthly |
| Release notes | Tag | `vX.Y.Z` | GitHub Releases | Forever | Generated from Conventional Commits, edited by the Tech Lead; there is no release or release-notes automation yet |

Pipeline rules:

- `main` is always releasable; a red `main` stops merges until fixed or reverted (revert first, investigate after).
- Versions follow SemVer: `feat` → minor, `fix` → patch, `!` or `BREAKING CHANGE` → major.
- Every deploy, staging or production, passes `deploy-gate`; production also needs an environment approval by someone other than the tag author.
- Secrets live only in GitHub Environments; workflows get the least `permissions` they need; forks never receive secrets.
- Flaky test policy: quarantine through an issue within 24 h, fix within one week; quarantined tests still run and report.
- Dependabot PRs are reviewed like any PR; base image updates go through staging before a release.
- Rollback = redeploy the previous release's digests with `scripts/deploy.sh <staging|production> --rollback`; database rollback is never needed because migrations are backward compatible.
