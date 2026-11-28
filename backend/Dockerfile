FROM debian:bookworm-slim AS python-wasi-build
ARG TARGETARCH
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential ca-certificates curl git python3 xz-utils \
    && rm -rf /var/lib/apt/lists/*
COPY script-runtime/build.sh ./build.sh
RUN sh ./build.sh "$TARGETARCH"

FROM python-wasi-build AS python-runtime
COPY script-runtime/launcher.c script-runtime/test_runtime.py ./
COPY script-runtime/install_libraries.py script-runtime/libraries.lock script-runtime/aegis_docx.py script-runtime/aegis_script_audit.py ./
RUN mkdir -p /opt/aegis-python/stdlib/lib-dynload \
    && python3 install_libraries.py /opt/aegis-python \
    && sha256sum build.sh launcher.c libraries.lock aegis_docx.py aegis_script_audit.py | sha256sum | cut -d ' ' -f 1 > /opt/aegis-python/revision \
    && cc -O2 -Wall -Wextra -Werror -I/opt/wasmtime-c-api/include \
    -L/opt/aegis-python -Wl,-rpath,'$ORIGIN' -o /opt/aegis-python/runner launcher.c -lwasmtime \
    && python3 test_runtime.py

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

# Render a bounded view of large attachments before sending them to the model.
RUN apt-get update \
    && apt-get install -y --no-install-recommends poppler-utils libvips-tools util-linux ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY --from=build /app/node-admin ./node-admin
COPY --from=python-runtime /opt/aegis-python /opt/aegis-python

EXPOSE 8090

ENTRYPOINT ["dotnet", "Aegis.Api.dll"]
