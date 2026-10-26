# AI-assisted development

Which AI development tool this project uses, why it was chosen, and how it is used in every stage of the lifecycle, with links to the evidence (chat sessions, commits, PRs) for each stage.

## Tool

| Item | Choice |
| --- | --- |
| Tool | **Claude Code** (Anthropic), used in the Claude desktop app (Code tab) and the `claude` CLI |
| Planning, architecture, review | **Claude Opus 5.5** (`claude-opus-5-5`) |
| Step-by-step implementation | **Claude Sonnet 5.5** (`claude-sonnet-5-5`), one session per execution-guide step |
| Human role | Owns every decision, reviews and merges every PR; the AI never merges, never approves its own PR and never touches environment secrets |

## Why Claude Code

- **Works inside the repository, not in a chat window.** It reads and edits files, runs `dotnet`, `npm`, `docker compose`, `git` and `gh`, and sees real compiler and test output. A step is done when the commands pass, not when the answer looks right.
- **Fits the one-step, one-commit, one-PR workflow.** The [Execution guide](02-execution-guide.md) splits the work into 58 small steps with tests and verify commands; Claude Code can take one step from branch to PR on its own, and a human reviews a small diff.
- **Long context.** The plan, execution guide, review guide and the code it touches fit in one session, so each step is checked against the written decisions instead of the model's memory.
- **Two models for two kinds of work.** Opus 5.5 handles ambiguous work (requirements, trade-offs, architecture, review). Sonnet 5.5 handles fully specified steps faster and cheaper, which is why the [Tech dimensioning](04-tech-dimensioning.md) assumes it for implementation.
- **Built-in review and planning aids.** Plan mode for design before editing, sub-agents for independent reviews, and the `/code-review` and `/security-review` commands as a first reviewer before the human one.
- **Shareable sessions.** Every session has a link, which is how this repository proves AI use per stage (see [Evidence](#evidence)).

Alternatives considered: GitHub Copilot (strong completion in the editor, weaker at multi-file steps that end in a verified PR) and chat-only assistants (no access to the build, tests or git, so every answer has to be copied and checked by hand).

## Process across the lifecycle

| # | Stage | How the AI is used | Model | Output |
| --- | --- | --- | --- | --- |
| 1 | Requirements analysis | Read the challenge, list requirements and acceptance criteria, raise open questions (currency, who creates orders, confirmation, retention) for a human decision | Opus 5.5 | Decisions table in the [Implementation plan](01-implementation-plan.md#overview) |
| 2 | Architecture and design | Propose and compare options (BFF or not, Wolverine or MediatR + RabbitMQ.Client, Mongo replica set), draw diagrams, write the data model and idempotency flow | Opus 5.5 | [Implementation plan](01-implementation-plan.md) |
| 3 | Work breakdown | Split the plan into 58 commit-sized steps, each with tests and verify commands, plus the reusable agent prompt | Opus 5.5 | [Execution guide](02-execution-guide.md) |
| 4 | Governance | Draft clean-code principles, PR criteria, blockers, stack checklists and pipeline rules | Opus 5.5 | [Code review guide](03-code-review-guide.md) |
| 5 | Dimensioning | Size runtime, effort per phase, team, timeline and risks from the plan | Opus 5.5 | [Tech dimensioning](04-tech-dimensioning.md) |
| 6 | Implementation | One Sonnet session per step: branch, tests first, code, verify, one commit, PR | Sonnet 5.5 | Commits and PRs `step/Cxx-*` |
| 7 | Testing | Tests written before code in each step (TDD), coverage gates enforced by CI; the AI may not skip tests or lower gates | Sonnet 5.5 | Test projects, `web/tests` |
| 8 | Code review | `/code-review` on every PR before human review; Opus reviews Domain, messaging and security PRs; the human approves and merges | Opus 5.5 + human | PR comments |
| 9 | CI/CD | Pipelines written as a step (C08) and reviewed like code; the deploy gate is never edited without two approvals | Sonnet 5.5 + human | `.github/workflows/*` |
| 10 | Documentation and release | README, ADRs, runbook and acceptance evidence kept current in each step and finalised in C58 | Sonnet 5.5 | `README.md`, `docs/adr/` |
| 11 | Presentation | Draft slides summarising architecture, decisions and differentials | Opus 5.5 | `docs/presentation/` |

### Guardrails

- The AI works from the written docs; when a step contradicts the plan or needs an unwritten decision, it stops and asks (rule in the agent prompt).
- One step per session, so each session link maps to exactly one PR.
- No merge, no approval, no change to branch protection, coverage gates, analyzers or secrets by the AI.
- Every AI-generated PR is owned by the human who opens it ([Code review guide](03-code-review-guide.md#review-workflow), step 1).

## Evidence

Fill in one row per session as the work happens. Session links must be shareable (Claude Code: share the session from the app; claude.ai: Share → copy link).

### Planning sessions

| Stage | Date | Model | Session link | Output |
| --- | --- | --- | --- | --- |
| 1–5 Requirements, architecture, breakdown, governance, dimensioning | 2026-10-26 | Opus 5.5 | _add link_ | `docs/01`–`04` |
| Repository and documentation organisation | 2026-10-26 | Opus 5.5 | _add link_ | `README.md`, `docs/05` |

### Implementation sessions

| Step | Model | Session link | PR | Notes (rework, decisions asked) |
| --- | --- | --- | --- | --- |
| C01 | Sonnet 5.5 | _add link_ | _add PR_ | |
