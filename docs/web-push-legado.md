# Encerramento do Web Push

Implementado na branch de manutenção pós-v0.7.1. Este registro histórico descreve
o encerramento do Web Push, antes da aposentadoria do cliente web na v0.8.0.

Naquela manutenção o navegador ainda criava lembretes pelo chat para Nodes elegíveis. Receber os lembretes atuais exige um aplicativo Android ou Windows pareado. O fluxo nativo (`ReminderProcessor`, dispatcher, tokens de ações nativas, FCM e polling em `ReminderOptions`) continua independente do Web Push antigo.

## Caminho encerrado

Foram retirados `NotificationControl`, os serviços de ativação/registro, o evento `notificationRequired`, o listener de novas mensagens push no worker, `IWebPushClient`/`WebPushService`, o pacote `WebPush`, as opções VAPID, sua validação no startup e o gerador de chaves sem consumidores atuais. As buscas por interface, implementação, `SendAsync` e `PushResult` localizaram apenas declarações, DI e testes do sender; `ReminderProcessor` já entregava exclusivamente pelos Nodes.

`GET /api/notifications/configuration` informa `enabled: false`, `publicKey: null`, `retired: true`. Tentativas antigas de `POST /api/notifications/subscriptions` recebem **410**, código `web_push_retired`, `retryable: false` e orientação para o aplicativo pareado. Os endpoints antigos de status retornam inativo; disable conclui sem alterar o banco. Não existe sucesso fictício de registro, nova emissão de tokens ou envio Web Push. Variáveis VAPID remanescentes no ambiente são ignoradas.

## Compatibilidade temporária preservada

Sem acesso ao banco de produção e às instalações existentes, não foi presumida ausência de notificações exibidas ou interações pendentes. Nenhuma tabela, migration, assinatura persistida, fila ou dado privado foi removido ou inventariado nesta execução.

A compatibilidade isolada consiste em:

- `ILegacyReminderInteractionTokens`/`LegacyReminderInteractionTokens`: somente validação do formato v1, mesmo propósito Data Protection e expiração original; não possui método de criação.
- Ações `/api/notifications/reminders/{id}/acknowledge` e `/open`: aceitam somente os tokens assinados existentes, para a ocorrência original de geração 1 sem série. Tokens expirados, de outra ação, ID ou geração não modificam a ocorrência.
- O Service Worker mantém o nome `aegis-reminder-interactions-v1`, sua retenção de 30 dias e as ações de notificações já exibidas. Falhas de rede, 429 e 5xx permanecem pendentes; demais 4xx são terminais. Na manutenção original, cache de navegação, atualização automática e exclusão de `/api` continuavam ativos; na v0.8.0, o worker passa a remover o antigo shell e não intercepta navegação ou API.

A retirada futura dessas ações/validador/fila exige comprovar a data da última emissão possível por **todas** as implantações anteriores e passar a validade máxima de 30 dias. Verificar somente a data de um commit ou supor que o proprietário não usa o recurso não basta. Se houver dúvidas ou metadados de ações ainda válidas, manter este restante. Quando expirados todos os tokens, os endpoints podem retornar 410 e a fila pode terminar pelas respostas terminais, em mudança própria. Retirada do esquema é outro trabalho, com migration e plano de preservação; não faz parte deste encerramento. O registro inativo/disable pode continuar como pequeno contrato terminal para clientes antigos.

Data Protection permanece: protege também outros segredos e tokens nativos. Não remover o key ring nem seu volume.

## Aposentadoria do chat web na v0.8.0

O gateway estático substitui o chat no domínio da instalação; os aplicativos Android
e Windows continuam recebendo notificações e executando o chat. A página não
registra outro service worker. Se existir registro antigo, solicita sua atualização.
O worker transitório mantém apenas o código necessário às ações explícitas já
emitidas, sem precache novo, sem handler de `fetch` e sem novas notificações push.
Remover a fila ou limpar toda a origem antes de comprovar sua expiração poderia
descartar ações legítimas.

- [Downloads oficiais do Signal](https://signal.org/download/): exemplo de um
  produto que oferece acesso a instaladores por plataforma, em lugar de expor
  outro cliente web operacional. A Aegis conserva seu próprio design e usa o
  feed recomendado já compartilhado pelos Nodes; com falha na leitura do feed,
  mantém links visíveis para os lançamentos oficiais.
- [Workbox Background Sync](https://developer.chrome.com/docs/workbox/modules/workbox-background-sync):
  o mecanismo real da fila histórica mantém requisições falhas para nova tentativa.
  Conservar nome, retenção e tratamento de resposta evita migrar arbitrariamente a fila.
- [skipWaiting](https://developer.mozilla.org/en-US/docs/Web/API/ServiceWorkerGlobalScope/skipWaiting)
  e [clients.claim](https://developer.mozilla.org/en-US/docs/Web/API/Clients/claim):
  os mecanismos de ciclo de vida permitem tornar ativo o novo worker e assumir os
  clientes no mesmo escopo. O novo worker remove apenas o cache precache padrão
  da Aegis, sem apagar IndexedDB, localStorage ou outros caches da origem.

Os endpoints antigos de worker permanecem acessíveis para atualização do navegador.
O manifesto responde 410; o gateway não oferece instalação web. O navegador precisa
estar online para completar a substituição e abrir a página nova; quando ainda há uma
aba aberta com o JavaScript antigo, uma navegação/reabertura usa a página nova.
Continuam válidos os critérios de expiração de tokens e filas descritos acima.
Em 25/11/2026, a implantação de backend sem nova emissão comprovada alcança
27/10/2026, ainda a menos de 30 dias; não há prova suficiente da última emissão
possível em todas as implantações para remover a fila ou a compatibilidade da API.
O gateway preserva as rotas API/OAuth conforme [operação privada](operacao-privada.md).

## Validação local

- Testes .NET de API verificam recusa estável sem escrita, histórico preservado, criação de lembrete pelo chat para Node, startup sem VAPID ou com variáveis antigas inválidas, OK/open idempotentes, expiração e continuidade do key ring após reinício.
- As suítes nativas existentes continuam verificando entrega, reconhecimento e adiamento sem serviços de push reais.
- `npm test` e `npm run build` em `frontend/aegis-pwa` verificam o gateway estático e o worker transitório.
- `npm run test:worker` nesse diretório usa Chromium/Playwright e Workbox com IndexedDB reais. Exercita upgrade a partir do worker do predecessor v0.8.0-unstable.45, enfileira ações sintéticas, verifica a aposentadoria do shell anterior e a continuidade da fila. Os testes não registram assinatura real nem enviam notificações ao proprietário.
- `npm run test:page` valida os links de download, fallback, retorno OAuth e comportamento visual da página em Chromium, em desktop e telas estreitas.

Use `PLAYWRIGHT_CHROMIUM_EXECUTABLE` quando o Chromium instalado não for o padrão do Playwright. O teste precisa do histórico Git preservado e das dependências dos dois pacotes (`frontend/aegis-pwa` e `packages/aegis-client`).
