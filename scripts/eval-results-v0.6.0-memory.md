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

## Correção de convergência da projeção Semantic

`RequeueCurrentStateAsync` substitui `RequeueActiveAsync`. Na inicialização e após recriar a collection, escolhe para **cada** `MemoryRecord` somente o job da revision atual: Active → Upsert; Forgotten/Superseded → Delete. Reabre os jobs atuais Completed e Failed, reutiliza Pending e respeita Processing com lease válida. Jobs históricos não são reproduzidos cegamente; jobs Graph permanecem inalterados. Isso repara Delete terminalmente falho depois que o Qdrant volta sem perder a collection e também corrige um point que reapareça após um Delete já Completed. Repetir a reconciliation três vezes não cria jobs ou points duplicados.

Validação da correção em 28/09/2026: **339/339** testes backend, sem skips, com PostgreSQL descartável e integração Qdrant física em collection temporária; **11/11** cenários PostgreSQL Memory passaram, incluindo Failed Delete Forgotten, Failed Delete Superseded, Completed Delete com stale point reintroduzido, rebuild Active, idempotência e Graph intacto. Build Release backend, **41/41** testes frontend, build PWA, Compose, `git diff --check` e EF `has-pending-model-changes` passaram. Nenhuma migration foi criada. Evals live e fixtures não foram alterados ou executados nesta correção interna. Os testes de embedding continuaram usando fake; nenhuma chamada real à Embeddings API foi feita.

---

# Parte 3 — Knowledge Graph

A versão permanece **Aegis v0.6.0 — “Yeah, I know.”**, na mesma branch. O relatório das Partes 1 e 2 acima é histórico e permanece preservado.

## Arquitetura aplicada

PostgreSQL continua sendo a única fonte de verdade. A migration `20260928153830_RefineMemoryRelationsForKnowledgeGraph` substitui apenas o índice único de relações Active por um índice de lookup não único; as sete tabelas canônicas permanecem. Operações internas criam/reutilizam relações exatas, rejeitam sobreposição de intervalos `[ValidFrom, ValidUntil)`, preservam relações históricas Active fora do instante atual e fazem supersession/forget com jobs Graph atômicos. Entidades têm resolução determinística por nome canônico e alias, com filtro opcional de tipo, entidades retired ignoradas por default e resultado Ambiguous sem adivinhação. Não há extração por LLM nem tools Graph públicas.

Neo4j Community `5.26.30-community` usa o driver oficial `Neo4j.Driver` 5.28.4. Um node `:AegisMemoryEntity` por `MemoryEntity.Id` recebe nome, nome normalizado, tipo, aliases, revision e retiredAt. Uma edge fixa `:AEGIS_RELATION` por `MemoryRelation.Id` recebe predicate, revision e validade; IDs canônicos possuem constraints unique. O grafo não recebe texto de `MemoryRecord`, evidências ou IDs de conversa/mensagem. `MemoryGraphProjectionWorker` consome apenas jobs Graph por claim PostgreSQL `FOR UPDATE SKIP LOCKED`, lease recuperável de dois minutos e cinco backoffs até Failed, com códigos técnicos sanitizados. Cada job relê o estado canônico atual antes de escrever; Active relation faz upsert, Superseded/Forgotten fazem delete. Entity e alias atuais são relidos. Relation upsert garante os endpoint nodes independentemente da ordem dos jobs.

Startup reabre somente os jobs da revision/operação desejadas agora, inclusive Completed/Failed, sem tocar no histórico ou nos jobs Semantic. `MemoryGraphRebuild` remove somente nodes Aegis e suas edges e reenfileira o estado atual. Traversal interna limita direção, profundidade 1–3, até 50 resultados, predicates normalizados e `asOf`; Neo4j filtra todas as edges do path por tempo, e PostgreSQL valida em lote status, validade, endpoints e nomes. `memory_search` segue apenas Semantic/Qdrant mais fallback textual. Nenhuma escrita automática, retrieval híbrido, nova tool, prompt ou eval live foi introduzido.

## Validação da Parte 3

Executada em 28/09/2026 com PostgreSQL, Qdrant v1.12.6 e Neo4j Community 5.26.30 em containers **descartáveis**. Os testes Graph físicos exigem `AEGIS_MEMORY_TEST_NEO4J_DISPOSABLE=YES_DELETE_AEGIS_PROJECTION`; só removem nodes `:AegisMemoryEntity` e suas relações, deixando um node externo de controle intacto durante o rebuild.

| Verificação | Resultado |
| --- | --- |
| `dotnet test backend/Aegis.sln` | **345/345**, zero skips, incluindo integração PostgreSQL, Qdrant físico e Neo4j físico |
| `dotnet build backend/Aegis.sln --configuration Release` | passou; apenas aviso xUnit2031 preexistente em `CalendarTests.cs` |
| `npm test --prefix frontend/aegis-pwa` | **41/41** |
| `npm run build --prefix frontend/aegis-pwa` | passou |
| `docker compose config --quiet`, `git diff --check` | passaram |
| `dotnet ef migrations has-pending-model-changes` | nenhuma mudança pendente após migration nova |

Os testes novos cobrem canonical/alias/type/retired/ambiguous resolution, intervalos separados, conflito temporal, replay exato, supersession e forget de relação, aliases projetados no mesmo node, traversal de um e dois saltos, `asOf`, stale edge rejeitada no PostgreSQL, recuperação de lease, Failed Delete, drift após Completed Delete, job Upsert antigo processado depois do Delete, reconstrução real de Neo4j e preservação de jobs Semantic. Nenhuma chamada OpenAI ou embedding real foi feita. Evals live não foram executados porque tool schema e identity prompt permanecem inalterados.

## Limitações e Parte 4

`memory_search` ainda não navega no grafo; traversal é interna. A escrita de entidades e relações ainda depende de serviços internos, sem extração automática. O controle de sobreposição temporal usa a transação serializada da camada Memory; escritas diretas fora desse serviço devem respeitar a mesma regra. O rebuild percorre os agregados canônicos atuais de forma integral, adequado ao volume single-user atual; pode exigir paginação em escala maior. A Parte 4 fica responsável por escrita automática, extração de entidades/relações, resolução de contradições, retrieval Semantic + Graph, uso contextual/proativo e hardening final.

## Correção pontual — ambiguidade entre nome canônico e alias

`MemoryEntityResolver` agora une matches exatos normalizados de `CanonicalName` e `MemoryEntityAlias` antes de aplicar tipo e filtro de retired, deduplicar por `MemoryEntity.Id`, ordenar e classificar o resultado. Um nome canônico de uma entidade que também é alias de outra passa a ser `Ambiguous`; `ResolveOrCreateAsync` lança `memory_entity_ambiguous` sem criar entidade ou Graph job. Nome e alias da mesma entidade continuam resolvendo para um único candidato. Não houve migration, prompt/tool schema novo, LLM, embeddings ou eval live.

Validação em 28/09/2026: **344 testes backend passaram, 0 falharam, 4 integrações físicas Qdrant/Neo4j foram ignoradas** por não terem URLs descartáveis configuradas; os testes PostgreSQL rodaram com banco descartável, inclusive os três cenários novos de colisão, deduplicação, tipo/retired e ausência de escrita em ambiguidade. Build backend Release, **41/41** testes frontend, build PWA, Compose e `git diff --check` passaram. EF `has-pending-model-changes` confirmou nenhuma mudança pendente. As integrações físicas Qdrant/Neo4j da Parte 3 permanecem documentadas acima e não foram repetidas para esta correção de resolução PostgreSQL.

# Parte 4 — Intelligent Memory

## Implementação

Migration `20260928173045_AddIntelligentMemory`: cria `memory_extraction_jobs` com FK nullable/`SET NULL` para conversa e mensagem, unicidade parcial por `UserMessageId`, claim/lease/retry/counters/status e constraints; troca o índice único Active de `MemoryRecord.ContentHash` por índice de lookup não único. Assim o mesmo fato pode reaparecer em intervalos não sobrepostos. `MemoryRecord.CloseValidity` e `MemoryRelation.CloseValidity` preservam fatos historicamente verdadeiros, incrementam revision e criam jobs Upsert. Correção factual continua Superseded/Delete; esquecimento continua Forgotten/Delete. A invalidação da única memória que sustenta uma relação também fecha/esquece a relação, preservando evidência e histórico canônicos.

`ChatService` salva a mensagem do usuário e a intenção de extração no mesmo `SaveChanges`. O worker faz claim PostgreSQL com `FOR UPDATE SKIP LOCKED`, lease de dois minutos, até seis tentativas com backoff, e chama `OpenAiMemoryExtractionClient` sem transação aberta. Fonte deletada/ausente/não usuário completa sem criar memória. O extractor usa Responses `store=false`, Structured Outputs `json_schema` estrito e política cacheável; recebe no máximo seis mensagens anteriores curtas, memórias/relações relevantes com refs efêmeras e um catálogo pequeno de predicates. Nenhum raw JSON é persistido/auditado. `MemoryAutomaticIngestionService` valida limites e refs, aplica até oito candidatos em transações individuais, usa `UserStatement` evidence, resolve entidades por canonical+alias com ambiguidade segura, aceita aliases apenas declarados no alvo e conecta relações a `MemoryRelationEvidence`. Chaves de autenticação óbvias são recusadas também em tools explícitas. Não há inferência persistente nem ações externas automáticas.

`MemoryHybridRetriever` combina Qdrant, busca textual PostgreSQL e caminhos Neo4j iniciados por menções exatas ou relações apoiadas pelas memórias semânticas. Graph roda apenas com seeds e limites; PostgreSQL valida status, temporalidade e suporte de evidência. `memory_search` aceita `asOf` RFC3339 e observa apenas resultados explícitos. O contexto automático usa threshold inicial 0,60, até cinco memórias/oito paths, 3.500 caracteres e deadline 2,5 segundos; falhas de índices derivados degradam para lexical. O contexto fica depois da mensagem atual, em item de menor confiança que a identidade confiável. Ele não cria referências invisíveis. Payload real do chat inclui os dados necessários; `RuntimeContextSnapshot` e `LlmRequestAudit.RequestPayloadJson` usam representação redigida, incluindo outputs de `memory_*` em rodadas seguintes. Reconciliação Semantic e Graph periódica: default 30 minutos, desabilitável com zero.

## Validação da Parte 4

Em 28/09/2026, usados **containers descartáveis**, separados da stack Aegis já em execução: PostgreSQL `16-alpine`, Qdrant `v1.12.6` e Neo4j Community `5.26.30-community`. Não houve chamada real de embedding no teste de ponta a ponta; `FakeMemoryEmbeddingClient` determinístico foi usado com Qdrant/Neo4j físicos. O teste físico cobre mensagem + job no mesmo commit, fake structured extraction, memórias/evidence/relations, projeção Qdrant/Neo4j, recuperação híbrida em nova conversa, contexto de prompt e reparo de drift sem restart. Integração física Qdrant e Neo4j herdada também foi executada isoladamente. O ambiente de teste Neo4j é explicitamente descartável; cleanup remove somente a projeção Aegis.

Testes novos cobrem replay idempotente, validade temporal de fatos recorrentes, correção vs transição, relação histórica vs fato errado, ambiguidade canonical/alias, refs inválidas, segredo explícito, modalidade de compra, alias declarado, job/lease e fonte deletada, isolamento de transações entre candidates quando um falha, seed semântico e por menção exata, degradação e timeout, ausência de refs invisíveis, Structured Outputs/store=false/cache, separação de payload real/auditado e redaction de outputs `memory_*`. `scripts/eval_memory_extraction.py` foi adicionado como avaliação live read-only e exige opt-in `AEGIS_MEMORY_TEST_OPENAI=YES`; este opt-in não estava habilitado, portanto a avaliação live de extração **não foi executada**. A política/modelo de extração foi validada por teste HTTP fake determinístico, não por chamada real ao modelo.

### Resultados finais — 28/09/2026

| Verificação | Resultado |
| --- | --- |
| Backend com PostgreSQL descartável | **356 aprovados, 0 falhas, 7 skips** antes do teste adicional de isolamento; os skips eram os cinco testes físicos Qdrant/Neo4j e dois testes Reminder sem `AEGIS_REMINDER_TEST_DATABASE` |
| PostgreSQL + Reminder descartáveis | **359 aprovados, 0 falhas, 5 skips físicos** na execução final após o teste adicional; Qdrant/Neo4j permanecem condicionais na execução geral para evitar limpeza concorrente da mesma projeção Neo4j. Todos os cinco foram executados isoladamente abaixo |
| Qdrant físico | **2/2** testes executados isoladamente: operações/schema da collection e projeção/rebuild canônico |
| Neo4j físico | **2/2** testes executados isoladamente: traversal/rebuild temporal e projeção PostgreSQL/rebuild/canonical filtering |
| Ponta a ponta física | **1/1**: job durável + fake extractor/embedding + PostgreSQL/Qdrant/Neo4j reais + contexto em nova conversa + reparo de drift |
| Backend Release | Build passou; apenas aviso preexistente xUnit2031 em `CalendarTests.cs` |
| Frontend | **41/41** testes; build PWA passou |
| Compose / diff / EF | `docker compose config --quiet`, `git diff --check` e `dotnet ef migrations has-pending-model-changes` passaram; zero mudanças pendentes no modelo |
| Eval de intenção focado | 22/23 na primeira passagem; reteste focado de referência não observada 1/1. A execução completa abaixo fornece a medição final sem repetir a suíte para melhorar a porcentagem |
| Eval live completo de intenção | **178/179** em uma execução. Único desvio: para `Eu prefiro backend.`, nenhuma tool de memória foi chamada (routing correto), mas a resposta disse `Anotado: você prefere backend.` em vez de responder sem sugerir anotação. Não houve escrita explícita pela tool |
| Eval live de extração | **Não executado**: `AEGIS_MEMORY_TEST_OPENAI=YES` não estava configurado. Cliente/schema/política foram exercitados por HTTP fake; comportamento probabilístico do extractor real ainda requer avaliação opt-in |

Versões físicas consultadas: PostgreSQL **16.14**, Qdrant **1.12.6**, Neo4j Community **5.26.30**. Os testes usaram schema/collections temporários e uma instância Neo4j descartável; nenhum banco de produção foi usado. As cinco integrações físicas condicionais foram executadas uma a uma para impedir que dois testes apaguem simultaneamente os nodes `:AegisMemoryEntity` da mesma instância de teste.

O caso live de `Anotado` é variância de resposta do modelo, apesar da regra explícita no identity prompt; deve ser observado na aceitação manual. O checklist manual/deployed A–E do README **não foi executado** e não é apresentado como validação física real de conversas em produção. A branch aguarda aceitação física e o eval live de extração opt-in antes de qualquer decisão de merge.

## Final pre-acceptance hardening — 28/09/2026

### Explicit forget e extração concorrente

`memory_forget` agora recebe o `ToolExecutionContext` completo. No mesmo commit PostgreSQL que marca o `MemoryRecord` como `Forgotten`, cria os Delete jobs e remove relações exclusivamente sustentadas, a operação marca o `MemoryExtractionJob` da mesma `UserMessageId` como `Suppressed`. A migration `20260928234638_SuppressMemoryExtractionAfterForget` apenas amplia a constraint de status; não altera as tabelas canônicas. Supressão revoga lease/retry e preserva os contadores já registrados, sem guardar o conteúdo esquecido no job.

Todo candidate do worker verifica dentro de `MemoryStore.WriteAsync`, sob o advisory lock canônico e lock do job, que o status ainda é `Processing` e que `LeaseId` e `UserMessageId` continuam correspondendo ao claim. Assim, uma resposta tardia do extractor não grava depois do forget; `CompleteAsync`/`FailAsync` também não sobrescrevem `Suppressed`. Se o reforço automático entra primeiro, o forget posterior vence e cria Semantic Delete e Graph Delete quando a relação depende exclusivamente da memória. `memory_remember` e `memory_update` não suprimem jobs. A policy do extractor foi ampliada para não transformar o objeto de pedidos de forget em fatos novos, sem confundir “tinha esquecido de comentar” com comando de forget.

### Privacidade do tool loop

Nas duas rotas do `AegisToolLoop`, uma rodada cujo response contenha qualquer chamada `memory_*` persiste `[memory_response_redacted]` em seu `ResponseBody` combinado. O modelo e a tool continuam recebendo os argumentos e outputs reais; as listas `AuditInputItems` e os outputs redigidos das rodadas seguintes permanecem separados. Rodadas sem memória mantêm os detalhes de auditoria existentes. Testes com `ULTRA_PRIVATE_MEMORY_MARKER_123` cobrem `memory_remember` e `memory_search`, com e sem streaming, no primeiro tool call; o marcador chega ao fluxo real e não aparece em `RequestPayloadJson` nem `ResponseBody` combinados.

### Validação deste hardening

| Verificação | Resultado |
| --- | --- |
| Backend com PostgreSQL/Reminder descartáveis | **368 aprovados, 0 falhas, 5 skips físicos condicionais**; os cinco foram executados separadamente abaixo |
| Corrida e histórico | Pending job suprimido de forma idempotente e nunca reclamado; claim + extractor fake pausado + explicit forget + resposta tardia: job `Suppressed`, fato `Forgotten`, zero novo Active/evidence. Ordem inversa: forget vence, Semantic Delete e Graph Delete; stale edge ausente em busca atual e histórica `asOf` |
| Auditoria | `memory_remember` e `memory_search` no primeiro call, streaming e não streaming; argumentos redigidos. Resposta de tool não Memory permanece auditável |
| Qdrant físico | **2/2** testes em collection temporária |
| Neo4j físico | **2/2** testes em instância descartável |
| Ponta a ponta física | **1/1**, fake extractor/embedding com PostgreSQL, Qdrant e Neo4j reais |
| Backend Release | Passou; só o aviso preexistente xUnit2031 em `CalendarTests.cs` |
| Frontend | **41/41** testes e build PWA passaram |
| Compose, diff, EF | `docker compose config --quiet`, `git diff --check` e `has-pending-model-changes` passaram; zero mudanças pendentes |
| Eval live de extração, rodada única | **16/16** com `store=false`; inclui quatro formulações de forget (todas zero candidates), “esquecido de comentar” (um candidate) e “esqueci o nome” (zero). Usage numérico: 12.359 input tokens, 1.795 output tokens, 0 cached tokens reportados |
| Eval focado de intenção Memory | **23/23** após ajuste mínimo do identity prompt; suíte completa de 179 casos não foi repetida |
| “Eu prefiro backend.” | Antes do ajuste: **2/3**, com uma resposta `Anotado`; depois de proibir confirmações de gravação sem pedido explícito: **3/3**, nenhuma memory tool |

As integrações físicas foram executadas sequencialmente para evitar que dois testes limpem a mesma projeção Neo4j. Nenhuma aceitação manual/deployed A–E foi inventada ou executada nesta etapa. O eval live da extração testa intenção do modelo e formato de saída; a garantia contra ressurreição após forget vem da transação PostgreSQL e da verificação do job antes de cada candidate.

## Memory Activity UX — 29/09/2026

A migration `20260929063414_AddMemoryActivityFeedback` adiciona `memory_activity_events` com Kind, Source, TargetType/TargetId, vínculo opcional de conversa e mensagem de usuário, horário e chave SHA-256 determinística de deduplicação. O índice único e `ON CONFLICT DO NOTHING` tornam retries idempotentes; o evento não contém texto da memória. `Used` guarda apenas IDs presentes nas linhas efetivamente enviadas ao modelo e é confirmado na mesma transação da resposta. `Consulted` vem dos resultados entregues por `memory_search`. Escritas explícitas e candidates automáticos `create`, `correct` e `transition` registram eventos na transação canônica; `reinforce` não cria atividade visual. `memory_forget` registra `Deleted` e mantém a supressão da extração.

O snapshot resolve os textos diretamente de PostgreSQL e usa `LlmRequestAudit.UserMessageId`/`AssistantMessageId` para associar eventos ao turno. O histórico carrega os snapshots em lote; `done` e a resposta sem streaming incluem o snapshot atual. Um endpoint por mensagem atualiza o resultado da extração assíncrona; o frontend consulta aproximadamente a cada segundo, no máximo por 20 segundos, e cancela ao trocar de conversa ou desmontar a tela. O disclosure nativo `<details>` começa fechado, é neutro, sem card/badge/emoji e mostra até três fatos por seção, com total restante. Os títulos são `Usou memória`, `Consultou memória`, `Guardou memória`, `Atualizou memória` e `Apagou memória`; múltiplos tipos usam `Memória`. O status temporário de `memory_forget` usa `Apagando memória…`/`Memória apagada`.

| Verificação | Resultado |
| --- | --- |
| Backend `dotnet test backend/Aegis.sln` | **374 aprovados, 0 falhas, 5 skips físicos condicionais**; PostgreSQL Memory e Reminder executados no banco descartável. |
| PostgreSQL integration | Cinco cenários novos com banco descartável e schemas isolados: restore/explicit tools, atomicidade/retry/reinforce, pending/zero/failed/suppressed, extraction após `done` e limite real do contexto automático. |
| Frontend tests | **47/47**; apresentação, limite de três, marcação nativa acessível, polling pending → completed, cancelamento e timeout. |
| Frontend build | passou, incluindo typecheck e PWA. |
| Backend Release | passou; somente aviso xUnit2031 preexistente em `CalendarTests.cs`. |
| Compose, diff, EF | `docker compose config --quiet`, `git diff --check` e `has-pending-model-changes` passaram; nenhuma alteração pendente no modelo. |
| Eval focado Memory | **23/23** antes do ajuste adicional de vocabulário; **6/6** nos casos de forget e declarações casuais após o ajuste. A suíte live completa não foi repetida. |

Aceitação manual visual **não executada** neste relatório. Roteiro mínimo: com `Meu time favorito de R6 é a FaZe Clan.` já persistido, perguntar `Qual meu time favorito de R6?` e verificar `Usou memória` com o fato ao expandir; enviar `Minha cerveja favorita é Heineken.` e verificar `Guardou memória` após a extração; consultar um fato e pedir `Apaga essa informação.`, verificando `Apagou memória`, nunca `Esqueceu memória`. Esses são resultados esperados, não observações de uma sessão manual.

## Manual Acceptance Failure & Reliability Hardening — 29/09/2026

### Reprovação observada e causa comprovada

A aceitação manual posterior à seção anterior **reprovou** a v0.6.0. A pergunta sobre o segundo time após a FaZe mostrou monitor e Aegis em Memory Activity. Declarações factuais receberam reformulações tautológicas. A correção de monitor apareceu correta na conversa corrente, mas 4K/144 voltou em conversas novas. O fato PostgreSQL/Aegis e a RAM persistidos não foram recuperados.

Inspeção somente de leitura do PostgreSQL da sessão original confirmou: a memória Aegis/PostgreSQL estava `Active`; 16 GB e 32 GB estavam ambos `Active` sem intervalos temporais; 4K/144 e QHD/180 estavam ambos `Active`, sem `SupersededById`. O job da segunda mensagem da RAM terminou `Completed`, `Created=1`, `Transitioned=0`, `Skipped=1`; o job da correção terminou `Completed`, `Created=1`, `Corrected=0`. Portanto, a falha do monitor era **canônica**, não um point stale do Qdrant. O extractor não recebeu a memória anterior de forma confiável porque o contexto existente dependia de busca semântica durante a janela anterior à projeção. O JSON bruto do extractor antigo não foi persistido, então o motivo exato daquele candidate descartado não pode ser reconstituído.

Isso descarta as hipóteses de “0 candidates” para essas mensagens e de “somente Qdrant stale” para o monitor. A falha de acesso ao embedding confirma um caminho concreto pelo qual o contexto antigo ficou incompleto; sem payload antigo nem trace original, não é possível provar que foi o único mecanismo de falha do extractor.

Os jobs Semantic dos fatos citados falharam com `embedding_auth_failed` na primeira tentativa. Isso explica a indisponibilidade semântica dos fatos PostgreSQL e RAM na sessão original; o fallback textual de frase inteira também não cobria bem perguntas indiretas. A chave da sessão retornou HTTP 403 `model_not_found` para `text-embedding-3-small`. Após o usuário informar liberação de `text-embedding-3-large`, a tentativa imediata com a mesma chave também retornou HTTP 403 `model_not_found`; nesta continuação a rede passou a negar a chamada antes de qualquer HTTP. Não há score real de embedding ou calibração válida a registrar. Os thresholds manuais `0.45` e automáticos `0.60` não foram elevados por palpite.

Um trace determinístico anterior à correção do Graph, com FaZe semântico score **0.90** (fake embedding, **não** score do provider real), reproduziu: memória FaZe → evidência Pedro–FaZe → seed Pedro → paths Pedro–monitor e Pedro–Aegis → supporting memories irrelevantes. A causa do fan-out foi confirmada nesse caminho. O score real de monitor/Aegis na consulta de aceitação permanece **desconhecido** por falta do embedding autorizado; não se pode excluir uma contribuição semântica adicional.

### Mudanças implementadas

- `MemoryExtractionSource.Recent` preserva IDs; `BuildInputAsync` busca `MemoryEvidence.SourceMessageId` e `MemoryRecord` no PostgreSQL da mesma conversa antes da busca semântica. Deduplica por ID e envia somente `mN` com `source=recent_conversation` ou `semantic`. Testes determinísticos com Qdrant desligado cobriram correction 4K/144 → `Superseded` + QHD/180 `Active` e transition 16 GB `Active` com `ValidUntil=T` → 32 GB `Active` com `ValidFrom=T`. Uma etapa posterior de retomada da projeção fake e convergência dos jobs foi adicionada ao teste, mas ainda não executada.
- `ClaimAsync` respeita `(ChatMessage.CreatedAt, Id)` dentro de cada conversa. Jobs Pending, Processing e retries anteriores bloqueiam o próximo; terminal Completed, Suppressed ou Failed não bloqueia. O diagnóstico indica a falha terminal anterior. O teste físico PostgreSQL com dois stores/workers verificou bloqueio na mesma conversa, claims simultâneos em conversas distintas e progresso após Failed terminal.
- O Graph ancora a relação apoiada por evidência e expande um hop da entidade específica, sem usar Pedro como seed universal vindo de uma relação. Mesmo a menção literal de Pedro em uma pergunta específica não o torna seed irrestrito; consultas amplas sobre Pedro ainda podem expandir. O teste de hub original deixou monitor/Aegis fora e a busca indireta Sakamoto–Bisky passou antes da restrição do ambiente; uma variante adicional com Pedro mencionado literalmente foi adicionada, mas ainda não executada. A busca lexical PostgreSQL passou a aceitar tokens relevantes, com ordenação e histórico, para cobrir atraso/indisponibilidade de Qdrant.
- O identity prompt proíbe reformulação tautológica de declarações sem pedido. O eval live declarativo focado passou **6/6 em uma rodada**; os três trials exigidos ainda não rodaram. O eval live de extração passou **28/28** após ajuste de alias Vecna; três repetições focadas do alias passaram **3/3**. O eval focado de intenção Memory passou **23/23**.
- `AEGIS_MEMORY_DIAGNOSTICS_ENABLED=false` por padrão. Os endpoints DEV de mensagem, retrieval, alias e ambiente retornam 404 desabilitados. O diagnóstico de mensagem consulta fatos/evidence/relations/projections/Activity canônicos sob demanda; `OutcomeJson` guarda apenas contagens por código técnico de skip. O trace de retrieval contém candidatos semânticos com score/validade, fallback lexical, anchors, seeds, paths e resultado final, sem vectors. `scripts/memory_diagnose.py` formata essas rotas. Não há cópia de conteúdo no job nem no trace persistido.
- A fixture `memory_v060_manual_acceptance.json` preserva literalmente as mensagens 001–022. O harness opt-in testa resposta (inclusive julgamentos estruturados para papagaio e semântica temporal), PostgreSQL canônico, retrieval e Activity, usa estados de job/projeção em vez de sleeps fixos, e imprime diagnóstico em falhas. `eval_memory_focused_trials.py` exige três APIs limpas com bancos e collections diferentes e roda os grupos A–E uma vez em cada uma. `run_memory_acceptance_disposable.py` foi adicionado para provisionar uma stack do replay e três dos trials via Compose, executar os evals e remover stacks/volumes no `finally`; usa `text-embedding-3-large` em collections novas. O Compose do runner e o gate sem opt-in passaram validação estática; nenhuma das quatro stacks pôde ser iniciada nesta continuação. Assim, sequência live, benchmark e focused trials permanecem **não executados**.

### Validação desta continuação

| Gate | Resultado verificável |
| --- | --- |
| Backend Release após últimas alterações | passou, 0 erros; aviso xUnit2031 preexistente |
| Backend `dotnet test` após últimas alterações | **não executou**: VSTest abortou antes do primeiro teste com `SocketException (13) Permission denied` ao abrir listener local |
| PostgreSQL físico | testes de recent canonical, ordenação multiworker, fallback lexical e diagnostics passaram antes da restrição do ambiente; não repetidos após as últimas edições |
| Qdrant/Neo4j físicos | teste conjunto com fakes passou antes da restrição do ambiente; repetição atual bloqueada por acesso negado ao Docker |
| Frontend | `npm test` e `npm run build` passaram após a restrição do ambiente |
| Compose / diff | `docker compose config --quiet`, configuração do Compose descartável com placeholders, e `git diff --check` passaram |
| EF pending model changes | passou: nenhuma mudança de modelo pendente após a migration |
| Eval de extração / intenção | 28/28 e 23/23, respectivamente, antes da restrição de rede |
| Eval de qualidade declarativa | 6/6 em uma rodada; faltam os três trials |
| Scores reais / relevance benchmark | bloqueados por 403 inicial e rede negada nesta continuação |
| Replay exato 001–022 / três trials críticos | **não executados**; requerem embedding e infraestrutura descartável acessíveis |

### Replay manual 001–022 — estado desta execução

`N/E` significa **não executado**; `—` significa camada não aplicável ao turno. O runner gera a mesma tabela com resposta e resultado de cada assert quando o ambiente live puder ser executado. Não há PASS inferido de testes unitários.

| Conversation | Message | Expected | Response | Canonical | Retrieval | Activity | Result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 001 | qual é meu time favorito? | FaZe | N/E | — | N/E | N/E | NOT RUN |
| 002 | Qual time eu mais torço no competitivo de Siege? | FaZe | N/E | — | N/E | N/E | NOT RUN |
| 003 | esqueça que O time favorito de Rainbow Six do Pedro é a FaZe Clan. | seed Forgotten; job Suppressed | N/E | N/E | — | N/E | NOT RUN |
| 004.1 | qual é meu time favorito? | não afirmar FaZe | N/E | — | N/E | N/E | NOT RUN |
| 004.2 | Meu time favorito de R6 é a FaZe Clan. | Active; sem papagaio | N/E | N/E | — | N/E | NOT RUN |
| 005 | Qual é meu time favorito de R6? | FaZe | N/E | — | N/E | N/E | NOT RUN |
| 006 | Qual time eu mais torço no competitivo de Siege? | FaZe | N/E | — | N/E | N/E | NOT RUN |
| 007.1 | Eu desenvolvo você, a Aegis. | conhecimento útil Active | N/E | N/E | — | N/E | NOT RUN |
| 007.2 | A Aegis usa PostgreSQL como fonte canônica do sistema de memória. | PostgreSQL Active; sem papagaio | N/E | N/E | — | N/E | NOT RUN |
| 008 | Qual banco é usado como fonte canônica de memória pelo projeto que eu desenvolvo? | PostgreSQL; Pedro→Aegis→PostgreSQL | N/E | — | N/E | N/E | NOT RUN |
| 009.1 | Meu PC tinha 16 GB de RAM. | 16 GB Active; sem papagaio | N/E | N/E | — | N/E | NOT RUN |
| 009.2 | Troquei a memória do PC e agora ele tem 32 GB de RAM. | transition; 16 Active fechado, 32 Active | N/E | N/E | — | N/E | NOT RUN |
| 010 | Quanto de RAM meu PC tem hoje? | 32 GB; sem fatos alheios | N/E | — | N/E | N/E | NOT RUN |
| 011 | Quanto de RAM meu PC tem hoje? e quanto tinha no passado? | 32 atual, 16 passado | N/E | — | N/E | N/E | NOT RUN |
| 012.1 | Meu monitor é 4K 144 Hz. | 4K/144 Active inicial | N/E | N/E | — | N/E | NOT RUN |
| 012.2 | Não, eu falei errado. Meu monitor é QHD 180 Hz. Ele nunca foi 4K 144 Hz. | correct; 4K Superseded, QHD Active | N/E | N/E | — | N/E | NOT RUN |
| 013 | Qual é a resolução e a frequência do meu monitor? | QHD/180, sem 4K/144 | N/E | — | N/E | N/E | NOT RUN |
| 014.1 | Meu monitor já foi 4K 144 Hz? | negar história falsa | N/E | — | N/E | N/E | NOT RUN |
| 014.2 | O que você sabe sobre meu monitor? | QHD/180 | N/E | — | N/E | N/E | NOT RUN |
| 015 | Depois da FaZe, meu time de R6 favorito é a DarkZero. | segundo favorito Active | N/E | N/E | — | N/E | NOT RUN |
| 016.1 | Depois da FaZe, qual time de R6 eu mais gosto? | DarkZero | N/E | — | N/E | N/E | NOT RUN |
| 016.2 | Esquece essa informação sobre a DarkZero. | Forgotten; Suppressed; relation removida | N/E | N/E | — | N/E | NOT RUN |
| 017 | Depois da FaZe, qual time de R6 eu mais gosto? | não DarkZero | N/E | — | N/E | N/E | NOT RUN |
| 018 | Qual time eu mais gosto no competitivo de Siege tirando a FaZe? | não DarkZero | N/E | — | N/E | N/E | NOT RUN |
| 019 | Historicamente, qual time eu dizia gostar mais depois da FaZe? | sem DarkZero e sem monitor/Aegis/RAM/Vecna | N/E | — | N/E | N/E | NOT RUN |
| 020 | Meu segundo time favorito de R6 é a DarkZero. | conhecimento Active novamente | N/E | N/E | — | N/E | NOT RUN |
| 021 | Na faculdade me chamam de Vecna. | memória Active; alias de Pedro | N/E | N/E | — | N/E | NOT RUN |
| 022 | Qual é meu apelido na faculdade? | Vecna | N/E | — | N/E | N/E | NOT RUN |

**Status: NOT READY.** Não há base para `READY FOR RE-ACCEPTANCE` até o replay 001–022, asserts canônicos/de retrieval, calibração com provider real e 3/3 trials críticos passarem. O registro anterior permanece como evidência histórica e não substitui a aceitação que reprovou.

O ambiente desta continuação passou a negar sockets locais ao VSTest, acesso ao daemon Docker e saída de rede. Um banco PostgreSQL descartável de teste e um container Neo4j descartável criados antes dessa mudança ainda precisam de cleanup quando o daemon voltar; nenhum recurso de produção deve ser removido. `git add` falhou com `.git/index.lock: Read-only file system`; por isso nenhum commit/push foi feito nesta etapa e não houve merge.
## Retomada com acesso restaurado — 29/09/2026

A seção anterior documenta a etapa em que o ambiente negava rede, Docker e VSTest. O usuário restabeleceu o acesso e confirmou a autorização de `text-embedding-3-large`; a mesma chave respondeu HTTP 200 com vetor de 1.536 dimensões. As evidências abaixo são da retomada, em recursos descartáveis. A reprovação original permanece registrada acima.

### Causas confirmadas e calibração real

- No PostgreSQL original, 4K/144 e QHD/180 estavam ambos `Active`, sem `SupersededById`; 16 e 32 GB estavam ambos `Active`, sem intervalo. O extractor gerou candidate para a segunda mensagem de RAM e para a correção de monitor, mas não realizou `transition` nem `correct`. Assim, o monitor falhou no conhecimento canônico, não apenas em Qdrant. O JSON bruto daquele extractor não foi persistido, logo a razão exata de cada candidate antigo não pode ser recuperada. O contexto anterior dependia de Qdrant na janela entre commit e projeção, falha arquitetural reproduzida e removida com lookup direto por `MemoryEvidence.SourceMessageId` no PostgreSQL.
- A chave antiga não autorizava `text-embedding-3-small`; jobs de projeção Semantic do fato Aegis/PostgreSQL e da RAM terminaram com `embedding_auth_failed`. O fallback lexical anterior falhava em perguntas indiretas. O padrão agora é `text-embedding-3-large`, 1.536 dimensões, em `aegis_memory_semantic_large_v1`. A collection nova impede mistura de embeddings antigos; a reconciliação na inicialização projeta de novo os registros canônicos.
- O fan-out Graph anterior foi reproduzido antes da mudança com âncora FaZe e score fake 0,90: relação Pedro–FaZe tornava Pedro seed e alcançava Pedro–monitor e Pedro–Aegis. Após instrumentação, a pergunta literal da conversa 019 marcou FaZe **0,565**, RAM **0,243**, Aegis **0,226** e monitor **0,220** no modelo real: os três últimos ficaram abaixo de 0,45. Mesmo assim, o primeiro replay live registrou Aegis/RAM/monitor em `Consultou memória` porque a tool aceitou uma query expandida do chat model. Assim, foram confirmadas duas vias independentes de irrelevância: traversal pelo hub e query de tool mais ampla que a pergunta original. Não há trace da sessão manual original para atribuir qual via gerou cada item nela. `memory_search` agora ancora perguntas curtas terminadas em `?` na frase original, com fallback à query do modelo se a original não trouxer resultado.
- Graph preserva a relação relevante como âncora e expande apenas um hop da fronteira específica. Pedro, quando é só o outro endpoint, não vira ponte para siblings. O teste Pedro–FaZe / Pedro–monitor / Pedro–Aegis excluiu monitor e Aegis; o teste indireto Pedro–Sakamoto–Bisky chegou em Bisky. O teste físico com Neo4j confirmou traversal, projection, rebuild e reconciliation. Relações exclusivamente sustentadas por memória apagada são removidas.
- Na primeira execução live, a frase sobre DarkZero depois da FaZe foi interpretada como `transition` e encerrou a validade da FaZe, embora indicasse o segundo time favorito. O benchmark então falhou 1/4. A policy e uma normalização canônica preservam a FaZe `Active` e criam DarkZero como preferência secundária. O replay final passou o assert específico, e o benchmark passou 4/4.
- As quatro consultas do benchmark final com o provider real tiveram scores positivos **0,553–0,722** e negativos da fixture até **0,340**. No caso histórico da FaZe, a relevância foi 0,565 versus irrelevantes 0,156–0,243. Um caso de monitor mais amplo teve positivo 0,499 e negativo máximo 0,314; a pergunta histórica de monitor teve negativo RAM 0,408 e positivo QHD 0,667. Mantivemos 0,45 manual, entre 0,408 e 0,499 nessa amostra, e 0,60 automático para maior precisão. Não há cutoff relativo. Esses números vêm da fixture pequena da aceitação, não provam separação universal ou estabilidade futura do modelo. `limit=10` permaneceu máximo, não meta de preenchimento.

### Correção, ordenação e diagnósticos

`MemoryRecentMessage` agora conserva `MessageId`. O extractor recebe primeiro memórias canônicas da mesma conversa via PostgreSQL/evidence, deduplicadas por ID com as semânticas relevantes e identificadas como `source=recent_conversation`; refs continuam efêmeras `mN`. Um teste determinístico e outro com Qdrant físico sem processar pontos confirmaram monitor 4K `Superseded` → QHD/180 `Active` e RAM 16 GB `Active` com `ValidUntil=T` → 32 GB `Active` com `ValidFrom=T`. Após retomar o processador, os projection jobs convergiram. A memória corrigida ativa guarda só a especificação verdadeira; o vínculo `SupersededById` fornece ao retrieval histórico a informação de que a declaração antiga era erro, sem tratá-la como fato histórico.

`ClaimAsync` usa `(ChatMessage.CreatedAt, Id)` e impede claim de mensagem posterior na mesma conversa enquanto a anterior estiver `Pending` ou `Processing`, inclusive retry agendado. Duas instâncias de store/worker não claimed a segunda antes da primeira; conversas distintas progrediram ao mesmo tempo. `Completed`, `Suppressed` e `Failed` terminal liberam os próximos; diagnostics mostra uma falha anterior. O teste físico PostgreSQL passou.

A identity policy agora evita reformulação tautológica de declaração factual sem pedido. `AEGIS_MEMORY_DIAGNOSTICS_ENABLED=false` mantém rotas DEV em 404 por padrão. O endpoint por mensagem lê job, contagens, `OutcomeJson` apenas com códigos/counts, fatos, relações, projeções e Activity do PostgreSQL; o endpoint retrieval detalha scores, validade canônica, fallback lexical, mentions, âncoras, seeds, paths, supporting IDs e final. Não armazena vectors nem trace, e aplica `MemorySecretGuard`. O CLI foi executado contra endpoints físicos descartáveis para `message` e `retrieval`.

### Replay literal completo e resultados físicos

A execução completa 001–022 em PostgreSQL, Qdrant e Neo4j descartáveis, com modelos reais de chat, extração e embedding, passou **28/28**. O harness aguardou estados `Completed`/`Suppressed` e projeções `Completed` por polling técnico. A tabela separa a prova canônica nas escritas, retrieval nas buscas, Activity e resposta textual. `—` marca camada não aplicável; os PASS vêm da execução, com expectativas específicas.

| Conversation | Message | Expected | Response | Canonical | Retrieval | Activity | Result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 001 | qual é meu time favorito? | FaZe | Seu time favorito de Rainbow Six é a FaZe Clan. | — | PASS | PASS | PASS |
| 002 | Qual time eu mais torço no competitivo de Siege? | FaZe | Você mais torce pela FaZe Clan no competitivo de Rainbow Six. | — | PASS | PASS | PASS |
| 003 | esqueça que O time favorito de Rainbow Six do Pedro é a FaZe Clan. | seed Forgotten; job Suppressed | Apaguei essa memória. | PASS | — | PASS | PASS |
| 004 | qual é meu time favorito? | sem FaZe persistida | Não sei qual é o seu time favorito. | — | PASS | PASS | PASS |
| 004 | Meu time favorito de R6 é a FaZe Clan. | FaZe Active; sem papagaio | Certo. | PASS | — | PASS | PASS |
| 005 | Qual é meu time favorito de R6? | FaZe | Seu time favorito de R6 é a FaZe Clan. | — | PASS | PASS | PASS |
| 006 | Qual time eu mais torço no competitivo de Siege? | FaZe | Você mais torce pela FaZe Clan no competitivo de Rainbow Six Siege. | — | PASS | PASS | PASS |
| 007 | Eu desenvolvo você, a Aegis. | Pedro desenvolve Aegis; Active | Certo. | PASS | — | PASS | PASS |
| 007 | A Aegis usa PostgreSQL como fonte canônica do sistema de memória. | Aegis usa PostgreSQL; Active | Entendi. | PASS | — | PASS | PASS |
| 008 | Qual banco é usado como fonte canônica de memória pelo projeto que eu desenvolvo? | PostgreSQL; vínculo Pedro–Aegis | PostgreSQL. | — | PASS | PASS | PASS |
| 009 | Meu PC tinha 16 GB de RAM. | 16 GB Active; sem papagaio | Certo. | PASS | — | PASS | PASS |
| 009 | Troquei a memória do PC e agora ele tem 32 GB de RAM. | transition; 16 GB Active fechado; 32 GB Active | Entendi. | PASS | — | PASS | PASS |
| 010 | Quanto de RAM meu PC tem hoje? | 32 GB atual; sem fatos alheios | Seu PC tem 32 GB de RAM, segundo a informação disponível. | — | PASS | PASS | PASS |
| 011 | Quanto de RAM meu PC tem hoje? e quanto tinha no passado? | 32 GB atual; 16 GB passado | Hoje, seu PC tem **32 GB de RAM**.   No passado, tinha **16 GB de RAM**. | — | PASS | PASS | PASS |
| 012 | Meu monitor é 4K 144 Hz. | 4K/144 inicial Active | Certo. | PASS | — | PASS | PASS |
| 012 | Não, eu falei errado. Meu monitor é QHD 180 Hz. Ele nunca foi 4K 144 Hz. | correct; 4K Superseded; QHD/180 Active | Certo — vou considerar a especificação correta: QHD a 180 Hz. | PASS | — | PASS | PASS |
| 013 | Qual é a resolução e a frequência do meu monitor? | QHD/180; sem 4K/144 verdadeiro | Seu monitor tem resolução **QHD (2560 × 1440)** e frequência de **180 Hz**. | — | PASS | PASS | PASS |
| 014 | Meu monitor já foi 4K 144 Hz? | nega história 4K/144 falsa | Pelo que tenho registrado, não. A informação de 4K a 144 Hz foi corrigida como errada; seu monitor é QHD a 180 Hz. | — | PASS | PASS | PASS |
| 014 | O que você sabe sobre meu monitor? | QHD/180; não afirma 4K/144 verdadeiro | Sei apenas que seu monitor tem:  - Resolução **QHD** - Taxa de atualização de **180 Hz**  Não tenho registrado o modelo, tamanho, painel, tempo de resposta ou outras especificações. | — | PASS | PASS | PASS |
| 015 | Depois da FaZe, meu time de R6 favorito é a DarkZero. | DarkZero segundo favorito Active; FaZe permanece Active | Certo. | PASS | — | PASS | PASS |
| 016 | Depois da FaZe, qual time de R6 eu mais gosto? | DarkZero | DarkZero. | — | PASS | PASS | PASS |
| 016 | Esquece essa informação sobre a DarkZero. | DarkZero Forgotten; job Suppressed | Apaguei essa informação. | PASS | — | PASS | PASS |
| 017 | Depois da FaZe, qual time de R6 eu mais gosto? | sem DarkZero | Não tenho essa informação — só sei que a FaZe Clan é a sua favorita. | — | PASS | PASS | PASS |
| 018 | Qual time eu mais gosto no competitivo de Siege tirando a FaZe? | sem DarkZero | Não sei — só tenho registrado que você gosta da FaZe. Qual é o outro time? | — | PASS | PASS | PASS |
| 019 | Historicamente, qual time eu dizia gostar mais depois da FaZe? | sem DarkZero, monitor, Aegis, RAM ou Vecna | Não encontrei memória de qual time você dizia gostar mais depois da FaZe Clan. | — | PASS | PASS | PASS |
| 020 | Meu segundo time favorito de R6 é a DarkZero. | DarkZero Active novamente | Certo. | PASS | — | PASS | PASS |
| 021 | Na faculdade me chamam de Vecna. | Vecna alias único de Pedro; Active | Entendi. | PASS | — | PASS | PASS |
| 022 | Qual é meu apelido na faculdade? | Vecna | Vecna. | — | PASS | PASS | PASS |

O benchmark de relevance sem chat passou **4/4**: FaZe 0,722 (DarkZero secundária 0,696), monitor QHD/180 0,553, RAM atual 0,577 e Aegis/PostgreSQL 0,646. Na pergunta PostgreSQL, o resultado final também continha Pedro desenvolve Aegis por Graph, apesar do score semântico isolado dessa memória ser 0,278. A conversa 019 retornou só conhecimento pertinente à FaZe e não exibiu monitor/Aegis/PostgreSQL/RAM/Vecna em `Consultou` ou `Usou`. As conversas 003 e 016 suprimiram extração após forget, e a busca histórica não recuperou DarkZero apagada.

O eval de extração passou **28/28**, incluindo todas as frases exatas requeridas, correction/transition com `m1` canônico recente, a preferência secundária com FaZe existente, alias Vecna e dois forgets com zero candidates. O eval de resposta declarativa passou **18/18** em três rodadas; o eval de intenção Memory passou **23/23** após disponibilizar o SDK .NET no `PATH`. A primeira tentativa de intenção falhou antes de chamar o modelo por ausência de `dotnet` no `PATH` do shell, não por decisão de tool.

O backend final passou **387/395** testes (8 físicos opt-in ignorados nessa chamada); **13/13** testes físicos PostgreSQL/Qdrant/Neo4j passaram separadamente, incluindo commit canônico da correção e transição antes de qualquer point no Qdrant e convergência após retomar o processador. Frontend: **47/47** e build de produção passaram. Backend Release final passou com 0 erros e 0 avisos. Compose principal e descartável, `git diff --check`, Python compile e `dotnet ef migrations has-pending-model-changes` passaram.

A primeira rodada de três trials focados terminou **2/3**: trial 1 e 3 passaram A–E; trial 2 falhou num assert textual que confundiu a resolução QHD `2560 × 1440` com frequência `144 Hz`. O diagnóstico desse turno mostrava resposta QHD/180, memória ativa QHD/180 e retrieval final só QHD/180. O assert foi corrigido para reconhecer `144 Hz` como unidade e permitir `1440` pixels. Isso foi uma falha do harness, registrada aqui sem apagá-la.

Na rodada seguinte, um trial revelou variância real na resposta à transição da RAM: “Entendi — agora o PC tem 32 GB de RAM.” A transição canônica e retrieval estavam corretos, mas a resposta apenas parafraseava o usuário. A rodada foi interrompida após esse achado. Foi adicionada uma proteção restrita em `ChatService` para declarações factuais: ela compara os termos informativos da resposta curta aos da declaração e substitui uma reformulação óbvia por reação mínima; perguntas, pedidos de ação e comentários com informação nova permanecem. O streaming de declarações é acumulado até essa verificação, para que o texto exibido coincida com o persistido. Nove testes determinísticos cobrem os exemplos reprovados e comentários úteis. A identity policy também proíbe explicitamente “Entendi” seguido da mesma informação.

### Fechamento da retomada — 01/10/2026

A rodada final, iniciada em **três stacks limpas independentes**, passou **3/3** para cada grupo crítico A–E: Aegis/PostgreSQL e retrieval; RAM transition; monitor correction; precisão da consulta 019; e resposta declarativa sem paráfrase. Todos os grupos passaram em todos os trials, sem repetir a suíte até obter êxito. Depois disso, o replay completo com o código final passou novamente **28/28**, e o benchmark isolado passou **4/4**. A avaliação direta da policy declarativa, repetida após o último ajuste, passou **18/18**. A extração live passou **28/28** e a intenção Memory **23/23**. Falhas e variação anteriores permanecem descritas acima; não restou failure na rodada final.

O ambiente de teste usou PostgreSQL, Qdrant e Neo4j descartáveis e collections próprias, com modelos reais de chat, extração e `text-embedding-3-large`; os recursos das stacks foram removidos após os runs. A base real de Pedro foi apenas inspecionada, sem reparação automática nesta branch: as memórias erradas já existentes lá continuam exigindo uma decisão de migração/correção operacional fora desta suíte isolada. O resultado desta branch não deve ser interpretado como correção retroativa de dados de produção.

**Status final da branch: READY FOR RE-ACCEPTANCE.** A decisão de aceitação manual continua com o usuário; não houve merge na `main` nem alteração de versão.
