# Correções da primeira preview hands-free Android

A tentativa física da `0.8.0-unstable.2` (Android `8000002`, fonte
`3ba041005111689f07547afaa8ce3f6de5424822`) falhou antes da configuração. Este
incremento continua `feat/v0.8.0-hands-free` a partir de `41743c8`, preservando a
worktree original, dados privados, modelo e baseline. Não declara homologação no
telefone do proprietário.

## Causa do bloqueio e autorização efetiva

O arquivo `capabilities/hands-free.json` existia, mas `android-hands-free` não
constava da seleção explícita `app.security.capabilities` em `tauri.conf.json`.
O Tauri descobria o arquivo sem conceder a permissão à janela. A seleção agora
inclui o identificador correto, mantendo plataforma Android, janela `main`,
origem local e a única permissão `allow-node-hands-free`. Não foram adicionados
curingas, origens remotas ou comandos abertos.

Fontes oficiais: [capabilities](https://v2.tauri.app/security/capabilities/) e
[SecurityConfig](https://v2.tauri.app/reference/config/#securityconfig).
A resolução efetiva é importante: `OUT_DIR/capabilities.json` contém também
capabilities descobertas mas não selecionadas, portanto sua existência sozinha
não comprova autorização.

`build.rs` resolve os manifestos e capabilities pelo `tauri-utils` usado pelo
Tauri, após aplicar a configuração da plataforma e `TAURI_CONFIG` utilizado pelo
build/distribuição. Falha se o comando fechado não estiver autorizado exatamente
no Android local/main, ou se houver ampliação para outra plataforma/origem.
Emite `hands-free-acl.json` sanitizado em `OUT_DIR`. Os testes removem a seleção
e tentam ampliar escopo para demonstrar que o guard falha. Permissões autogeradas
continuam originadas pelo manifesto, sem edição manual.

A chamada segue `HandsFreeSettings.vue` → `services/handsFree.ts` →
`invoke("node_hands_free")` → ACL Tauri → `hands_free.rs` → bridge JNI/plugin →
`MainActivity`/`NodeHandsFree` → JSON validado. O estado inicial sem modelo,
permissão, assistente, habilitação ou backend retorna esses campos falsos;
consultar não abre AudioRecord, pede permissão nem acessa rede/backend.

## Falha e recuperação da interface

O carregamento, o estado atual, a última informação conhecida e o erro são
separados. A primeira falha mostra erro e **Tentar novamente**; não deixa
“Consultando escuta…” permanente. Uma atualização falha conserva a última
informação com aviso de desatualização e desabilita ações dependentes do estado
nativo. Recuperação limpa o erro e restaura os controles. Não há estado de
microfone inventado.

As operações são serializadas e atualizações durante uma operação são
coalescidas em uma consulta posterior. Geração/desmontagem impedem resultados
antigos de alterar a tela. Não foi adicionado polling. Erros conhecidos são
reduzidos a códigos `command_not_authorized`, `bridge_unavailable`, `timeout`,
`invalid_response` e `native_failure`, além dos erros fechados de solicitação,
plataforma e lease. Exceções completas, conteúdo e credenciais não vão para UI.

## Captura, VAD, sinal e TTS

A ativação e o endpointing deixaram de compartilhar uma evidência de fala
forçada. O classificador local libfvad/WebRTC fixado analisa o pre-roll preservado
e os blocos seguintes; início confirmado exige 80 ms. Silêncio, pulso de 20 ms,
sinal próprio e ruído contínuo têm regressões específicas. Áudio curto incerto é
conservado para STT: energia/classificação local não prova que uma invocação
verdadeira não inclua pedido. STT vazio ou somente invocação não cria conversa
nem chama LLM. A continuação limita a dois segundos o início, com qualificação
temporal limitada que pode terminar depois da fronteira. Ver
[VAD, replay real e limites](preview-corrections-vad.md).

O sinal próprio local de 40 ms usa duas notas com envelope, executado fora da
captura em fila limitada. Não silencia nem descarta áudio. O controlador revalida
a geração após leituras bloqueantes e serializa início/ativação com desligar,
lease manual e cancelamento, impedindo retomada por um bloco antigo. A continuação
não apaga cancelamento de sessão.

O TTS existente é lido incrementalmente para um sink PCM limitado, sem acumular
a resposta. Fragmentos ímpares são recompostos. Há cancelamento imediato de rede
e player, controle de fluxo e conclusão pelo contador real de frames reproduzidos.
EOF e `write()` não abrem continuação; falhas abortam o turno. O servidor HTTP
lento dos testes exige áudio entregue antes de liberar EOF e verifica drenagem,
cancelamento e erros intermediários. Ver [transporte, limites e evidência](preview-corrections-streaming.md).

## Métricas, privacidade e bateria

A exportação passa para `schema: 2`, preservando os agregados existentes e uma
cópia dos dados sem atribuição anterior (`unattributedBeforeSchema2`). Catálogo de
identidades e fronteiras observadas permitem separar build/modelo/configuração
por dia. Os campos gerais descrevem a observação atual; nunca reclassificam
histórico. Identidade desconhecida e overflow permanecem explícitos. Timings e
wake lock de turnos remotos ficam associados à identidade de início, mesmo após
alterar configuração. Primeira entrega de PCM ao player e duração total são
medidas separadamente, com soma/contagem/mínimo/máximo. Não são latência acústica.

Nenhum áudio, comando, transcrição, screenshot ou dado de outro app foi adicionado
às métricas. O [baseline original](battery/baseline-2026-10-25_30/README.md) não
foi modificado e não houve reset de estatísticas Android. A preview bloqueada
não constitui dia funcional; seguir o [protocolo atualizado](battery/three-day-protocol.md)
após ativação efetiva, excluindo dias parciais e registrando mudanças de build.

## Detector e evidências distintas

O modelo canônico de 39.552 bytes permanece com SHA-256
`f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514`.
Não há alteração de pesos, frontend, quantização, threshold ou janela de scores.
Os [resultados anteriores de paridade](detector-parity.md) continuam registrando
divergência numérica no runtime default e conservação dos eventos no Linux;
não há alegação de igualdade bit a bit nem tolerância ampliada.

Replay ARM64 permanece pendente. O mecanismo
[`device-replay.py`](../../scripts/hands-free/device-replay.py) continua disponível;
emulador x86_64 e compilação ARM64 não comprovam execução ARM64. Nenhum aparelho
físico foi utilizado nesta correção. Rota de áudio, microfone concorrente, tela
bloqueada e latência acústica precisam da homologação do proprietário.

## Verificação e distribuição

Verificações locais concluídas em 31/10/2026:

| Verificação | Resultado |
| --- | --- |
| Node / contratos / leases de áudio | 160 testes passaram |
| Rust, incluindo resolução ACL real | 90 passaram, 2 ignorados já existentes |
| Contratos de distribuição / transformação da preview | 53 + 10 passaram |
| Build/typecheck Node e cenário Chromium falha→retry→recuperação | passaram |
| Kotlin Android (`testArm64DebugUnitTest`) | 132 passaram, sem falhas/skips |
| Instrumentação debug e container independente release | Kotlin compilou |
| Build completo APK ARM64 debug | passou; não é prova de execução |
| VAD host com JNI/classificador real e corpus privado | 16 passaram |
| Transporte/player JVM com servidor lento real | 54 passaram nas suítes selecionadas, incluindo 22 de transporte + 8 de PCM |
| PWA (regressão) | 91 passaram e build passou |

Os testes de browser usam IPC simulado para os cenários de falha. O novo
`NodeHandsFreeStateIpcTest` chama o comando real pela WebView do APK instalado,
antes de conceder microfone, importar modelo, selecionar assistente ou estabelecer
backend. O runner existente executa esse cenário no debug e no release minificado
com container de instrumentação independente. Sua execução em emulador e a
publicação serão registradas após os respectivos workflows. Nenhuma chave oficial
foi usada nas verificações locais; o container release compilou com chave de
teste descartável. Uma
compilação local ou teste IPC simulado não equivale à publicação nem à execução
Android real. A atualização deve ser instalada sobre o Node existente, sem
desinstalar, limpar dados, ativar modo desenvolvedor ou alterar otimizações globais.

Após atualizar: abrir **Configurações → Áudio e voz → Hands-free**, importar o
modelo privado se ainda ausente, permitir microfone, selecionar Aegis pelo seletor
oficial de assistente e habilitar escuta. Modelo já importado, pareamento e
preferências são preservados. Para uso real, deixar **Diagnóstico** desmarcado.
O botão de atualização permanece disponível se a primeira consulta falhar.
