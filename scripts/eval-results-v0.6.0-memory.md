# Validação — Aegis v0.6.0 “Yeah, I know.”, Parte 1: Memory Foundation

Branch `feat/v0.6.0-memory`, criada da `main` atualizada em `93338f6b86dac5aa322facb3be64e4fb321e3ee1` (v0.5.1). Esta branch não foi mesclada na `main`.

## Implementação aplicada

PostgreSQL é a fonte canônica. `MemoryRecord` tem conteúdo, hash normalizado, status, validade opcional, revision e histórico de supersession/forget. `MemoryEvidence` registra múltiplas provenances sem ownership da conversa; suas FKs de conversa e mensagem usam `SET NULL`. Entidades, aliases, relações e evidências de relações também são canônicos no PostgreSQL. `MemoryProjectionJob` persiste `Semantic` e `Graph`, `Upsert` e `Delete`, com unicidade por target/agregado/revision/operação. Jobs ficam `Pending`; não há consumers nesta parte. A migration única é `20260928081556_AddMemoryFoundation`.

`memory_remember`, `memory_search`, `memory_update` e `memory_forget` usam `MemoryService` e `MemoryStore`. A escrita explícita e o job são atômicos. Busca é textual no PostgreSQL, limitada a 30 resultados e apenas para memórias vigentes. Referências observadas expiram em 30 minutos. Update cria novo registro e substitui o antigo; forget é soft e idempotente. `TimeProvider` governa o novo fluxo temporal. Métricas não contêm conteúdo, consulta, nomes nem IDs; o audit de execução não duplica payloads das tools Memory.

## Validação automatizada

Executada em 28/09/2026:

| Verificação | Resultado |
| --- | --- |
| `dotnet test backend/Aegis.sln` | **329/329**; inclui **4/4 cenários Memory PostgreSQL** e **2/2 Reminder PostgreSQL**, sem skips, com banco descartável |
| `dotnet build backend/Aegis.sln --configuration Release` | passou; um aviso xUnit2031 preexistente em `CalendarTests.cs` |
| `npm test --prefix frontend/aegis-pwa` | **41/41** |
| `npm run build --prefix frontend/aegis-pwa` | passou, incluindo typechecks e PWA manifest v0.6.0 |
| `docker compose config --quiet` | passou |
| `dotnet ef migrations has-pending-model-changes` | nenhuma mudança pendente |
| `git diff --check` | passou |

Os testes PostgreSQL usam schemas isolados em bancos descartáveis criados apenas para validação. Cobrem os contratos JSON das quatro tools, deduplicação/replay, evidências distintas, concorrência de escrita exata, rollback sem memória nem job, TTL de referências, busca limitada e vigente, supersession, forget repetido, jobs pendentes e revision, exclusão física de conversa preservando memória/evidência com FKs anuladas, aliases e relação canônica com duas memórias de apoio. O banco de produção não foi usado.

## Evals de intenção

`scripts/eval_tool_intent.py` exportou o catálogo real de **32 tools**. Rodadas live com `gpt-5.6-luna`, reasoning `medium`, resultados simulados para as tools e sem executar ações Google ou Web Push:

- **Memory: 18/18.** Inclui pedidos explícitos, declarações casuais, busca, correção/esquecimento com e sem referência, segundo item observado, rejeição de esquecimento amplo e colisões com RAM, cache, memória virtual, Qdrant e grafos.
- **Regressão representativa das integrações anteriores: 45/45.** Inclui controles sem tool, Gmail, Calendar, séries recorrentes, Reminder e proatividade após leitura de email.

O fixture inicial de busca Memory retornava um resultado que não corresponderia ao texto consultado; foi corrigido para usar substring canônica antes da rodada final. A suíte inteira de 174 evals não foi reexecutada; a amostra acima cobre as principais colisões de intenção introduzidas por Memory.

## Limites desta etapa

Busca textual pode não localizar uma lembrança por sinônimo ou termo ausente do conteúdo. Correção que altera apenas a validade temporal de texto idêntico ainda exige reformular o conteúdo; repetir sem datas uma memória temporal expirada retorna conflito, para não afirmar que foi guardada como vigente. Ainda não há embeddings, cliente Qdrant, Neo4j, entity resolution semântico, retrieval automático, escrita automática, extração de conversas, worker de projeção ou políticas de memória proativa. A Parte 2 implementará embeddings, projeção Qdrant dos jobs Semantic pendentes e recuperação semântica, preservando PostgreSQL como fonte de verdade.
