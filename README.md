# Aegis

Aegis v0.1, "Hello, Aegis", is the first accessible foundation for a personal assistant with an ASP.NET Core Web API backend and a simple installable chat PWA.

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
curl http://localhost:8080/api/health
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
VITE_AEGIS_API_BASE_URL=http://localhost:8080
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
curl http://localhost:8080/api/health
```

Swagger is enabled in the Development environment.

Docker/PWA mode uses the Nginx container as the public origin. In this mode, leave `VITE_AEGIS_API_BASE_URL` empty in the root `.env`; the built PWA calls `/api/...`, and Nginx proxies those requests to `aegis-api:8080` inside Docker:

```env
AEGIS_FRONTEND_PORT=5173
VITE_AEGIS_API_BASE_URL=
```

`VITE_AEGIS_API_BASE_URL` is baked into the PWA at image build time. Use `http://localhost:8080` for local Vite development, and an empty value for Docker/PWA proxy mode. Rebuild the frontend image after changing it:

```bash
docker compose build aegis-pwa
```

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
