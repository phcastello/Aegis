# Verificação privada da preview 0.7.2-unstable.2

Em 27/10/2026, a inspeção inicial já encontrou a API no SHA
`64dc3345aec6299cb971f52e4701a42e63746926` e a migration de proatividade aplicada
por uma operação anterior, registrada às 02:35–02:36 UTC. A tarefa não presumiu
deploy a partir do GitHub: conferiu containers, banco, HTTPS, artefatos e backup.

O novo [publish-dev](publish-dev.md) recompilou/testou e publicou API e PWA desse
mesmo SHA. Depois executou uma publicação **somente de backend**, sem build de
PWA ou aplicativos, e uma reexecução sem componentes pendentes.

| Componente | Estado verificado |
| --- | --- |
| API | SHA `64dc3345aec6299cb971f52e4701a42e63746926`, imagem `sha256:f155ac6b42051897a97a4c8554495c5125d294132d49a9e1240654243c7ea38b`, `Production` |
| PWA | Mesmo SHA, imagem `sha256:65d33cf722ad7abb6d4910237adddbed8d221050bd18322c6a4e37b4d0a88b0d` |
| Nodes recomendados | `0.7.2-unstable.2`, código `7002002`, protocolo/API 1 |
| Estado de distribuição | Branch `aegis-distribution` em `7213faf9d39816cba3e9a4d9c099c544f1ca857e`, preservada |
| Feed legado | Asset `latest.json` de `node-preview`, atualizado por último em 21/10/2026 01:17:05 UTC, preservado |
| Migration necessária | `20261022133709_ReminderInterventions`, já registrada; nenhuma reaplicação na instalação real |

O acesso utilizado foi o socket Docker local no servidor privado. O projeto
Compose continua `aegis`, originado em `/home/phc/Aegis/docker-compose.yml` e seu
override operacional. O orquestrador mantém uma composição privada resolvida por
execução, com imagens fixadas e os mesmos ambientes/mounts. Não muda o checkout
original com alterações do proprietário. Não abriu SSH, Docker TCP ou portas de
banco, não instalou runner e não alterou credenciais, volumes ou identidades.
PostgreSQL, Qdrant e Neo4j mantiveram os mesmos containers, sem reinício.

O proxy existente é Nginx Proxy Manager: `aegis.phcastello.com` encaminha para
`10.1.1.22:5173`. O HTTPS externo e a API direta responderam 200. Os bytes de
`index.html` e `sw.js` servidos por HTTPS conferem com a PWA em execução, e o
service worker tem `Cache-Control: no-cache`. O teste de upgrade em Chromium
preservou fila de ações e dados do usuário em fixtures.

`ReminderInterventionWorker` e `ProactiveNotificationWorker` constam do registro
de serviços da imagem selecionada. A observação após publicação registrou aumento
dos scans normais: `proactive_notifications` de 2885 para 2891 e
`reminder_interventions` de 2164 para 2173. Não se criou incidente, lembrete ou
mensagem proativa artificial na instalação real. Os contratos de conversa e o
endpoint de atividade proativa responderam normalmente; Nodes existentes foram
observados com versão `.2` e protocolo 1. Pareamento/autenticação foram cobertos
pelos testes descartáveis, sem criar identidades pessoais para testá-los.

O backup real anterior à migration foi restaurado novamente numa rede interna
temporária, sem portas publicadas. Todas as linhas/colunas históricas das 36
tabelas conferiram com o inventário da janela original. A migration foi aplicada
duas vezes nessa cópia, sem mudar campos antigos, e a API anterior conseguiu
ler o esquema expandido. O arquivo Data Protection também foi validado. SHA-256
do dump: `3edfac60defc6986d39c070c783273b3e8cdbd84cf2910c654bc17720ab8e9e8`.
Nenhum backup foi restaurado sobre o banco pessoal.

Evidências privadas, sem enviar seu conteúdo sensível ao GitHub:

- API/PWA: `~/.local/state/aegis/publish-dev/20261027T042128Z-75557e3c/`.
- Backend isoladamente e workers: `~/.local/state/aegis/publish-dev/20261027T043214Z-ad405024/`.
- Reexecução sem builds: `~/.local/state/aegis/publish-dev/20261027T042438Z-36022226/`.
- Backup real restaurado: `~/.local/state/aegis/backup-rechecks/20261027T043120Z-529113a7/`.

Validações: 714 testes backend aprovados e 47 omitidos por dependerem de outros
serviços/cenários; testes PWA/cliente e upgrade real do worker em fixtures; 55
testes JavaScript de controle/distribuição; 24 testes Python da operação; cinco
testes dos verificadores Android; um ensaio Docker sintético completo de backup,
migration e recuperação; actionlint 1.7.12, check-compose, check-docs e diff limpo.
O ensaio Docker é opt-in e aparece como skip na suíte rápida sem suas imagens.

O bootstrap dos controles está no [PR #22](https://github.com/phcastello/Aegis/pull/22),
separado das funcionalidades do [PR #19](https://github.com/phcastello/Aegis/pull/19),
que permanece draft. A main conserva seu ruleset e `Aegis Node CI result`.
Não se gerou outra distribuição nativa para homologar o orquestrador: `.3` não
foi consumida. O caminho de uma aprovação foi validado por contratos/fixtures e
pela estrutura do workflow; nova instalação física não foi simulada. A homologação
funcional de proatividade ainda exige lembrete geográfico descartável e falha
controlada de permissão com o proprietário.
