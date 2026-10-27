# Guia de revisão de código e governança

Um manual de regras curto para um squad multi-stack: o que um PR precisa ter, o que o bloqueia e como os artefatos percorrem os pipelines. Como o trabalho gerado por IA se encaixa neste processo está descrito em [Desenvolvimento assistido por IA](05-ai-development.md).

## Princípios para todas as stacks

Estas dez regras valem igualmente para C#, TypeScript, SQL, YAML, Dockerfiles e scripts shell; os checklists por stack abaixo apenas acrescentam especificidades.

1. **Nomes revelam a intenção.** Quem lê entende uma função pelo nome e pela assinatura, sem precisar abri-la. Sem abreviações, exceto termos do domínio (SKU, DLQ).
2. **Um único motivo para mudar.** Uma função faz uma única coisa, em um único nível de abstração; uma classe ou módulo tem uma única responsabilidade. Cerca de 20 linhas por função é um sinal de alerta, não uma lei.
3. **Dependências apontam para dentro.** O código de domínio não sabe nada sobre frameworks, bancos de dados, HTTP ou o DOM. A mesma divisão em camadas vale em `web/` (`ui` → `domain` → nada).
4. **Torne estados ilegais irrepresentáveis.** Value objects e tipos em vez de primitivos (`Money`, `Sku`, `OrderStatus`); valide na borda, confie no interior.
5. **Sem efeitos colaterais ocultos.** Consultas não alteram estado; comandos deixam claro o que alteram. Tempo, aleatoriedade e IO chegam por abstrações injetadas.
6. **Falhe cedo e de forma explícita.** Nenhuma exceção engolida, nenhum `catch` vazio, nenhum valor padrão silencioso para entrada inválida. Erros carregam contexto, nunca segredos ou dados pessoais.
7. **Testes são código de produção.** Um comportamento por teste, nomeado `Method_Condition_Result` (C#) ou `it('does X when Y')` (TS), arrange-act-assert, builders para os dados, sem lógica nos testes.
8. **Apague, não comente.** Sem código morto, sem código comentado, sem TODO sem link para uma issue.
9. **Comentários explicam o porquê, não o quê.** Se o código precisa de um comentário de "o quê", renomeie ou extraia.
10. **Deixe mais limpo, dentro do escopo.** Pequenas limpezas no código alterado são bem-vindas; refatorações não relacionadas vão em um PR próprio.

## Critérios de aceite de PR

Um PR está apto para merge quando todos os itens são atendidos; o template de PR traz esta lista como checkboxes, e o autor os marca antes de pedir revisão.

- [ ] **Escopo:** um único propósito, vinculado à sua issue ou etapa do guia de execução; menos de 400 linhas alteradas, sem contar arquivos gerados e lock files (acima disso, é preciso uma justificativa na descrição).
- [ ] **Título e commits:** título no padrão Conventional Commits (`feat(orders): ...`); a mensagem do squash explica o porquê.
- [ ] **Descrição:** o que mudou, por quê, como verificar (comandos ou capturas de tela para UI), riscos e notas de rollback para migrações ou configuração.
- [ ] **Testes:** comportamento novo coberto, um teste por novo caminho de código, correções de bug começam com um teste falhando; gates de cobertura verdes (hoje só o do front-end, 80%, é cobrado; os do .NET dependem de um pacote de cobertura, veja [Qualidade](quality.md)).
- [ ] **Pipeline:** `gate` (exibido como `ci / gate`) e `CodeQL` verdes no último commit; nada de reexecutar até ficar verde quando há testes flaky (testes flaky ganham uma issue e uma correção).
- [ ] **Contratos:** mudanças de API são retrocompatíveis ou versionadas; snapshots dos contratos OpenAPI e de eventos atualizados; migrações são apenas de expansão (expand-only).
- [ ] **Segurança:** entrada validada na borda, autorização em todo endpoint novo, nenhum segredo, nenhum dado pessoal em logs.
- [ ] **Observabilidade:** novos fluxos emitem traces; erros registrados uma única vez, com contexto; novas métricas documentadas.
- [ ] **Documentação:** README, ADR, `.env.example` ou runbook atualizados quando comportamento, decisões ou configuração mudarem; toda alteração de documentação é feita em `docs/pt-br/` (fonte da verdade) e espelhada em `docs/en-us/` no mesmo PR.
- [ ] **Aprovações:** 1 revisor; 2 (um deles do CODEOWNERS) para Domain, mensageria, segurança, migrações, `.github/**` e `deploy/**`.

## O que bloqueia um PR

Qualquer um destes itens é uma parada obrigatória: o revisor solicita alterações e o PR não é mergeado até que o problema seja corrigido, seja qual for o prazo.

| Bloqueio | Exemplo | Por que bloqueia |
| --- | --- | --- |
| Pipeline vermelho ou pulado | `gate` falhando, testes marcados como skip, limite de cobertura reduzido | O gate é a única prova de que a `main` continua pronta para deploy |
| Falha de segurança | SQL montado por concatenação de strings, autorização ausente, segredo no código, dependência vulnerável HIGH/CRITICAL | Não pode ser desfeita depois do deploy ou do vazamento |
| Idempotência ou cálculo monetário quebrados | Caminho de criação sem a chave de idempotência, `double` para dinheiro, arredondamento fora de `Money` | Garantias centrais do negócio (AC3) |
| Violação de arquitetura | Domain referenciando o EF Core, BFF contendo lógica de preço, UI chamando `fetch` diretamente | Corrói a Clean Architecture; os testes de arquitetura devem detectar isso, e os revisores os reforçam |
| Quebra de contrato | Campo de API ou propriedade de evento removido ou renomeado sem uma nova versão | Quebra outros serviços e o front-end |
| Migração destrutiva | `DROP COLUMN` na mesma release que deixa de usar a coluna | Impede o rollback |
| Testes ausentes para novos caminhos | Nova ramificação em um handler sem teste | AC4 |
| Tamanho inviável para revisão | 1.500 linhas alteradas misturando refatoração e funcionalidade | Revisões deixam de encontrar bugs acima de \~400 linhas |
| Regressão de acessibilidade | Violação do axe, controle sem rótulo, armadilha de teclado | AC12 |
| Dependência sem licença ou não aprovada | Pacote fora da tabela de stack tecnológica, licença comercial | Risco jurídico e de cadeia de suprimentos |

Não são bloqueios (comente como `nit:` ou `suggestion:`): preferências de nomenclatura dentro das convenções, designs alternativos de qualidade equivalente, formatação aceita pelas ferramentas.

## Fluxo de revisão

Primeira revisão em até 4 horas úteis, merge em até 1 dia útil; PRs pequenos tornam isso possível.

1. **Autorrevisão do autor primeiro.** Leia o diff na visualização do PR, marque os itens do template e garanta que o CI esteja verde antes de pedir revisão. PRs gerados por IA seguem a mesma regra: a pessoa que abre o PR é a responsável por ele.
2. **Ordem do revisor:** execute ou leia os testes primeiro, depois o domínio e os contratos, depois a infraestrutura e, por fim, o estilo. Avalie a mudança em relação à sua etapa ou issue, não a preferências pessoais.
3. **Rótulos de comentário:** `blocker:` (correção obrigatória, conforme a tabela acima), `question:`, `suggestion:`, `nit:`. Apenas `blocker:` impede a aprovação.
4. **Tom:** comente sobre o código, não sobre a pessoa; explique o porquê e proponha uma alternativa; elogie boas soluções também.
5. **Resolução:** o autor resolve as threads que corrigiu; o revisor resolve as perguntas respondidas; divergências que passem de duas rodadas vão para uma chamada de 10 minutos, e o resultado é registrado na thread.
6. **Revisões entre stacks:** um engenheiro de back-end pode aprovar PRs de front-end apenas nos aspectos de contrato de API e segurança, e vice-versa; o responsável pelo checklist da stack dá a segunda aprovação quando o CODEOWNERS a exige.
7. **Após o merge:** o autor acompanha o `cd` para staging e o smoke test; uma falha é responsabilidade do autor, que deve corrigi-la ou reverter a mudança em até uma hora.

## Checklists por stack

Use o checklist de cada stack que o PR altera, além dos critérios de aceite.

**.NET (C#)**

- [ ] `sealed` por padrão, records para DTOs e value objects, sem setters públicos em agregados
- [ ] `async` de ponta a ponta com `CancellationToken`; sem `.Result`, `.Wait()` ou `async void`
- [ ] Dinheiro como `decimal` via `Money`; tempo via `TimeProvider`; sem estado estático mutável
- [ ] EF Core: `AsNoTracking` para leituras, projeções em vez de carregar grafos, sem consultas dentro de loops, `FromSql` apenas com interpolação
- [ ] Endpoints retornam `TypedResults`, validam a entrada, mapeiam erros para ProblemDetails e declaram autorização
- [ ] Comandos e consultas são `public sealed record`s com um validador FluentValidation; sem verificações manuais de campos obrigatórios ou de formato ([regras de design](02-execution-guide.md#regras-de-design-para-todas-as-etapas))
- [ ] Mapeamento por classes `[Mapper]` do Mapperly; sem cópias manuais membro a membro; sem AutoMapper
- [ ] Logging por `LoggerMessage` gerado por source generator; sem interpolação de strings nas chamadas de log

**Front-end TypeScript**

- [ ] Tipos `strict`, sem `any`, sem non-null assertions sem um comentário
- [ ] Todo HTTP passa por `shared/api`; sem `fetch` em `ui/`
- [ ] DOM construído com `textContent` ou templates, nunca `innerHTML` com dados
- [ ] Todo controle tem rótulo, o foco é visível, a navegação por teclado funciona, `aria-live` para atualizações assíncronas
- [ ] Utilitários do Bootstrap em vez de CSS customizado; sem estilos inline

**SQL e MongoDB**

- [ ] Sempre parametrizado; sem consultas ou filtros montados por concatenação de strings
- [ ] Toda nova consulta tem um índice que a suporte; `EXPLAIN` anexado para consultas de listagem ou busca
- [ ] Migrações apenas de expansão, idempotentes, revisadas como script SQL
- [ ] Filtros do Mongo via `Builders<T>`; escritas que envolvem dois documentos usam uma transação de sessão

**Infraestrutura (Docker, compose, GitHub Actions, shell)**

- [ ] Imagens executadas sem root, tags de imagem base fixadas, multi-stage, sem segredos nas camadas
- [ ] Actions fixadas em SHAs de commit, `permissions` de menor privilégio, sem `pull_request_target` com checkout do código do PR
- [ ] Scripts shell com `set -euo pipefail`, finais de linha LF, aprovados no `shellcheck`
- [ ] Healthchecks no compose para todo serviço; configuração por variáveis de ambiente documentadas em `.env.example`

## Artefatos e pipelines

Faça o build uma vez, promova os mesmos bytes e nunca edite um pipeline ou artefato manualmente. Arquivos de pipeline são código: passam por PRs com duas aprovações.

| Artefato | Produzido por | Identidade | Armazenado em | Retenção | Regra |
| --- | --- | --- | --- | --- | --- |
| Imagens de contêiner | Job `images` do `cd.yml` na `main` | `sha-<commit>`, depois `vX.Y.Z` na release | GHCR | Releases para sempre; `sha-*` por 90 dias | Deploy por digest; nunca reconstruídas para uma release; devem incluir atestado de proveniência e SBOM |
| Bundle de migração / imagem do migrator | `cd.yml` | Mesma tag de commit dos serviços | GHCR | Igual às imagens | Executa antes dos serviços; apenas de expansão |
| Relatórios de testes e cobertura | `ci.yml` | Id da execução | Artefatos do Actions | 14 dias | Lidos na revisão; nunca commitados. O .NET ainda não gera relatório de cobertura e o do front-end não é publicado como artefato |
| Documentos OpenAPI | Build | Arquivo no repositório | Git (`*/openapi/*.json`) | Histórico do Git | Gerados e commitados; divergência faz o CI falhar |
| Relatórios de mutação e de carga | `mutation.yml` (não criado, C57), perfil de carga | Id da execução / data | Artefatos do Actions; `docs/perf/` guarda hoje só os planos `EXPLAIN` | 30 dias | Tendências revisadas mensalmente |
| Notas de release | Tag | `vX.Y.Z` | GitHub Releases | Para sempre | Geradas a partir dos Conventional Commits, editadas pelo Tech Lead; ainda não há release nem automação de notas |

Regras de pipeline:

- A `main` está sempre pronta para release; uma `main` vermelha interrompe os merges até ser corrigida ou revertida (reverta primeiro, investigue depois).
- As versões seguem o SemVer: `feat` → minor, `fix` → patch, `!` ou `BREAKING CHANGE` → major.
- Todo deploy, seja em staging ou em produção, passa pelo `deploy-gate`; produção também exige uma aprovação de ambiente por alguém que não seja o autor da tag.
- Segredos ficam apenas em GitHub Environments; workflows recebem apenas as `permissions` mínimas de que precisam; forks nunca recebem segredos.
- Política de testes flaky: quarentena por meio de uma issue em até 24 h, correção em até uma semana; testes em quarentena continuam sendo executados e reportando resultados.
- PRs do Dependabot são revisados como qualquer PR; atualizações de imagem base passam por staging antes de uma release.
- Rollback = refazer o deploy dos digests da release anterior com `scripts/deploy.sh <staging|production> --rollback`; rollback de banco de dados nunca é necessário porque as migrações são retrocompatíveis.
