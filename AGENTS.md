# Agent instructions

Rules for every AI agent (Claude Code, Copilot, Codex, Cursor and others) working in this repository. They add to the agent prompt in the execution guide; they do not replace it.

## Documentation is bilingual, pt-BR first

All project documentation lives in two trees with the same file names and the same structure:

| Tree | Language | Role |
| --- | --- | --- |
| `docs/pt-br/` | Brazilian Portuguese | **Source of truth.** Top priority. |
| `docs/en-us/` | English (US) | Mirror of `docs/pt-br/`. |

Rules:

1. **Write pt-BR first.** Every new document or change is made in `docs/pt-br/` first, then mirrored in `docs/en-us/`.
2. **Same commit, same PR.** Both languages change together. A PR that changes one tree and not the other is incomplete and does not merge. If time runs short, pt-BR is the one that must be complete and correct.
3. **pt-BR wins.** When the two trees disagree, pt-BR is correct and en-US is fixed to match it, never the reverse.
4. **Same shape.** Same file names and paths, same heading order, same tables (rows and columns), same lists. A reader can open the same path in either tree and find the same content.
5. **Translate prose, not code.** Code blocks, inline code, identifiers, commands, paths, env vars, URLs, commit subjects (Conventional Commits stay in English) and step IDs (C01–C58) are identical in both trees. Fenced blocks are copied byte for byte, including Mermaid labels and comments inside code. The one exception is a fenced `text` block that is prose meant to be read, such as the agent prompt in `02-execution-guide.md`: it is translated, keeping its commands, paths and placeholders unchanged.
6. **Anchors follow the language.** Links between docs stay relative (`02-execution-guide.md`); anchors use each language's own headings (`#regras-de-design-para-todas-as-etapas` in pt-BR, `#design-rules-for-every-step` in en-US).
7. **New docs go in both trees**, including ADRs (`docs/pt-br/adr/`, `docs/en-us/adr/`), runbooks, `operations/`, `quality.md` and the presentation. Language-neutral artifacts (query plans, images, generated reports) stay outside both trees, for example `docs/perf/`.
8. **Evidence rows too.** The row each step adds to the "Implementation sessions" table goes in `docs/pt-br/05-ai-development.md` and, translated, in `docs/en-us/05-ai-development.md`.

Use natural Brazilian Portuguese (not European Portuguese), keeping the English terms Brazilian developers use as-is (deploy, pipeline, commit, pull request, branch, merge, endpoint). An execution-guide "step" is an "etapa".

## Where to start

- Agent prompt and step list: [`docs/pt-br/02-execution-guide.md`](docs/pt-br/02-execution-guide.md) ([en-US](docs/en-us/02-execution-guide.md)).
- Review criteria and blockers: [`docs/pt-br/03-code-review-guide.md`](docs/pt-br/03-code-review-guide.md) ([en-US](docs/en-us/03-code-review-guide.md)).
