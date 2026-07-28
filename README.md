# Aegis

Aegis v0.3.1, "Now We're Talking!", refines chat controls, contextual feedback, voice playback feedback, cancellation, and server availability while preserving the v0.3.0 voice stack.

Version history:

- v0.1.0, "Hello, Aegis", was the first functional and accessible milestone.
- v0.1.1, "Finding My Voice", adjusted conversational behavior to reduce repetitive greetings, avoid status dumps, respect explicit exclusions, and sound less generic.
- v0.1.2, "Bonk the Bot!", adds feedback capture for good and bad assistant responses.
- v0.1.3, "Finally, It’s Raining!", adds streaming responses and safe Markdown rendering.
- v0.1.4, "Where Were We?", adds real conversation history, opening old conversations, rename/delete actions, paginated history, and automatic short titles.
- v0.2.0, "Neural Uplink", moves Aegis' main interpretive brain to an online OpenAI model stack, with nano as the default model, mini as the operational model, and local non-blocking title generation.
- v0.2.1, "Inbox Familiar", adds chat-driven Gmail connection, inbox briefing, email/thread summaries, and light inbox organization through confirmed tool actions.
- v0.3.1, "Now We're Talking!", refines chat controls, contextual feedback, voice playback feedback, cancellation, and server availability.

In v0.2.1, Pedro keeps the same chat, history, feedback, streaming, and Markdown experience while Aegis can connect to Gmail through OAuth, brief the inbox from chat, summarize emails and threads, and prepare light organization actions that only execute after textual confirmation.

The repository is organized as a monorepo. Backend code lives under `backend/`, and the Vue PWA lives under `frontend/aegis-pwa/`.

## Stack

- .NET 8
- ASP.NET Core Web API with Controllers
- PostgreSQL
- Entity Framework Core
- Docker Compose
- Qdrant prepared for future vector memory work
- Vue 3
- Vite
- TypeScript
- PWA

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

For Gmail connection in v0.2.1, configure these values in `.env` for Docker or as environment variables when running the API locally:

```env
GOOGLE_CLIENT_ID=
GOOGLE_CLIENT_SECRET=
GOOGLE_REDIRECT_URI=http://localhost:8090/api/email/oauth/callback
GOOGLE_OAUTH_SCOPES=https://www.googleapis.com/auth/gmail.modify

AEGIS_MAX_EMAILS_PER_MANUAL_BRIEFING=30
AEGIS_MAX_EMAILS_TO_READ_PER_BRIEFING=15
AEGIS_MAX_EMAIL_BODY_CHARS=6000
AEGIS_EMAIL_BRIEFING_LOOKBACK_DAYS=7
```

Gmail actions are chat-driven. Aegis can read inbox content and metadata, but in v0.2.1 attachments are metadata-only: filenames, MIME types, sizes, and inline status can be mentioned, but attachment contents are not downloaded or analyzed.

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

## Voice in v0.3.0

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
