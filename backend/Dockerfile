FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Aegis.sln ./
COPY src/Aegis.Api/Aegis.Api.csproj src/Aegis.Api/
COPY src/Aegis.Application/Aegis.Application.csproj src/Aegis.Application/
COPY src/Aegis.Domain/Aegis.Domain.csproj src/Aegis.Domain/
COPY src/Aegis.Infrastructure/Aegis.Infrastructure.csproj src/Aegis.Infrastructure/
RUN dotnet restore

COPY . .
RUN dotnet publish src/Aegis.Api/Aegis.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8090

ENTRYPOINT ["dotnet", "Aegis.Api.dll"]
