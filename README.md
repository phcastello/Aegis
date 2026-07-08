# Aegis

Aegis v0.2.1, "Inbox Familiar", adds chat-driven Gmail connection, inbox briefing, email/thread summaries, and light inbox organization through confirmed tool actions.

Version history:

- v0.1.0, "Hello, Aegis", was the first functional and accessible milestone.
- v0.1.1, "Finding My Voice", adjusted conversational behavior to reduce repetitive greetings, avoid status dumps, respect explicit exclusions, and sound less generic.
- v0.1.2, "Bonk the Bot!", adds feedback capture for good and bad assistant responses.
- v0.1.3, "Finally, It’s Raining!", adds streaming responses and safe Markdown rendering.
- v0.1.4, "Where Were We?", adds real conversation history, opening old conversations, rename/delete actions, paginated history, and automatic short titles.
- v0.2.0, "Neural Uplink", moves Aegis' main interpretive brain to an online OpenAI model stack, with nano as the default model, mini as the operational model, and local non-blocking title generation.
- v0.2.1, "Inbox Familiar", adds chat-driven Gmail connection, inbox briefing, email/thread summaries, and light inbox organization through confirmed tool actions.

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
