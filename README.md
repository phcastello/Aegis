# Aegis v0.6.0 — "Yeah, I know."

Aegis v0.6.0 now includes Intelligent Memory: durable automatic extraction, canonical and temporal knowledge in PostgreSQL, rebuildable Qdrant and Neo4j projections, hybrid retrieval and bounded automatic context. Recurring Google Calendar events from v0.5.1 and one-shot reminders from v0.5.0 remain available.

**Memory acceptance status: READY FOR RE-ACCEPTANCE.** The original manual acceptance failed; the exact 001–022 replay now passes 28/28 in isolated live infrastructure, and all five critical groups pass 3/3 clean trials. This is a request for a new human acceptance decision, not a merge. See the [v0.6.0 memory report](scripts/eval-results-v0.6.0-memory.md).

Version history:

- v0.1.0, "Hello, Aegis", was the first functional and accessible milestone.
- v0.1.1, "Finding My Voice", adjusted conversational behavior to reduce repetitive greetings, avoid status dumps, respect explicit exclusions, and sound less generic.
- v0.1.2, "Bonk the Bot!", adds feedback capture for good and bad assistant responses.
- v0.1.3, "Finally, It’s Raining!", adds streaming responses and safe Markdown rendering.
- v0.1.4, "Where Were We?", adds real conversation history, opening old conversations, rename/delete actions, paginated history, and automatic short titles.
- v0.2.0, "Neural Uplink", moves Aegis' main interpretive brain to an online OpenAI model stack, with nano as the default model, mini as the operational model, and local non-blocking title generation.
- v0.2.1, "Inbox Familiar", adds chat-driven Gmail connection, inbox briefing, email/thread summaries, and light inbox organization through confirmed tool actions.
- v0.3.0, "Now We're Talking!", introduces spoken chat responses, persistent browser playback, and coordinated turn cancellation.
- v0.3.1, "Now We're Talking!", refines chat and voice controls, improves server availability feedback, and adds push-to-talk transcription.
- v0.3.2, "Now We're Talking!", improves model driven tool use, streaming performance and cost, explicit prompt caching, Gmail reliability, visible tool progress, errors, and configuration consistency.
- v0.3.3 — refresh visual minimalista da interface.
- v0.4.0 — "Booked!" — integração conversacional com Google Calendar.
- v0.4.1 — "Booked!" — informações e cópia do ID da conversa, controles circulares, sidebar mobile com drag, layout estável e proatividade seletiva entre ferramentas existentes.

- v0.5.0 — "Knock Knock" — lembretes internos únicos, worker temporal persistente, Web Push e acknowledgement explícito.
- v0.5.1 — criação de séries recorrentes reais no Google Calendar.
- v0.6.0 — "Yeah, I know." — memória canônica, extração automática, histórico temporal, projeções semântica/relacional e recuperação híbrida.

Gmail capabilities introduced in v0.2.1 remain available: Aegis can connect through OAuth, brief the inbox from chat, summarize emails and threads, and prepare light organization actions that only execute after textual confirmation.

The repository is organized as a monorepo. Backend code lives under `backend/`, and the Vue PWA lives under `frontend/aegis-pwa/`.

## Stack

- .NET 8
- ASP.NET Core Web API with Controllers
- PostgreSQL
- Entity Framework Core
- Docker Compose
- Qdrant semantic projection for persistent memory
- Neo4j Community 5.26.30 relational projection for memory
- Vue 3
- Vite
- TypeScript
- PWA

## Aegis Memory

PostgreSQL stores the canonical state, evidence, lifecycle, temporal history and durable extraction jobs. Qdrant projects `MemoryRecord` for semantic retrieval. Neo4j projects canonical entities and relations for graph traversal. Both projections can be rebuilt from PostgreSQL. Automatic extraction runs after a user message commits; hybrid retrieval can add a small relevant context to a later chat turn. Neither projection owns knowledge.

`MemoryRecord` stores up to 2,000 characters of readable knowledge about any useful subject, with optional `ValidFrom`/`ValidUntil`. A record starts `Active`. A factual correction creates a new record and marks the incorrect old one `Superseded`; a real change closes the old record's validity while it stays `Active`, and starts the new fact at the transition time. Forgetting marks a record `Forgotten`. Intervals are half open: `[ValidFrom, ValidUntil)`, with null endpoints unbounded. The same exact fact can recur in nonoverlapping intervals; exact replay reuses an existing record. `MemoryEvidence` records distinct explicit requests or user statements and their optional source conversation/message references. Deleting the source conversation leaves consolidated memory and evidence intact, while a pending extraction job skips a deleted source.

`MemoryEntity`, `MemoryEntityAlias`, `MemoryRelation` and `MemoryRelationEvidence` are canonical PostgreSQL records. Automatic ingestion can create and reinforce entities/relations and attach evidence, using deterministic canonical-name/alias resolution. Ambiguous matches are skipped, never guessed. Only aliases explicitly stated in the target user message are accepted. Predicates remain validated UPPER_SNAKE_CASE strings. Relation transitions close validity while preserving history; corrections forget incorrect relations. Every canonical change and its `MemoryProjectionJob` commit in the same PostgreSQL transaction. The Semantic worker consumes only Semantic jobs; the Graph worker consumes only Graph jobs. Both reconcile current PostgreSQL state at startup and every 30 minutes by default, reopening Failed/Completed current jobs to repair drift.

Four conversational tools are available now:

| Tool | Contract | Effect |
| --- | --- | --- |
| `memory_remember` | `{"content":"Aegis usa PostgreSQL.","validFrom":"2026-09-01T00:00:00Z"}` (`validFrom`/`validUntil` optional) | Explicitly stores knowledge with provenance from the current user turn. |
| `memory_search` | `{"query":"qual banco a Aegis usa?","limit":10,"asOf":"2025-03-01T12:00:00Z"}` (`limit` default 10, max 30; `asOf` optional) | Combines semantic candidates with bounded graph traversal, validates facts in PostgreSQL at `asOf` or now, and records 30-minute observed memory references. Text search handles technical fallback. |
| `memory_update` | `{"memoryId":"<observed id>","content":"Aegis usa outro banco."}` | Supersedes one observed active memory in one transaction. |
| `memory_forget` | `{"memoryId":"<observed id>"}` | Soft-forgets one observed active memory, idempotently. |

The model receives IDs only through tool results and may not invent them. An explicit “lembra que…” stores immediately, with no second confirmation. Useful casual user statements may be learned silently by the background extractor; the chat model does not call `memory_remember` for them. Automatic context enters only after the current user message, beyond the stable prompt cache prefix, and creates no invisible tool references. Broad topic deletion is unavailable. Stored memory is untrusted data, never an instruction or authorization for external actions; current Gmail, Calendar and Reminder state must come from their tools.

### Memory Activity feedback

Assistant responses can show a small, persistent disclosure when memory was used, consulted, saved, updated or deleted. Expanding it shows the canonical facts involved. Automatic writes can appear a few seconds after the response, without changing its text. Activity stores only turn references, never a second copy of memory content; it is not a source of truth and is never read back by Aegis as memory or as authorization to change one.

### Semantic projection and configuration

The backend calls OpenAI `POST /v1/embeddings` directly, defaulting to `text-embedding-3-large` with 1,536 dimensions. It sends **only `MemoryRecord.Content`** (or the requested search query) to the external embedding provider. It sends no provenance, conversation/message IDs, aliases or other memory records. Embedding calls have API cost; `aegis_memory_embedding_input_tokens_total` records numeric input usage. Embeddings are never stored in PostgreSQL and no conversational LLM call is used to prepare them.

Qdrant uses one collection, `aegis_memory_semantic_large_v1`, with cosine distance and the configured vector size. Point ID equals `MemoryRecord.Id`; payload contains only `memoryId`, `revision`, `contentHash`, `model` and `dimensions`. The worker validates an existing collection's dimension and distance and **never deletes an incompatible collection**. This new collection keeps vectors from the authorized large model separate from earlier small-model vectors. On startup the worker requeues canonical records and projects them into the new collection. Changing embedding model or dimensions later requires another collection name; simultaneous model migration is not supported.

The worker atomically claims only Semantic jobs in PostgreSQL using `FOR UPDATE SKIP LOCKED`, a two minute recoverable lease and an ownership token. It retries transient failures after 10 seconds, 30 seconds, 2 minutes, 5 minutes and 30 minutes, then marks the job Failed. `LastError` stores a small technical category only. Before every Qdrant write, it reads the current canonical record under a row lock: Active means upsert; Superseded, Forgotten or missing means delete, regardless of the old job's operation. Existing points with matching revision, hash and model skip a new embedding call. At worker startup, and whenever a missing collection is recreated, reconciliation reopens the current unique Semantic job for **each** canonical record: Active uses Upsert; Forgotten and Superseded use Delete. This also repairs failed Deletes and drift after a completed Delete. Historical jobs remain untouched; repeating reconciliation does not create duplicate jobs, memories or points. Graph jobs are untouched.

Writes commit in PostgreSQL without waiting for Qdrant. Semantic indexing is eventually consistent. Search overfetches candidates, applies a configurable similarity threshold, then loads candidates in one PostgreSQL query and filters by Active status and `ValidFrom`/`ValidUntil`. It preserves vector order. A stale Qdrant point cannot expose a forgotten or superseded memory. Canonical text matches supplement semantic results, including during indexing lag; if embeddings or Qdrant are unavailable or semantic mode is disabled, search falls back to PostgreSQL text search. The manual threshold of 0.45 is a relevance filter, not factual confidence; a real `text-embedding-3-large` acceptance fixture measured relevant scores of 0.553–0.722 and irrelevant fixture scores up to 0.340. A broader monitor question had a relevant score of 0.499, while a historical monitor question had an irrelevant score of 0.408. The automatic threshold remains 0.60 for stricter context selection. This small fixture does not guarantee separation on other data.

Backend environment variables (never sent to the PWA):

| Variable | Default |
| --- | --- |
| `AEGIS_MEMORY_SEMANTIC_ENABLED` | `true` |
| `AEGIS_MEMORY_EMBEDDING_MODEL` | `text-embedding-3-large` |
| `AEGIS_MEMORY_EMBEDDING_DIMENSIONS` | `1536` |
| `AEGIS_MEMORY_EMBEDDING_BASE_URL` | `https://api.openai.com` |
| `AEGIS_MEMORY_EMBEDDING_API_KEY` | empty; falls back to `OPENAI_API_KEY`, never the STT key |
| `AEGIS_MEMORY_QDRANT_URL` | `http://qdrant:6333` |
| `AEGIS_MEMORY_QDRANT_COLLECTION` | `aegis_memory_semantic_large_v1` |
| `AEGIS_MEMORY_SEMANTIC_SCORE_THRESHOLD` | `0.45` |
| `AEGIS_MEMORY_PROJECTION_POLL_SECONDS` | `5` |

For a disposable physical Qdrant integration test, set `AEGIS_MEMORY_TEST_QDRANT_URL`; the test creates and removes only a random test collection. No new migration was needed for Part 2.

### Knowledge Graph projection and configuration

Docker Compose includes Neo4j Community `5.26.30-community`, with persistent `neo4j_data`. The backend uses the official `Neo4j.Driver` 5.28.4. Set `NEO4J_PASSWORD` for local Compose; configure `AEGIS_MEMORY_NEO4J_PASSWORD` separately when connecting to a different instance. Compose binds Bolt and Browser HTTP to localhost by default. Keep these ports protected in production: the graph contains names, aliases and potentially sensitive relationships. Neo4j receives no `MemoryRecord.Content`, evidence, conversation IDs or message IDs.

Each `MemoryEntity` projects to one `:AegisMemoryEntity` node identified by `entityId = MemoryEntity.Id`, with `canonicalName`, `normalizedName`, optional `entityType`, `aliases`, `revision` and optional `retiredAt`. Every relation is a fixed `:AEGIS_RELATION` edge identified by `relationId = MemoryRelation.Id`, with `predicate`, `revision` and optional `validFrom`/`validUntil`; predicates never become Cypher code. Unique ID constraints and a normalized name index are created automatically. PostgreSQL still owns entity identity, aliases, relation status and temporal validity.

Active relations with the same subject, predicate and object may coexist when their half-open validity intervals `[ValidFrom, ValidUntil)` do not overlap; null endpoints mean unbounded time. Exact replay reuses a relation. Overlap is rejected in the serialized PostgreSQL write transaction. An Active relation whose interval ended remains projected as history; Superseded and Forgotten relations are soft retained in PostgreSQL and removed from Neo4j. The `RefineMemoryRelationsForKnowledgeGraph` migration replaces the earlier Active unique triple index with a nonunique lookup index without dropping data.

The Graph worker claims only Graph entity/relation jobs with PostgreSQL `FOR UPDATE SKIP LOCKED`, a two minute recoverable lease, and limited retries at 10 seconds, 30 seconds, 2 minutes, 5 minutes and 30 minutes. Technical `LastError` codes contain no graph properties. For every job, the processor reads the *current* PostgreSQL aggregate: entities upsert with current aliases, Active relations upsert with current endpoints, other relations delete. Thus an old Upsert cannot restore a forgotten edge. Startup reconciliation reopens current Completed/Failed jobs, including Deletes; historical jobs are preserved. A maintenance `MemoryGraphRebuild` deletes only `:AegisMemoryEntity` nodes and their edges, then requeues current Graph jobs. Repeated runs preserve canonical IDs and do not alter Semantic jobs. Neo4j outages do not block canonical writes or semantic search.

Internal graph traversal accepts canonical start IDs, direction, optional normalized predicates, depth 1–3 (default 2), result limit up to 50, and optional `asOf`. Neo4j filters **every edge in the path** by that instant; PostgreSQL then validates every candidate relation, interval, supporting memory and endpoint, and supplies current entity names. A stale graph edge is never returned as a valid fact. Graph traversal is not a public tool; `memory_search` uses it through the bounded hybrid retriever.

| Variable | Default |
| --- | --- |
| `AEGIS_MEMORY_GRAPH_ENABLED` | `true` |
| `AEGIS_MEMORY_NEO4J_URI` | `bolt://neo4j:7687` |
| `AEGIS_MEMORY_NEO4J_USERNAME` | `neo4j` |
| `AEGIS_MEMORY_NEO4J_PASSWORD` | `NEO4J_PASSWORD` fallback; never sent to PWA |
| `AEGIS_MEMORY_NEO4J_DATABASE` | `neo4j` |
| `AEGIS_MEMORY_GRAPH_PROJECTION_POLL_SECONDS` | `5` |
| `AEGIS_MEMORY_GRAPH_MAX_DEPTH` | `3` |
| `AEGIS_MEMORY_GRAPH_MAX_RESULTS` | `50` |

Physical Graph integration tests require a **disposable** PostgreSQL database (`AEGIS_MEMORY_TEST_DATABASE`) and/or Neo4j (`AEGIS_MEMORY_TEST_NEO4J_URI`, `AEGIS_MEMORY_TEST_NEO4J_USERNAME`, `AEGIS_MEMORY_TEST_NEO4J_PASSWORD`, optional `AEGIS_MEMORY_TEST_NEO4J_DATABASE`). Neo4j tests also require the explicit guard `AEGIS_MEMORY_TEST_NEO4J_DISPOSABLE=YES_DELETE_AEGIS_PROJECTION`; they clean only nodes labelled `:AegisMemoryEntity` and their edges. The graph worker never calls OpenAI.

### Intelligent Memory

An explicit `memory_forget` suppresses the durable extraction job for that same user message in the same PostgreSQL transaction. A worker already waiting on extraction rechecks the job status and lease before each canonical candidate write, so a late model response cannot restore the forgotten fact. Suppression is a terminal `Suppressed` job state; explicit remember and update do not suppress extraction. Combined chat audits redact the response body of any model round containing a `memory_*` tool call, including its first round, while the real request and tool execution retain their arguments.

Each committed user message and its `MemoryExtractionJob` are saved together. The worker claims one job atomically with a two minute lease, then calls the configured OpenAI Responses model **outside** a database transaction using strict JSON schema Structured Outputs and `store=false`. Jobs from one conversation are claimed in user message creation order, including when multiple workers run; a terminal Failed job leaves a diagnostic gap but does not block later messages. The extractor receives the target message, at most six short preceding messages, up to ten existing memories, relevant relation refs and a small predicate catalog. Memories evidenced by those recent messages come directly from canonical PostgreSQL, ahead of semantic results, so correction and transition can refer to them while Qdrant is behind. The model sees only ephemeral `m1`/`r1` references and a source label, never canonical UUIDs. Only the target message may supply new facts. No raw extractor output is persisted or entered in `LlmRequestAudit`; job outcomes store only counts and technical skip reasons. A job with no useful candidates completes with zero candidates. Retries use bounded backoff; deleted source conversations or missing/non-user messages are skipped before extraction.

The extractor proposes at most eight atomic claims, each applied in a short canonical transaction. `create` stores a new fact or reuses an exact temporal match; `reinforce` adds evidence; `correct` supersedes a false old record; `transition` closes a formerly true interval and starts a new one. Relation actions create/reinforce an edge, close a historical interval or forget an incorrect edge. References such as `m1` and `r1` are valid only for the supplied context; invalid references cannot trigger destructive changes. Entity ambiguity skips graph mutation while preserving a valid textual memory. Automatic evidence uses `UserStatement`; no inference is persisted. Passwords, API keys, tokens and similar obvious authentication secrets are rejected by backend guards, including explicit `memory_remember` and `memory_update`. Memory is not a secret manager.

Hybrid retrieval uses semantic ranking and a token-ranked canonical PostgreSQL text fallback. Graph anchors come from relations supported by retrieved memories and from exact entity mentions. Traversal expands one hop from a specific frontier; an evidence relation through Pedro does not turn Pedro into a seed for all sibling facts. PostgreSQL validates candidate status, temporal validity and evidence. Qdrant down yields lexical plus graph; Neo4j down yields semantic plus lexical; both down yields lexical. Automatic context uses a stricter default vector threshold (`0.60`), at most five memories, eight graph paths and 3,500 characters, with a 2.5 second deadline. `memory_search` uses the same hybrid layer with its manual threshold (`0.45`) and optional historical `asOf`; only explicit tool search creates references for correction/forget. The isolated real embedding relevance fixture passed 4/4 without a relative cutoff; `limit` is a maximum, never a target count.

### Memory diagnostics

Set `AEGIS_MEMORY_DIAGNOSTICS_ENABLED=true` only on an isolated development or acceptance deployment. The default is `false`, and diagnostic routes return 404 when disabled. `GET /api/memory/diagnostics/messages/{userMessageId}` reports the extraction job, action counts, technical skip reason counts, canonical facts and relations, projection jobs, prior terminal failure in that conversation and Memory Activity. `POST /api/memory/diagnostics/retrieval` accepts `query`, optional `asOf`, `limit` and `automatic`; it reports semantic scores and filtering, lexical candidates, graph anchors/seeds/paths and final results. The routes fetch canonical content on demand, apply the memory secret guard, expose no vectors and do not enter a model prompt or tool schema.

`python scripts/memory_diagnose.py message <user-or-assistant-message-id>` and `python scripts/memory_diagnose.py retrieval "Historicamente, qual time eu dizia gostar mais depois da FaZe?"` format those routes. Point `AEGIS_MEMORY_DIAGNOSTICS_BASE_URL` at the isolated API. `AEGIS_MEMORY_ACCEPTANCE_LIVE=YES python scripts/run_memory_acceptance_disposable.py` provisions four disposable Compose stacks, runs the literal 001–022 replay, isolated retrieval benchmark, three focused trials, extraction, declarative and Memory intent evals, then removes all four stacks and volumes. Set `AEGIS_MEMORY_ACCEPTANCE_FOCUSED_ONLY=YES` with the live flag to run three fresh critical trials without repeating the full replay. It requires Docker, reachable real models, an authorized `text-embedding-3-large` key and a local `dotnet` executable for the intent eval; the opt-in flag prevents accidental paid calls. The underlying eval scripts can also target already provisioned isolated resources. Results and remaining limits are recorded in [the memory validation report](scripts/eval-results-v0.6.0-memory.md).

Automatic memory context and observed memory references are redacted from persisted `RuntimeContextSnapshot` and request audit payloads. Memory tool outputs in later model rounds have separate real and redacted request input lists. The network chat request still contains relevant memory as lower-trust data; stored audit data does not replicate it. User statements may be sent to the configured extraction provider, memory content/query text to the configured embedding provider, and selected memory context to the configured chat provider. Qdrant and Neo4j are local projections in normal Compose deployments. Conversation deletion does not erase already consolidated memory; use explicit search and forget for a specific fact. No Memory Manager UI, autonomous agent, Gmail/Calendar memory ingestion or broad forget is part of v0.6.0.

Automatic memory adds roughly one extraction request per processed user message plus embeddings for newly projected memories and semantic queries. Actual token usage is counted by metrics; no price is hardcoded. Eventual projection lag means a new fact may appear in PostgreSQL/lexical search before Qdrant and Neo4j converge. A different embedding model/dimension requires a new collection and explicit rebuild.

| Variable | Default |
| --- | --- |
| `AEGIS_MEMORY_AUTOMATIC_WRITES_ENABLED` | `true` |
| `AEGIS_MEMORY_EXTRACTION_MODEL` | `gpt-6-luna` |
| `AEGIS_MEMORY_EXTRACTION_REASONING_EFFORT` | `low` |
| `AEGIS_MEMORY_EXTRACTION_BASE_URL` | `https://api.openai.com` |
| `AEGIS_MEMORY_EXTRACTION_API_KEY` | empty; falls back to `OPENAI_API_KEY`, never STT key |
| `AEGIS_MEMORY_EXTRACTION_TIMEOUT_SECONDS` | `30` |
| `AEGIS_MEMORY_EXTRACTION_MAX_OUTPUT_TOKENS` | `2000` |
| `AEGIS_MEMORY_EXTRACTION_POLL_SECONDS` | `5` |
| `AEGIS_MEMORY_AUTO_CONTEXT_ENABLED` | `true` |
| `AEGIS_MEMORY_AUTO_CONTEXT_SCORE_THRESHOLD` | `0.60` |
| `AEGIS_MEMORY_AUTO_CONTEXT_MEMORY_LIMIT` | `5` |
| `AEGIS_MEMORY_AUTO_CONTEXT_GRAPH_PATH_LIMIT` | `8` |
| `AEGIS_MEMORY_AUTO_CONTEXT_GRAPH_DEPTH` | `1` (the retriever caps frontier expansion at one hop) |
| `AEGIS_MEMORY_AUTO_CONTEXT_MAX_CHARS` | `3500` |
| `AEGIS_MEMORY_AUTO_CONTEXT_TIMEOUT_MS` | `2500` |
| `AEGIS_MEMORY_DIAGNOSTICS_ENABLED` | `false` |
| `AEGIS_MEMORY_RECONCILE_INTERVAL_MINUTES` | `30` (`0` disables periodic repair) |

For physical acceptance, use a nonproduction deployment and wait for the extraction/projection jobs to complete: (A) say “Minha cerveja favorita é Heineken.”, then ask in a new conversation; (B) say “Sakamoto é meu amigo e namora Bisky.”, then ask who Sakamoto dates; (C) state an RX 6700 XT, then “Troquei por uma RTX 5080.” and check current and historical answers; (D) correct a false fact and confirm it does not become historical truth; (E) search and explicitly forget one memory, then confirm neither semantic search nor automatic context returns it. These manual deployed scenarios remain a checklist until actually run; deterministic disposable-store integration tests are separate evidence.

## Project Layout

```text
docker-compose.yml
.env.example
backend/
  Aegis.sln
  Dockerfile
  src/
    Aegis.Api/
    Aegis.Application/
    Aegis.Domain/
    Aegis.Infrastructure/
frontend/
  aegis-pwa/
    src/
    public/
    package.json
```

## Run Locally

From the repository root, start PostgreSQL and Qdrant:

```bash
cp .env.example .env
docker compose up -d postgres qdrant
```

The `.env` file is read by Docker Compose. The API does not load `.env` automatically when run with `dotnet run`; local API settings come from `backend/src/Aegis.Api/appsettings.Development.json` and normal ASP.NET Core environment variables.

For the shared Google connection (Gmail + Calendar), enable both Gmail API and Google Calendar API in the same Google Cloud project and configure these values in `.env` for Docker or as environment variables when running the API locally:

```env
GOOGLE_CLIENT_ID=
GOOGLE_CLIENT_SECRET=
GOOGLE_REDIRECT_URI=http://localhost:8090/api/email/oauth/callback
AEGIS_PUBLIC_APP_URL=http://localhost:5173
GOOGLE_OAUTH_SCOPES="https://www.googleapis.com/auth/gmail.modify https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.calendarlist.readonly"

AEGIS_MAX_EMAILS_PER_MANUAL_BRIEFING=30
AEGIS_MAX_EMAILS_TO_READ_PER_BRIEFING=15
AEGIS_MAX_EMAIL_BRIEFING_BODY_CHARS=500
AEGIS_MAX_EMAIL_FULL_BODY_CHARS=50000
AEGIS_EMAIL_BRIEFING_LOOKBACK_DAYS=7
```

`GOOGLE_REDIRECT_URI` is the public API callback registered with Google. `AEGIS_PUBLIC_APP_URL` is the browser URL of the PWA. Set both to their externally reachable HTTPS URLs behind a reverse proxy; they may have different origins. Gmail actions are chat-driven. Attachments remain metadata-only: filenames, MIME types, sizes, and inline status can be mentioned, but attachment contents are not downloaded or analyzed. Chat authorization tools return a short `/api/email/connect?redirect=true` entry link. Opening it creates a fresh Google consent URL in the browser, keeping protected OAuth state out of model-generated links and avoiding stale state in chat history. The redirect remains in the same PWA tab. After OAuth, the PWA confirms `/api/email/status` and shows the connected account when available.

Calendar discovers the account’s calendars through [Calendar List](https://developers.google.com/workspace/calendar/api/v3/reference/calendarList/list), including hidden calendars. It reads events from every calendar with event-read access, aggregates and sorts them chronologically, and applies a **global** limit (default 20, maximum 50). Calendars with only free/busy access are listed but cannot provide event details. `calendar_list_calendars` returns compact IDs, names, primary flags, access roles and `calendarType`. Create defaults to `primary`; another calendar is used only when the user explicitly requests that destination, using a calendar ID previously observed in this conversation. Ambiguous names require clarification. Update/delete automatically preserve the observed calendar of origin. Writer/owner access is checked during preparation and again at execution; reader calendars cannot be changed. Ask from chat to list an interval, search by text, read an event, or prepare a create/update/delete. Each mutation creates a `PendingCalendarAction` valid for 10 minutes and requires acceptance of the presented proposal in a later turn. The model interprets natural language and selects confirm/cancel tools; the backend validates persisted conversation/message identity, temporal ordering, expiration and action state, without keyword matching. Event references `(calendarId, eventId)` must have been returned by Calendar tools in this conversation within the last 30 minutes. If context expires, list/search again. The model obtains essential missing information or chooses reasonable concrete values when the user explicitly delegates that detail, presenting its choices for confirmation. Missing information without delegation can still require clarification; the backend never supplies an arbitrary duration. Timed values require RFC3339 with an explicit offset, and use the runtime reference `America/Sao_Paulo` unless another timezone is specified. All-day tool dates have an **inclusive** end (one day: start = end); Infrastructure converts to Google's exclusive `end.date`.

Google holiday calendars are identified by the calendar ID suffix `#holiday@group.v.calendar.google.com`, regardless of display name or country. They have `calendarType: "holiday"`; other calendars have `calendarType: "calendar"`. Reads separate appointments in `events` from day context in `holidays`, with explicit item `type` and preserved calendar/event IDs. Each list is chronological. The shared output limit prioritizes appointments so a holiday cannot replace the next appointment; `hasMoreEvents` and `hasMoreHolidays` indicate omitted results. Holidays are not busy time or scheduling conflicts.

Preparing a creation or a date/time change queries the connected holiday calendars for the selected local dates and returns `holidayWarnings` before confirmation. The warnings contain names, dates and calendar origins; multi-day overlaps can include an inclusive `endDate`. They are informational and do not block a confirmed operation. Queries respect timezones and exclusive end boundaries, and warnings are capped globally at 50 with `holidayWarningsHasMore` if truncated. Holidays come solely from the connected Google calendars, with no static holiday catalog, new OAuth scope, background monitoring, or identity prompt rule.

Existing Gmail-only authorizations remain valid for Gmail. Missing Calendar Events produces `calendar_scope_missing`; existing Events-only connections report `calendar_list_scope_missing` for the new Calendar List readonly scope. In both cases, `calendar_create_connect_link` requests the combined scopes in the existing OAuth flow. Reauthorize with the same Google account; the existing refresh token is preserved if Google omits a new one. No additional credentials or Calendar login are needed. `EmailAccountConnection`, existing OAuth endpoints, and token encryption are retained; a small shared `IGoogleAccessTokenProvider` handles refresh for both services.

Calendar mutations persist normalized fields and audit results. Create uses a stable UUID-hex event ID so retry cannot insert a duplicate. Updates fetch the current complete resource, preserve omitted and unknown fields, and use the prepared ETag through `If-Match`; changed events require new preparation, following [Google’s GET + UPDATE guidance](https://developers.google.com/workspace/calendar/api/v3/reference/events/update). Deletes are idempotent, including a final 404/cancelled resource. Every mutation verifies the post-state. A timeout, cancellation, or unavailable verification leaves explicit possible external effects; cancelling a pending action does not promise rollback. Gmail and Calendar confirmation tools act only on their own integration; an unrelated pending proposal does not require cancellation. Apply the EF migrations `AddCalendarActions`, `AddCalendarOrigins` and `AddPendingActionSupersession` when upgrading (the existing API startup migration flow applies it).

Unexecuted proposals without possible external effects can be replaced directly by another preparation. `calendar_amend_pending_action` changes only supplied fields of a pending create/update, preserves the rest and prepares a fresh proposal for later confirmation; it does not update a Google event that has not been created. Earlier proposals retain `SupersededAt`, `SupersededById`, their original payload and audit records. Gmail preparations follow the same replacement contract. Possible external effects are persisted before execution; even expiration does not permit silently replacing an unresolved attempt. Explicitly cancelling such an attempt leaves its possible effects visible and does not reverse them. Backend validation accepts complete, valid parameters chosen by the model under delegation, with no linguistic classifier or backend default table.

Calendar creation accepts structured daily, weekly, monthly and yearly recurrence with an optional interval, multiple weekdays for weekly events, a count, or an inclusive final date. With no count or final date, the series has no defined end. The supplied start and end describe the first occurrence; the backend validates the recurrence and writes one Google Calendar event with an RRULE, never a collection of separate events. The confirmation summary describes the recurrence in plain language. Pending creations can acquire, change, preserve or explicitly remove recurrence through `calendar_amend_pending_action`. Event reminders and the selected calendar apply to the recurring master. Holiday warnings during preparation cover only the first occurrence. No relational migration is required because the existing pending action JSON stores the normalized proposal. Occurrences returned by `singleEvents=true` retain their existing individual edit behavior; editing or deleting a whole existing series and “this and following” are not supported yet.

Event notes use Google's native `description`, containing only content explicitly requested by the user or clearly provided as an annotation. New events otherwise have no description: titles, conversation context, internal IDs, pending state, holiday warnings and reminders never generate a note. Reads through `calendar_get_event` include the description; compact event lists omit it. Create/update/amend reuse the same optional string: omission preserves an existing note, text defines/replaces it, and `""` clears it. Explicit `null` is rejected by tools. To append, read the current note and supply the complete text with the addition, preserving the original content. Notes are stored in the pending payload and mentioned briefly in its summary when changed; confirmation, ETag checks and post-operation verification apply normally. Updates to time/title/location preserve descriptions, including long existing descriptions. Reads are capped at 8000 characters and flag `descriptionTruncated` when incomplete; this preview must not be used to reconstruct a complete note. Replacement notes accept up to 8000 characters.

Event reminders are handled entirely by Google Calendar. New events without an explicit reminder preference receive the configured Aegis popup policy automatically in the backend:

| Event type | Minutes before the start | Default policy |
| --- | --- | --- |
| Timed | `4320, 1440, 240, 60, 0` | 3 days, 1 day, 4 hours, 1 hour and at the start |
| All-day | `3480, 600, 240` | 3 days before at 14:00, 1 day before at 14:00 and the eve at 20:00, relative to midnight in `America/Sao_Paulo` |

`GoogleCalendarOptions` provides the typed defaults. Configure `GoogleCalendar:TimedEventDefaultReminders` and `GoogleCalendar:AllDayDefaultReminders` as integer arrays, or use comma-separated `AEGIS_CALENDAR_TIMED_REMINDERS` / `AEGIS_CALENDAR_ALL_DAY_REMINDERS` as shown in `.env.example`; the latter take precedence and are forwarded by Compose. Invalid configuration fails validation. The values are resolved and stored in the pending payload before confirmation; a subsequent configuration change cannot alter that approved proposal. The all-day policy uses the requested offsets rather than creating timed events or altering calendar settings. [Google does not support an event-specific timezone for all-day events](https://developers.google.com/workspace/calendar/api/concepts/events-calendars); a calendar configured in another timezone does not acquire a guaranteed Brasilia notification time from these offsets.

The create/update/amend tools accept `reminderMode`: `aegis_default` applies the type-specific policy; `calendar_default` sends `useDefault: true`; `custom` requires the complete `reminders` array of `{ method, minutes }`; `none` sends `useDefault: false` and an empty overrides array. `keep`/omission on update preserves existing reminders, including on timed ↔ all-day changes. Creation defaults to `aegis_default`; `keep` is not a valid new-event preference. An explicit user preference takes priority over automatic defaults. Custom lists can contain up to five items, use `popup` or `email`, and require integer minutes from 0 through 40320; duplicate `(method, minutes)` pairs and inconsistent mode/list combinations are rejected before a Calendar request. An email and popup at the same minute are distinct reminders. To add a reminder, read the existing settings and supply the complete desired list, respecting the five-item limit.

Event reads expose compact `reminders` with `useDefault` and overrides; an empty overrides list with `useDefault: false` means no reminders. Calendar List exposes each calendar's actual `defaultReminders` when Google supplies them; omitted data is not replaced with a presumed Google default. Explicit reminder changes appear in the pending summary, and reminder-only updates follow the same later-turn confirmation, context safety, audit and idempotent post-state verification as other changes. Verification accepts reordered overrides and Google's omission of an empty overrides array. Pending amendments preserve the selected preference: custom settings remain intact; an unexecuted new event using Aegis defaults switches to the appropriate policy when its type changes. These Google event alerts still execute within Calendar; the independent Aegis reminder worker introduced in v0.5.0 does not store reminders as Google events.

Google errors distinguish `calendar_api_disabled` (including `SERVICE_DISABLED`/`accessNotConfigured`), insufficient scopes, missing calendars/events, denied access, and temporary failures. Backend diagnostics log HTTP status, Google reason/code, service and operation without credentials or raw error bodies. Calendar/event pagination is bounded at 20 pages and discovery at 1,000 calendars; exceeding a traversal bound returns an explicit incomplete-query error rather than silently hiding calendars.

Calendar has no dedicated screen, background monitoring, push/watch, scheduler or administration. Aegis remains an assistant without autonomous monitoring; its independent Reminder worker only handles time. Gmail content can lead to a Calendar suggestion only after a user-requested read puts that content in the conversation. Recurring occurrences returned by `singleEvents=true` can be edited individually; editing or deleting a whole existing series, “this and following”, invitations, and Meet creation remain outside the current Calendar integration. API and tool-flow tests use simulated Google responses; real-account OAuth and operations still require your configured Google project and consent.

Chat uses `AEGIS_CHAT_MODEL=gpt-6-luna` with configurable `AEGIS_CHAT_REASONING_EFFORT=medium`. Async conversation titles use `AEGIS_TITLE_MODEL=gpt-5-nano`, `AEGIS_TITLE_REASONING_EFFORT=minimal`, `OPENAI_API_KEY`, and `AEGIS_TITLE_MAX_OUTPUT_TOKENS=64`; no local model is required. These title settings are configurable for a future model change. `AEGIS_OPENAI_BASE_URL=https://api.openai.com` and `AEGIS_MAX_OUTPUT_TOKENS=4000` apply to chat. The model receives the Gmail, Calendar and Reminder tools with automatic selection. The backend still validates arguments, pending actions, confirmation, and effects.

The first Responses API request puts stable tools and identity first, with an explicit cache breakpoint at the end of the identity. It then appends native-role conversation history and the current user message. Runtime context, including the timestamp and any real pending Gmail/Calendar action state, comes last. Cache policy is `implicit` plus that explicit breakpoint: the stable prefix can be reused even when the conversation changes, while the implicit boundary at the current user message can be matched as a prior user-message boundary on the next turn. The changed runtime state follows that boundary and cannot break the history match. In a second turn, the previous user message and all earlier unchanged history can therefore hit cache when the shared visible prefix reaches GPT-5.6's 1,024-token minimum; new assistant text and the latest question are processed normally. A rolling 20-message history window can reset this longer match when old messages leave the window. Tool order and schemas remain deterministic. No padding or extra explicit history breakpoints are added, limiting cache writes to the stable boundary and OpenAI's implicit conversation boundary. OpenTelemetry meter `Aegis` exposes input, cached input, cache write, and output token counters, plus model/tool calls, tool iterations, and turn duration. See the [OpenAI prompt caching documentation](https://developers.openai.com/api/docs/guides/prompt-caching).

Run the read-only intent evaluation with `OPENAI_API_KEY=... python3 scripts/eval_tool_intent.py`. It exports all 28 registered production tool descriptions and schemas through `scripts/Aegis.ToolCatalogExport` and inspects model function calls with stubbed results; it never calls Gmail, Calendar or Web Push. It is intentionally excluded from credential-free CI. If `dotnet` is unavailable on the host, export the catalog in a .NET SDK container and pass `--tools-json <path>`. The historical v0.3.2 live run is recorded in [`scripts/eval-results-v0.3.2.md`](scripts/eval-results-v0.3.2.md). The v0.4.0 run and Calendar cases are recorded in [`scripts/eval-results-v0.4.0.md`](scripts/eval-results-v0.4.0.md). The v0.4.1 regression and proactivity run is recorded in [`scripts/eval-results-v0.4.1.md`](scripts/eval-results-v0.4.1.md).

After tool results, the next model call also receives the same trusted identity as a final developer message. This keeps selective next-step suggestions and their interest-before-action rule present when interpreting the results. The reminder is transient: it does not accumulate in native history, change the cached prefix, add a dynamic explicit cache breakpoint, or promote tool content to instructions.

Restore and build the backend:

```bash
dotnet restore backend/Aegis.sln
dotnet build backend/Aegis.sln
```

Run the API:

```bash
dotnet run --project backend/src/Aegis.Api/Aegis.Api.csproj --launch-profile http
```

Health check:

```bash
curl http://localhost:8090/api/health
```

Run the frontend PWA in local development mode:

```bash
cd frontend/aegis-pwa
cp .env.example .env.local
npm install
npm run dev
```

Open `http://localhost:5173`.

In local development, Vite serves the PWA directly and calls the backend on a separate origin. The frontend reads `VITE_AEGIS_API_BASE_URL` from `frontend/aegis-pwa/.env.local`; the value should point to the backend origin, without `/api`:

```env
VITE_AEGIS_API_BASE_URL=http://localhost:8090
```

Build the frontend:

```bash
cd frontend/aegis-pwa
npm run build
npm run preview
```

## Run With Docker Compose

```bash
cp .env.example .env
docker compose up --build
```

Then open the frontend:

```text
http://localhost:5173
```

And call the API health check:

```bash
curl http://localhost:8090/api/health
```

Swagger is enabled in the Development environment.

Docker/PWA mode uses the Nginx container as the public origin. In this mode, leave `VITE_AEGIS_API_BASE_URL` empty in the root `.env`; the built PWA calls `/api/...`, and Nginx proxies those requests to `aegis-api:8090` inside Docker:

```env
AEGIS_FRONTEND_PORT=5173
VITE_AEGIS_API_BASE_URL=
```

`VITE_AEGIS_API_BASE_URL` is baked into the PWA at image build time. Use `http://localhost:8090` for local Vite development, and an empty value for Docker/PWA proxy mode. Rebuild the frontend image after changing it:

```bash
docker compose build aegis-pwa
```

The PWA Nginx proxy waits up to 300 seconds for API responses, matching the
backend's model HTTP client timeout. If the deployment has another reverse
proxy or load balancer in front of Docker Compose, configure its upstream
response timeout to at least 300 seconds as well.

## Entity Framework

The database context is `Aegis.Infrastructure.Persistence.AegisDbContext`.

Create migrations when needed:

```bash
dotnet ef migrations add InitialCreate \
  --project backend/src/Aegis.Infrastructure \
  --startup-project backend/src/Aegis.Api
```

Apply migrations manually when needed:

```bash
dotnet ef database update \
  --project backend/src/Aegis.Infrastructure \
  --startup-project backend/src/Aegis.Api
```

## Voice output (introduced in v0.3.0)

Voice is part of the normal chat, never a separate public TTS screen. A browser creates a UUID turn when a message is sent; the API owns that turn, links its cancellation token to model/tool execution, and emits the same NDJSON chat protocol with `turnId` on conversation, token, and done events. A completed `done` event includes both `assistantMessageId` and the legacy `messageId`.

When automatic speech is enabled (the local default), the PWA sends the persisted assistant message ID to `POST /api/voice/speech`. The API verifies that it is an assistant message in the current turn's conversation, sends its complete persisted text to the private `aegis-tts` deployment, validates PCM s16le mono/24 kHz headers, and streams the bytes unchanged to the PWA. The browser never receives the TTS URL, token, profile controls, reference, or acoustic parameters.

The relevant endpoints are:

- `DELETE /api/chat/turns/{turnId}` — idempotently cancels model/tool work and any associated speech.
- `POST /api/voice/speech` — accepts `turnId`, `speechRequestId`, and `assistantMessageId`; it never accepts arbitrary text.
- `DELETE /api/voice/speech/{speechRequestId}` — stops only that voice request.
- `GET /api/voice/status` — reports whether voice is enabled and currently reachable without exposing internal addresses or credentials.

The native TTS contract is `POST /v1/aegis/speech` and `DELETE /v1/aegis/speech/{request_id}`. The native service requires a ULID-shaped request ID, so Aegis maps public browser UUIDs to internal native IDs. Requests use fixed `AegisVoicev1.0`, priority 50, `enqueue`, PCM, mono 24 kHz, and complete text only after the LLM has finished. No acoustic recipe, reference, normalizer, or DSP is duplicated here.

The PWA keeps one `AudioContext`/`AudioWorkletNode` alive after the first send gesture. It accepts arbitrary HTTP chunk boundaries, retains an odd residual byte, converts little-endian s16 PCM to floats, resamples continuously from 24 kHz to the actual device rate, and clears the generation-tagged queue immediately on stop. It targets 400 ms of audio, applies read backpressure above two seconds, and drops rather than grows beyond five seconds. This also keeps Bluetooth output awake while the model is generating, without adding artificial silence to speech.

`aegis.voice.autoSpeak` is a device-local preference; a missing key means enabled. Disabling it stops current audio but not textual generation. Each completed assistant message retains a compact **Ouvir** action that creates a new voice request without rerunning the LLM. If browser autoplay is blocked, use the first send or the toggle as the gesture to activate audio. Text remains available if TTS is disabled or unavailable; no browser `speechSynthesis` fallback is used.

Configure the backend only (never `VITE_*`) with:

```env
AEGIS_TTS_ENABLED=true
AEGIS_TTS_BASE_URL=http://10.1.1.47:8001
AEGIS_TTS_PROFILE=AegisVoicev1.0
AEGIS_TTS_DEFAULT_PRIORITY=50
AEGIS_TTS_CONNECT_TIMEOUT_SECONDS=5
AEGIS_TTS_FIRST_AUDIO_TIMEOUT_SECONDS=90
AEGIS_TTS_IDLE_STREAM_TIMEOUT_SECONDS=30
AEGIS_TTS_API_TOKEN=
```

For rollback, set `AEGIS_TTS_ENABLED=false` and redeploy the API; chat remains textual and the PWA keeps manual controls harmlessly unavailable. The independent `aegis-tts` deployment is not included in this Compose file.

Run the turn-lifecycle regression suite with:

```bash
dotnet test backend/Aegis.sln
```

It covers superseding a conversation turn, idempotent cancellation, invalid transitions, cancellation before registration, and concurrent registrations. The PWA typecheck and production bundle are verified with `npm run build` in `frontend/aegis-pwa`.

## Voice input (introduced in v0.3.1)

Voice input is push-to-talk only: the browser records a short clip, sends it only to the Aegis API, then inserts the returned transcript into the composer for review. It never sends the message automatically, does not persist audio, and does not use the active chat `turnId`.

Configure STT on the backend only. `AEGIS_STT_OPENAI_API_KEY` is intentionally different from `OPENAI_API_KEY`; the STT client never reads or falls back to the chat-model key.

```env
AEGIS_STT_ENABLED=true
AEGIS_STT_PRIMARY_PROVIDER=elevenlabs
AEGIS_STT_ELEVENLABS_MODEL=scribe_v2
AEGIS_STT_ELEVENLABS_API_KEY=
AEGIS_STT_FALLBACK_ENABLED=true
AEGIS_STT_FALLBACK_PROVIDER=openai
AEGIS_STT_OPENAI_MODEL=gpt-4o-transcribe
AEGIS_STT_OPENAI_API_KEY=
AEGIS_STT_LANGUAGE=por
AEGIS_STT_TIMEOUT_SECONDS=30
AEGIS_STT_MAX_AUDIO_BYTES=20971520
AEGIS_STT_MAX_RECORDING_SECONDS=90
AEGIS_STT_MAX_KEYTERMS=80
AEGIS_STT_KEYTERMS=Aegis;oito;às oito;Qdrant;TTS;STT;GPU;CPU
```

ElevenLabs Scribe v2 is the primary batch provider. It receives `language_code=por`, `no_verbatim=false`, `tag_audio_events=false`, and the backend-validated static keyterms. A technical primary failure (timeout, network/DNS failure, 429, 5xx, invalid/empty response, or missing primary configuration) may call the OpenAI fallback with `gpt-4o-transcribe`, `language=pt`, and a literal-transcription prompt. Invalid audio, provider HTTP 400, and client cancellation never trigger fallback.

The public contracts are `GET /api/voice/transcription/status`, returning `{ "enabled": boolean, "configured": boolean, "maxRecordingSeconds": number }` without an external probe, and `POST /api/voice/transcriptions` as `multipart/form-data` with `audio`, `transcriptionRequestId`, and `clientDurationMilliseconds`. The response is `{ "transcriptionRequestId": "uuid", "text": "..." }`; all transcription responses use `Cache-Control: no-store`.

The PWA prefers `audio/webm;codecs=opus`, then `audio/webm`, and uses an MP4/AAC MediaRecorder choice when that is what the browser supports (notably Safari). The API also accepts WAV, OGG/Opus, and MPEG/MP3. There is no browser-to-provider request, streaming STT, wake word, continuous capture, audio storage, or transcript cleanup by an LLM in this version.

## Aegis reminders and Web Push (v0.5.0 — "Knock Knock")

Reminders are Aegis entities, independent of Google Calendar and of the conversation that created them. Chat supports `reminder_create`, `reminder_list`, `reminder_update` and `reminder_cancel`. Explicit requests execute immediately; no second-turn Google mutation confirmation is used. Mentioning a deadline or casually wanting to remember something does not authorize creation. Updates/cancellation require a real reference observed in the same conversation, valid for 30 minutes. The latest list's order is preserved for requests such as “cancel the second”. Lists return at most 50 reminders and expose `hasMore`; optional time bounds use an exclusive upper bound. Text is limited to 600 characters to keep encrypted push payloads bounded.

These are **one-shot** reminders. Web Push with VAPID, using the [WebPush C# protocol library](https://github.com/web-push-libs/web-push-csharp), is the initial notification channel, targeting **desktop Chromium and Android Chrome/PWA**. Notifications stay enabled once permission is granted: opening the PWA reconciles the browser and backend and repairs missing registrations automatically, without requesting permission again. There is no permanent notification toggle in the chat header. On first use, a small dismissible notice above the composer offers **Permitir**; only this explicit click requests native permission. A failed reminder operation can offer setup again. Success/error feedback can be closed with × or Escape and expires after 5/12 seconds.

If permission is denied, allow notifications in browser settings and reopen the PWA. In Brave desktop, also enable **Use Google services for push messaging** in `brave://settings/privacy`, as described in [Brave's privacy settings documentation](https://support.brave.com/hc/en-us/articles/360017989132-How-do-I-change-my-Privacy-Settings). Notifications use the monochrome Aegis symbol on a transparent background as their small `badge` and a fully transparent bitmap as their large `icon`, avoiding [Chromium's generated fallback icon](https://chromium.googlesource.com/chromium/src/+/98bcd85e696b5a2b236e706204da0e9936d6e1b1/chrome/browser/notifications/android/java/src/org/chromium/chrome/browser/notifications/NotificationBuilderBase.java). Android controls the notification layout and may still reserve space for that image. The manifest also declares the Aegis symbol for monochrome use by the installed PWA. Opening the PWA checks once for a service worker update; existing displayed notifications retain the appearance they had when shown.

Activation still requires permission, an active service worker, a real PushSubscription, successful backend registration and an active backend status for that exact browser endpoint. Permission alone is insufficient. Revoked permission or a missing browser subscription disables the old backend record when its saved management credentials are available. Offline disable failures retain those credentials for the next reconciliation; localStorage is not proof of an active channel. If browser storage has been cleared, an old registration cannot be safely identified until this device registers again or the push service rejects it. No continuous polling is added. Up to 20 active devices can coexist. If Web Push is unconfigured or there is no active subscription, a creation request fails without saving a silent reminder: configure/enable notifications and repeat the request.

Notifications show the stored text and an **OK** action. OK records explicit **acknowledgement**, closes the notification and does not open Aegis. Clicking the body records `OpenedAt` when possible and focuses/opens the PWA, without acknowledgement. Acknowledgement is global: it ends reminder notification work as `Triggered`, clears the processing lease and stops future claims/retries and new attempts on other devices, while retaining all delivery history. It is distinct from cancellation. The processor rechecks under the reminder row lock before attempt creation and again before network I/O; an already running bounded send may finish before the ACK transaction obtains that lock. Notifications already visible on other devices are not removed remotely. These timestamps are idempotent and independent; a missing acknowledgement means only that none is registered. Temporary interaction failures are queued by the service worker, using Chromium Background Sync (up to 30 days); failed network/5xx/429 responses can be retried, while invalid tokens are terminal. The custom `injectManifest` worker retains precache, automatic updates and the `/api` navigation exclusion.

### Setup

1. Generate VAPID keys **once**, using `node scripts/generate_vapid_keys.mjs`. Store the private output in deployment secrets or your ignored `.env`, never in git. Retain the same pair across restarts.
2. Set `AEGIS_WEB_PUSH_SUBJECT=mailto:your-address@example.com` (or an HTTPS contact URL), `AEGIS_WEB_PUSH_PUBLIC_KEY` and `AEGIS_WEB_PUSH_PRIVATE_KEY`. All three empty disables Web Push: granting browser permission does not change that configuration. Confirm `/api/notifications/configuration` returns `enabled: true` before physical validation. Changing `.env` requires recreating the API container so Compose passes the variables; merely restarting the old container retains its old environment. Partially configured or invalid key pairs fail startup validation. `AEGIS_REMINDER_POLL_SECONDS` defaults to 5 and accepts 1–60 seconds. Equivalent `WebPush` settings exist in `appsettings.json`. Docker Compose forwards all four variables.
3. Apply migration `20260927130234_AddRemindersAndWebPush` through the existing EF migration procedure. Development mode applies migrations on startup. The new tables are `reminders`, `push_subscriptions` and `reminder_delivery_attempts`; the source conversation foreign key uses `SET NULL`, never cascading deletion.
4. Serve the PWA and `/api` through the existing same-origin proxy over **HTTPS** (localhost is the development exception). HTTP on an Android LAN address cannot enable Web Push. Open the PWA and allow notifications through the first-use notice if permission has not been granted; existing permission enrolls the device automatically. Then repeat a reminder request. Development validation of notifications should use the built PWA rather than the old public development worker.
5. Retain the existing Data Protection volume. Notification interaction and device-management tokens use separate purposes; notification tokens bind reminder, device and action and expire after 30 days. Multiple backend instances must share the database, VAPID pair and Data Protection key ring.

A reminder stores UTC operational instants and its IANA timezone (default `America/Sao_Paulo`); the model converts language to RFC3339 with an explicit offset, and the backend validates the absolute future instant. Every new temporal rule uses .NET `TimeProvider`. The persisted `ReminderWorker` polls due/overdue reminders, atomically claims rows with `FOR UPDATE SKIP LOCKED`, and assigns a two-minute recoverable lease with an ownership token. Row locks serialize chat mutations, human interactions and bounded sends. Attempts are persisted before network I/O; each device's outcome commits separately, so restart/retry skips accepted subscriptions. Downtime produces late reminders rather than losing them.

There is **no LLM in the trigger path**: PostgreSQL → ReminderWorker → encrypted Web Push → service worker. HTTP 404/410 disables a subscription while retaining audit data. Transient network/timeout/408/429/5xx failures allow at most five attempts per subscription with delays of 10s, 30s, 2min and 5min. Other HTTP failures are terminal and remain auditable. Push TTL is 24 hours. `AcceptedAt` means technical acceptance by the push service, not proof of delivery, opening or recognition. A crash after external acceptance but before its database commit leaves an unknown outcome, which can be retried; a stable notification tag and `renotify: false` replace an existing notification without intentionally re-alerting. Web Push cannot guarantee exactly-once delivery in this ambiguous crash window. Processing ends as `Triggered` if any device accepted, or `Failed` if none did; per-device failures remain visible in attempts.

The `Aegis` meter adds `aegis_reminders_{created,updated,cancelled,triggered,failed}_total`, `aegis_push_{attempts,accepted,failed}_total`, and histogram `aegis_reminder_trigger_delay_ms` (actual first processing time minus due time). These instruments contain no reminder text, IDs or subscription credentials. The API returns only the public VAPID key/configuration and minimal device registration/status responses. Subscription endpoints/keys never go to the model; network logging is disabled for the push client. The single-user deployment's existing access model is unchanged: keep the API inside the existing trusted access boundary. Subscription interaction endpoints require scoped signed tokens, and accepted subscription endpoints are restricted to Chromium's `fcm.googleapis.com` service to prevent arbitrary outbound requests.

### Automated validation

Run `dotnet test backend/Aegis.sln`, `npm test --prefix frontend/aegis-pwa` and `npm run build --prefix frontend/aegis-pwa`. PostgreSQL integration tests require `AEGIS_REMINDER_TEST_DATABASE` and/or `AEGIS_MEMORY_TEST_DATABASE` pointing at a **disposable** database; each suite is skipped when its variable is absent. Intent evaluation uses all 32 production tool schemas with simulated integration results, never live Google operations or live push.

The v0.5.1 checks on 28 September 2026 passed: **325/325 backend tests**, including **2/2 PostgreSQL integration scenarios** against a disposable container, **41/41 frontend tests**, backend Release build, frontend build/typechecks and `git diff --check`. Focused live-model intent evals passed **7/7** for creation/selection and **1/1** for recurring pending amendment. The backend retains one existing xUnit2031 test warning. Commands, scope, eval inputs and limits are in the [v0.5.1 validation report](scripts/eval-results-v0.5.1.md). The [v0.5.0 validation report](scripts/eval-results-v0.5.0.md) remains the historical record for that release.

The v0.6.0 Memory Foundation, Semantic Memory and Knowledge Graph checks passed **345/345 backend tests** with disposable PostgreSQL, Qdrant and Neo4j, **41/41 frontend tests**, both builds, Compose validation, EF model-change check and `git diff --check`. Focused live-model intent evals from Parts 1–2 and the Part 3 physical Graph checks are recorded in the [cumulative v0.6.0 Memory validation report](scripts/eval-results-v0.6.0-memory.md).

### Physical validation

Real Web Push was verified on the deployed HTTPS PWA during the manual tests on 27 September 2026, after configuring VAPID and repairing activation/reconciliation and the notification UX. The Android user confirmed receipt; the persisted record independently shows push-service acceptance (HTTP 201), `TriggeredAt` and an explicit `AcknowledgedAt`, with `OpenedAt` unset. This replaces the earlier statement that physical Web Push had not been validated.

| Scenario | Recorded evidence |
| --- | --- |
| Android Chrome/PWA activation and real notification | Confirmed in the manual test; a reminder requested through chat produced a real system notification. |
| Real OK acknowledgement | Confirmed by the persisted acknowledgement for that reminder. Opening is a separate interaction; no `OpenedAt` was recorded. |
| Multiple registered devices | A later reminder has two distinct device attempts accepted with HTTP 201 and a global acknowledgement. This proves technical acceptance, not visual receipt on both devices or suppression of a physically induced retry. |
| Desktop Brave | Its push messaging setting was identified and documented. A separate desktop receipt/interaction checklist has not been recorded. |

These observations do not establish that every physical subscenario was exercised. The simulated Chromium checks, including icon transparency and service worker updates, remain distinct from device testing.

### Remaining physical validation

Use the deployed built PWA over HTTPS to record the following separately:

1. On desktop Chromium, allow notifications through the first-use notice (or reopen with existing permission) and verify backend registration is active. In Brave, also enable its Google push messaging setting. Ask “Me lembra daqui 2 minutos de testar o Web Push.” Confirm creation, close the Aegis windows and check a real system notification arrives. Press OK: it must close without opening Aegis and persist `AcknowledgedAt`.
2. Use a separate reminder and click its body. Verify focus/open, `OpenedAt != null` and `AcknowledgedAt == null`.
3. Complete any unrecorded Android details, including receipt with the PWA closed and confirming OK does not open it. With desktop and Android registered, verify both visibly receive a reminder and confirm an actual pending retry on the other device stops after OK. Existing visible notifications can remain.
4. Stop the backend before a reminder is due, let the due time pass and start it again. Verify the late push, `TriggeredAt`, trigger-delay metric and persisted attempts.
5. Send a reminder, recreate the backend container with the same Data Protection volume and VAPID pair, then press OK on the already existing notification. Verify its interaction token still works.
6. Revoke Chrome notification permission outside Aegis and reopen it. Confirm the device record is disabled. Restore browser permission and reopen: automatic enrollment must verify a real active channel again. No reminder may be created when Web Push is unconfigured or all device subscriptions are disabled.

Only the scenarios with recorded evidence above are marked as physically validated. The remaining checks must not be inferred from automated coverage or HTTP acceptance.

### Known remaining limitations

Web Push cannot guarantee exactly-once delivery: a crash after external acceptance but before committing `AcceptedAt` remains ambiguous. Accepted push does not prove visual receipt, and absence of `AcknowledgedAt` does not mean the user ignored the notification. Already displayed notifications on another device are not removed remotely. Without local management credentials, an old subscription may not be identifiable immediately; permission changes while the PWA is closed are reconciled when the app can next observe them or the push service rejects the subscription.

Future work for the independent Aegis Reminder system deliberately excludes recurring reminders, cron/RRULE scheduling, priorities, reincidence/re-alerting, escalation, snooze, behavioral learning, a generic scheduler/automation engine, external conditions and autonomous monitoring. This does not limit the recurring Google Calendar events created in v0.5.1. The reminder worker stores useful telemetry without implementing adaptive decisions. No platform-specific Apple code or architecture is introduced.
