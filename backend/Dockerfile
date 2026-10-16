FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Aegis.sln ./
COPY src/Aegis.Api/Aegis.Api.csproj src/Aegis.Api/
COPY src/Aegis.Application/Aegis.Application.csproj src/Aegis.Application/
COPY src/Aegis.Domain/Aegis.Domain.csproj src/Aegis.Domain/
COPY src/Aegis.Infrastructure/Aegis.Infrastructure.csproj src/Aegis.Infrastructure/
COPY tests/Aegis.Application.Tests/Aegis.Application.Tests.csproj tests/Aegis.Application.Tests/
COPY tools/Aegis.NodeAdmin/Aegis.NodeAdmin.csproj tools/Aegis.NodeAdmin/
RUN dotnet restore
RUN dotnet restore tools/Aegis.NodeAdmin/Aegis.NodeAdmin.csproj

COPY . .
RUN dotnet publish src/Aegis.Api/Aegis.Api.csproj -c Release -o /app/publish --no-restore

RUN dotnet publish tools/Aegis.NodeAdmin/Aegis.NodeAdmin.csproj -c Release -o /app/node-admin --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .
COPY --from=build /app/node-admin ./node-admin

EXPOSE 8090

ENTRYPOINT ["dotnet", "Aegis.Api.dll"]
