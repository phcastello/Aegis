# Encerramento do Web Push

Implementado na branch de manutenção pós-v0.7.1; não altera os instaladores já publicados.

O navegador continua criando lembretes pelo chat para Nodes elegíveis. Receber os lembretes atuais exige um aplicativo Android ou Windows pareado. O fluxo nativo (`ReminderProcessor`, dispatcher, tokens de ações nativas, FCM e polling em `ReminderOptions`) continua independente do Web Push antigo.

## Caminho encerrado

Foram retirados `NotificationControl`, os serviços de ativação/registro, o evento `notificationRequired`, o listener de novas mensagens push no worker, `IWebPushClient`/`WebPushService`, o pacote `WebPush`, as opções VAPID, sua validação no startup e o gerador de chaves sem consumidores atuais. As buscas por interface, implementação, `SendAsync` e `PushResult` localizaram apenas declarações, DI e testes do sender; `ReminderProcessor` já entregava exclusivamente pelos Nodes.

`GET /api/notifications/configuration` informa `enabled: false`, `publicKey: null`, `retired: true`. Tentativas antigas de `POST /api/notifications/subscriptions` recebem **410**, código `web_push_retired`, `retryable: false` e orientação para o aplicativo pareado. Os endpoints antigos de status retornam inativo; disable conclui sem alterar o banco. Não existe sucesso fictício de registro, nova emissão de tokens ou envio Web Push. Variáveis VAPID remanescentes no ambiente são ignoradas.

## Compatibilidade temporária preservada

Sem acesso ao banco de produção e às instalações existentes, não foi presumida ausência de notificações exibidas ou interações pendentes. Nenhuma tabela, migration, assinatura persistida, fila ou dado privado foi removido ou inventariado nesta execução.

A compatibilidade isolada consiste em:

- `ILegacyReminderInteractionTokens`/`LegacyReminderInteractionTokens`: somente validação do formato v1, mesmo propósito Data Protection e expiração original; não possui método de criação.
- Ações `/api/notifications/reminders/{id}/acknowledge` e `/open`: aceitam somente os tokens assinados existentes, para a ocorrência original de geração 1 sem série. Tokens expirados, de outra ação, ID ou geração não modificam a ocorrência.
- O Service Worker mantém o nome `aegis-reminder-interactions-v1`, sua retenção de 30 dias e as ações de notificações já exibidas. Falhas de rede, 429 e 5xx permanecem pendentes; demais 4xx são terminais. Cache, atualização automática, navegação e exclusão de `/api` continuam ativos.

A retirada futura dessas ações/validador/fila exige comprovar a data da última emissão possível por **todas** as implantações anteriores e passar a validade máxima de 30 dias. Verificar somente a data de um commit ou supor que o proprietário não usa o recurso não basta. Se houver dúvidas ou metadados de ações ainda válidas, manter este restante. Quando expirados todos os tokens, os endpoints podem retornar 410 e a fila pode terminar pelas respostas terminais, em mudança própria. Retirada do esquema é outro trabalho, com migration e plano de preservação; não faz parte deste encerramento. O registro inativo/disable pode continuar como pequeno contrato terminal para clientes antigos.

Data Protection permanece: protege também outros segredos e tokens nativos. Não remover o key ring nem seu volume.

## Validação local

- Testes .NET de API verificam recusa estável sem escrita, histórico preservado, criação de lembrete pelo chat para Node, startup sem VAPID ou com variáveis antigas inválidas, OK/open idempotentes, expiração e continuidade do key ring após reinício.
- As suítes nativas existentes continuam verificando entrega, reconhecimento e adiamento sem serviços de push reais.
- `npm test` e `npm run build` em `frontend/aegis-pwa` verificam o host, pacote compartilhado e worker.
- `node tests/worker-upgrade.mjs` nesse diretório usa Chromium/Playwright e Workbox com IndexedDB reais. Compila o worker histórico do commit `5552de452c8465ec2eeacdef32308f45d5aa981d`, enfileira uma ação sintética enquanto o endpoint responde 503 e atualiza para o worker atual. Verifica reprodução após recuperação, término em 410, notificações exibidas antigas, ausência de nova entrega push, atualização do cache, navegação offline, exclusão de `/api` e preservação de IndexedDB/localStorage alheios. O teste injeta apenas os eventos sintéticos e um contador de exibição; não registra assinatura real nem envia notificações ao proprietário.

Use `PLAYWRIGHT_CHROMIUM_EXECUTABLE` quando o Chromium instalado não for o padrão do Playwright. O teste precisa do histórico Git preservado e das dependências dos dois pacotes (`frontend/aegis-pwa` e `packages/aegis-client`).
