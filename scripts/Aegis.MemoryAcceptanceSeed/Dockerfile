FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY backend/src/Aegis.Domain/Aegis.Domain.csproj backend/src/Aegis.Domain/
COPY backend/src/Aegis.Application/Aegis.Application.csproj backend/src/Aegis.Application/
COPY backend/src/Aegis.Infrastructure/Aegis.Infrastructure.csproj backend/src/Aegis.Infrastructure/
COPY scripts/Aegis.MemoryAcceptanceSeed/Aegis.MemoryAcceptanceSeed.csproj scripts/Aegis.MemoryAcceptanceSeed/
RUN dotnet restore scripts/Aegis.MemoryAcceptanceSeed/Aegis.MemoryAcceptanceSeed.csproj
COPY backend/src/ backend/src/
COPY scripts/Aegis.MemoryAcceptanceSeed/ scripts/Aegis.MemoryAcceptanceSeed/
RUN dotnet publish scripts/Aegis.MemoryAcceptanceSeed/Aegis.MemoryAcceptanceSeed.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Aegis.MemoryAcceptanceSeed.dll"]
