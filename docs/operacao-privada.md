# Desenvolvimento e operação privada

Para a rotina autorizada de desenvolvimento, use
[publish-dev](publish-dev.md). O script descobre a instalação efetiva e orquestra
os procedimentos abaixo; não é necessário coordená-los manualmente.

O Compose base continua em `Development`: Swagger e migrations automáticas no startup ajudam no desenvolvimento. A operação privada combina **`docker-compose.yml` e `docker-compose.private.yml`**; a API fica em `Production`, sem Swagger nem migrations automáticas. O código de `Program.cs` permanece igual.

Os comandos abaixo documentam os mecanismos administrativos reutilizados pelo
orquestrador e continuam úteis para recuperação controlada.

## Configuração e acesso

Copie `.env.example` para um arquivo local ignorado pelo Git e configure os provedores utilizados. Os valores de senha do exemplo são de desenvolvimento. Antes da operação privada, configure suas credenciais locais e mantenha `POSTGRES_PASSWORD`, `POSTGRES_USER`, `POSTGRES_DB` e `AEGIS_DB_CONNECTION_STRING` coerentes; mantenha também a configuração do Neo4j coerente. A mudança de senha de um banco existente requer seu procedimento administrativo próprio; editar `.env` não troca a senha persistida.

Todos os binds publicados no host usam loopback por padrão:

| Configuração | Efeito |
| --- | --- |
| `AEGIS_HTTP_BIND_ADDRESS=127.0.0.1` | PWA e API acessíveis pelo próprio host, por exemplo atrás do proxy privado existente. |
| `AEGIS_HTTP_BIND_ADDRESS=<IP da interface privada>` | Opção explícita para acesso pela interface VPN/LAN escolhida. Configure a política de rede e HTTPS correspondente. |
| `AEGIS_DATA_BIND_ADDRESS=127.0.0.1` | PostgreSQL, Qdrant e Neo4j permanecem no loopback do host. Os serviços Docker continuam se comunicando pela rede interna. |

Não é necessário publicar os bancos em outras interfaces para que a API Docker os alcance. A mudança dos padrões versionados não comprova a exposição efetiva da instalação atual: firewall, VPN e proxy reais não foram inspecionados nesta tarefa.

Os nomes dos containers e dos quatro volumes (`postgres_data`, `qdrant_data`, `neo4j_data`, `data_protection_keys`) continuam iguais. Em uma instalação existente, execute na mesma pasta e com o mesmo projeto Compose usado antes; preserve um eventual `COMPOSE_PROJECT_NAME` ou `-p` já adotado. Trocar o nome do projeto pode selecionar volumes novos e aparentar perda de dados. Não remova volumes para atualizar. Preserve o key ring Data Protection, os arquivos FCM e as identidades pareadas.

## Banco novo: migrations antes da API

Execute na raiz do repositório. Esta função apenas abrevia a combinação exata dos arquivos:

```sh
dc() {
  docker compose --env-file .env -f docker-compose.yml -f docker-compose.private.yml "$@"
}

dc config --quiet
dc build aegis-api aegis-pwa
dc up -d postgres qdrant neo4j
dc exec -T postgres sh -c 'pg_isready --username="$POSTGRES_USER" --dbname="$POSTGRES_DB"'

# One-off: funciona mesmo quando a API nunca foi iniciada.
dc run --rm --no-deps --entrypoint dotnet aegis-api /app/node-admin/Aegis.NodeAdmin.dll migrate

# Só iniciar a API depois de migrate terminar com código 0.
dc up -d aegis-api aegis-pwa
```

Se PostgreSQL ainda não estiver pronto, aguarde e repita `pg_isready` antes de executar o migrador. Para PostgreSQL externo, confirme o acesso com a conexão administrativa configurada; o `exec postgres` verifica apenas o serviço local do Compose. O `NodeAdmin` já é incluído no Dockerfile do backend, não abre listener HTTP e usa `ConnectionStrings__AegisDatabase`. Não depende de `docker compose exec aegis-api` em um container que ainda não existe.

## Banco existente: backup, atualização e recuperação

Mantenha a mesma conexão, projeto e volumes. Registre o commit/imagem anterior e confirme um backup recuperável antes da atualização. Para uma janela curta de manutenção, pare os escritores da Aegis e exporte o banco:

```sh
umask 077
mkdir -p backups
dc stop aegis-api aegis-pwa
dc exec -T postgres sh -c 'pg_dump --username="$POSTGRES_USER" --dbname="$POSTGRES_DB" --format=custom' > backups/postgres-antes-manutencao.dump

dc run --rm --no-deps --entrypoint tar aegis-api -C /root/.aspnet -czf - DataProtection-Keys > backups/data-protection-antes-manutencao.tar.gz
```

Use nomes novos para cada backup para preservar o anterior. Inclua Qdrant, Neo4j, configuração local e credenciais no procedimento privado de backup já utilizado; não versione seus conteúdos nem imprima tokens. Valide o dump e a restauração em um destino separado.

Com o backup confirmado, construa a imagem nova e execute o mesmo comando administrativo:

```sh
dc build aegis-api aegis-pwa
dc run --rm --no-deps --entrypoint dotnet aegis-api /app/node-admin/Aegis.NodeAdmin.dll migrate
dc up -d aegis-api aegis-pwa
```

Uma repetição bem-sucedida de `migrate` não reaplica migrations registradas. Em caso de falha, mantenha os escritores parados e investigue o erro; o startup em `Production` não vai corrigir o esquema implicitamente. Voltar à imagem anterior só é suficiente quando ela aceita o esquema resultante. Se for necessária recuperação de banco, restaure o backup em um banco de recuperação separado, valide-o e aponte a conexão de maneira controlada junto da imagem correspondente. Não use reset, `down -v`, limpeza de key ring ou reescrita de migrations como rollback.

## Validação reproduzível

`node scripts/check-compose.mjs` verifica a composição com arquivos de ambiente temporários: Development versus Production, loopback, seleção explícita de IP privado para HTTP, binds de dados, volumes e ausência de VAPID. Não lê `.env` nem inicia a instalação.

`PrivateOperationPostgresTests` exige `AEGIS_OPERATION_TEST_DATABASE` apontando para um banco descartável chamado **`aegis_operation_test`**. Cada teste cria e remove somente seu próprio schema aleatório. Cobre banco novo, repetição idempotente e atualização a partir de `20260604044343_InitialChatPersistence`, preservando IDs e conteúdo sintético de conversa/mensagem após reiniciar o contexto.

```sh
AEGIS_OPERATION_TEST_DATABASE='Host=127.0.0.1;Port=<porta descartável>;Database=aegis_operation_test;Username=<usuário de teste>;Password=<senha de teste>' \
  dotnet test backend/tests/Aegis.Application.Tests/Aegis.Application.Tests.csproj --filter FullyQualifiedName~PrivateOperationPostgresTests
```

Na execução desta manutenção, PostgreSQL 16 e a imagem nova do backend foram usados somente em containers/rede `aegis-maintenance-*`, com porta de teste em loopback. O executável da imagem também aplicou migrations em banco novo, repetiu a operação e atualizou um banco sintético anterior. Nenhum Compose real, volume persistente do proprietário ou configuração de produção foi alterado.
