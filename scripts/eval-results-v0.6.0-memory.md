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

---

# Parte 2 — Semantic Memory

Esta seção registra o estado posterior ao relatório histórico da Parte 1 acima. A versão continua **v0.6.0 — “Yeah, I know.”** na mesma branch.

## Implementação

`OpenAiMemoryEmbeddingClient` chama diretamente `/v1/embeddings` com `text-embedding-3-small` e 1.536 dimensões por padrão. Só o texto da memória, ou a consulta de busca, é enviado ao provider externo; não há chamada conversacional adicional nem vetor em PostgreSQL. `QdrantMemoryVectorStore` cria/verifica uma collection cosine, usa `MemoryRecord.Id` como point ID e payload mínimo (`memoryId`, `revision`, `contentHash`, `model`, `dimensions`). O teste físico executou criação, upsert repetido, leitura, busca, delete repetido e incompatibilidade de dimensão numa collection temporária do servidor Qdrant v1.12.6; a collection de teste foi removida. Nenhuma collection de produção foi apagada.

`MemorySemanticProjectionWorker` consome exclusivamente jobs Semantic. O claim PostgreSQL usa `FOR UPDATE SKIP LOCKED`, lease de dois minutos e `LeaseId`; um crash permite novo claim após expiração. Até cinco backoffs (10s, 30s, 2min, 5min, 30min) precedem `Failed`. `LastError` guarda apenas categoria técnica. O processador bloqueia o agregado e relê `MemoryRecord` sob row lock antes de reconciliar o estado atual, independentemente da operação histórica do job. Upsert de job obsoleto não ressuscita uma memória forgotten/superseded. Pontos já compatíveis dispensam embedding. A collection é verificada em cada ciclo; na inicialização e após recriação, jobs correntes de memórias Active são reabertos para reconstrução. Jobs Graph não são reclamados ou alterados.

`memory_search` agora usa embedding da query, candidatos Qdrant com overfetch, threshold 0,45, carga em lote e filtragem canônica PostgreSQL de status/validade, preservando a ordem de similaridade. Match textual complementa resultados durante eventual lag. Falhas técnicas e desabilitação usam fallback textual. Resultados mantêm as referências temporárias da Parte 1 para update/forget. Nenhuma memória é injetada automaticamente no prompt. As métricas novas contam requisições/falhas/tokens de embedding, projeções concluídas/falhas/retries/lag e busca semântica/fallback/candidatos/resultados/duração, sem texto ou identificadores.

## Validação da Parte 2

Executada em 28/09/2026, com PostgreSQL descartável por execução e Qdrant local v1.12.6 usando collection aleatória de teste:

| Verificação | Resultado |
| --- | --- |
| `dotnet test backend/Aegis.sln` | **336/336**, sem skips, incluindo integração PostgreSQL de Memory/Reminder e integração física Qdrant |
| `dotnet build backend/Aegis.sln --configuration Release` | passou; permanece apenas o aviso xUnit2031 preexistente em `CalendarTests.cs` |
| `npm test --prefix frontend/aegis-pwa` | **41/41** |
| `npm run build --prefix frontend/aegis-pwa` | passou |
| `docker compose config --quiet` e `git diff --check` | passaram |
| `dotnet ef migrations has-pending-model-changes` | nenhuma mudança pendente; **nenhuma migration nova** |

Os novos testes determinísticos cobrem request e vetor da Embeddings API sem rede real, sinônimos, decisão arquitetural, irrelevância/threshold, ordem, overfetch, temporalidade, pontos forgotten/superseded obsoletos, fallback, escrita/correção/esquecimento com Qdrant indisponível, crash/lease, claim concorrente, retry terminal, Graph Pending, job rev2 Delete antes de rev1 Upsert reaberto e rebuild após perda simulada da collection. Também passou uma integração ponta a ponta com PostgreSQL descartável, worker, embeddings falsos e Qdrant físico: projeção, ponto esquecido filtrado e reconstrução após remoção da collection. Nenhum teste normal chama a Embeddings API real.

Os evals live de intenção usaram o catálogo real de 32 tools, respostas simuladas para integração e nenhuma operação Google/Web Push. A primeira rodada ampliada obteve **27/28**: o único erro foi um fixture antigo que simulava apenas substring textual e, portanto, não devolvia a memória da GPU após uma consulta indireta. O fixture passou a simular poucos candidatos semânticos determinísticos; a repetição focada obteve **7/7** para `memory_search` e `memory_forget_lookup`. Os demais casos da rodada ampliada passaram: pedidos explícitos, declarações casuais, referências observadas, colisões RAM/cache/Qdrant/grafo e uma amostra de Gmail/Calendar/Reminder. A suíte live completa não foi reexecutada. Evals testam escolha da tool; qualidade de retrieval fica nos testes determinísticos e no Qdrant físico.

## Limitações e sequência

Indexação é assíncrona; um novo fato pode aparecer primeiro apenas na busca textual. O default 0,45 requer calibração com memória real; não é confiança factual. Rebuild reabre jobs de todas as memórias Active na inicialização, com leitura completa desses registros, adequado ao volume atual single-user mas passível de paginação se crescer. Alterar modelo/dimensão exige nova collection e rebuild explícito; não há migração blue/green. A correção apenas de validade da Parte 1 ainda exige reformular o texto. A regra de unicidade de relação Active cuja validade acabou será revisada na Parte 3. Neo4j, resolução de entidades, projeção/retrieval de Graph e semântica temporal de relações ficam para a **Parte 3**. Escrita automática, extração de conversas, retrieval semântico + grafo, proatividade e hardening ficam para a Parte 4.
