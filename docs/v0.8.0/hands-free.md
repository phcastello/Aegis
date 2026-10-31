# Aegis v0.8.0 — primeiro incremento hands-free Android

Implementação na branch `feat/v0.8.0-hands-free`, derivada de `913e629`
(`feat/v0.8.0-wakeword-poc`), que contém a main `8607dc0`. A worktree original
e suas alterações locais foram preservadas. Esta entrega prepara interação real
e diagnóstico no Node existente; a homologação no telefone ainda é necessária.

Distribuição de desenvolvimento publicada: **0.8.0-unstable.2**, código Android
**8000002**, fonte `3ba0410`. Disponível em **Configurações → Atualizações** ou
pelo [APK oficial ARM64](https://github.com/phcastello/Aegis/releases/download/node-v0.8.0-unstable.2/Aegis-Android-arm64.apk).
Os [resultados da publicação](validation-and-publication.md) registram assinatura,
hashes e as limitações de verificação.

## Instalação e ativação

1. Instalar a distribuição de desenvolvimento assinada por cima do Node
   existente. Preservar instalação, dados, pareamento e certificado; não
   desinstalar nem limpar dados. A versão efetiva e o código são os reservados
   pelo publicador, registrados no feed e em Configurações → Sobre.
2. Transferir privadamente `aegis.int8.tflite` para o telefone. O arquivo preparado
   no servidor está em
   `~/.local/share/aegis/private/hands-free-model-20261031/`, com manifesto privado.
   Não publicar esse arquivo nem os WAVs. O script
   [export-private-model.py](../../scripts/hands-free/export-private-model.py)
   confere o cache canônico e prepara uma cópia privada; não treina nem troca pesos.
3. Abrir **Configurações → Áudio e voz → Hands-free**. Com a escuta desligada,
   **Importar modelo privado** pelo seletor de arquivos do Android.
4. **Permitir microfone**, selecionar Aegis na interface oficial de assistente do
   Android e ligar **Escutar “Aegis”**. Manter as notificações/indicadores normais
   do sistema. O papel de assistente não concede DSP nem privilégios de sistema.
5. Conferir o estado. **Diagnóstico** usa a mesma captura/detector, emitindo sinal
   e contadores sem STT/chat. Desmarcá-lo para testar interação real.

Dizer “Aegis, o que tem de importante hoje?” sem pausa obrigatória após o nome.
Testar inicialmente com o Node aberto, com outro aplicativo e com a tela bloqueada.
Não é necessário abrir o chat ou desbloquear para processar e ouvir o turno.
Uma sessão hands-free inicia sua própria conversa e mantém esse destino nas
continuações, mesmo que a interface navegue. Não incorpora rascunhos digitados.

Para interromper, usar **Cancelar interação**, o botão da sessão nativa ou
**Desligar** na notificação. Encerrar um turno volta à wake word; desabilitar a
configuração libera a escuta. Para recuperar indisponibilidade, conferir modelo,
microfone, papel de assistente, pareamento e conexão; reabrir o Node e retomar a
configuração. Modelo ausente/corrompido requer importação válida. O Android pode
exigir nova concessão nas configurações do sistema após revogação permanente.

## Responsabilidades e fluxo

| Camada | Implementação e contrato |
| --- | --- |
| Captura/sessão | `NodeHandsFree.kt`: controlador nativo independente do WebView, com um proprietário de AudioRecord. |
| Detector | `cpp/`: frontend adaptado do Home Assistant, TFLite Micro e JNI; modelo carregado da área privada. |
| Buffer/VAD | `NodeHandsFreeAudio.kt`: ring limitado, WAV PCM e endpointing local. |
| STT/backend | `NodeHandsFreeBackend.kt`: endpoints existentes, identidade/conversa fixadas, limites e cancelamento. |
| Reprodução | `NodeHandsFreePlayback.kt`: voz existente em PCM mono 24 kHz, AudioTrack e conclusão pelo playback head. |
| Sistema/apresentação | `NodeVoiceInteractionService.kt` e `NodeVoiceInteractionSession.kt`: assistente oficial, painel discreto e notificação. |
| Instrumentação | `NodeHandsFreeMetrics.kt`: agregados locais, sem conteúdo, exportados pelo seletor de documentos. |
| Configuração/áudio manual | Controle fechado Rust/JNI, `HandsFreeSettings.vue` e lease de áudio no cliente compartilhado. |

A espera captura PCM16 mono 16 kHz localmente em leituras bloqueantes de 3.200
amostras (200 ms). Features de 10 ms e stride de três frames são processados
internamente; não há timers Kotlin de 10 ms. Não há STT/LLM contínuo, envio
ambiental, áudio em disco, atualização do WebView por frame ou polling da UI
para sustentar a escuta. Taxa não suportada resulta em indisponibilidade; não
foi introduzido resampling sem validação.

O ring guarda até 40.000 amostras (2,5 s). Na ativação, captura e detector não
fecham/reabrem o microfone: o bloco inteiro já recebido, incluindo áudio após o
evento, entra no comando. O sinal local de 55 ms é disparado sem bloquear a
captura. A apresentação é independente dessa passagem; não usa overlay genérico
nem consulta screenshots/conteúdo do aplicativo em primeiro plano.

O painel ajusta a janela de `VoiceInteractionSession` explicitamente para
altura `WRAP_CONTENT` e gravidade inferior em `onShow()`, depois da criação
pelo framework. Não escurece a tela nem solicita mantê-la ligada. A gravidade
isolada deixava a janela padrão de tela inteira; a
[investigação AOSP](android-hands-free-research.md#geometria-do-painel-nativo)
registra a ordem de callbacks e a decisão. Tamanho e navegação no telefone
continuam pendentes de homologação física.

O VAD local usa energia RMS em frames de 20 ms, piso 240 e limiar adaptativo
`max(240, ruído × 3)`, endpoint após 800 ms sem fala e limite de 20 s após
ativação, mais o pre-roll, limitado a 360.000 amostras. É uma regra conservadora
para este incremento: ruído contínuo, distância, voz baixa e rotas Bluetooth
precisam de avaliação física. O pre-roll pode conter fala anterior à invocação;
esta entrega não estima alinhamento fonético exato da palavra no áudio.

STT vazio ou somente a invocação não cria conversa nem chama o modelo. O
[contrato conversacional](hands-free-backend.md) acrescenta origem `hands_free`
e decisão estruturada no mesmo turno. As ferramentas mantêm suas confirmações.
Erros, cancelamentos e resultados atrasados não redirecionam a sessão.

Durante processamento remoto e TTS, o microfone é liberado e o detector fica
suspenso. Não há barge-in. Após o fim real do AudioTrack, `continue` abre até
dois segundos para **início** de fala; uma fala iniciada a tempo segue até o
endpoint/limite normal. `close`, decisão ausente/inválida, falha de reprodução
ou ausência de fala volta à wake word. A decisão não desliga hands-free.

O chat pede exclusividade antes de gravação ou reprodução manual e a libera
após parar tracks/reprodução. Callbacks de uma operação antiga não liberam uma
lease nova. O Windows e a PWA preservam seus caminhos de áudio existentes.

## Sistema, recursos e limites

O Android mantém o VoiceInteractionService escolhido pelo proprietário. A
captura usa FGS do tipo microphone, permissões normais e a exceção documentada
para o aplicativo que fornece o assistente. O estado diferencia configuração
habilitada de recebimento confirmado de áudio. Nenhuma API oculta, root,
AccessibilityService, overlay genérico ou alteração global de otimização foi usada.

Modelo e estruturas são reutilizados no stream; reset recompõe também variáveis
streaming, conforme a PoC. Filas, buffers, duração de comando, downloads e TTS
têm limites. A espera não solicita wake lock próprio. Operações de sessão usam
wake lock parcial com timeout de segurança e liberação em todos os caminhos;
sua duração agregada é exportada. O comportamento real de suspensão/captura
precisa ser medido no telefone; não se presume DSP ou consumo de outro assistente.

Permissão/papel ausente, microfone silenciado, chamada, rota indisponível e falhas
de captura são representados como indisponibilidade. Há recuperação limitada e
sinais passivos do sistema; a escuta não disputa prioridade por usar fonte
VOICE_COMMUNICATION. Regras de compartilhamento e silenciamento variam entre
versões/fabricantes, exigindo a matriz física abaixo.

## Modelo e paridade

O artefato congelado tem 39.552 bytes, SHA-256
`f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514`.
Importação e carga validam integridade e contrato do runtime antes da ativação.
Não há pesos no Git/APK público, treinamento, novos thresholds ou outro provedor
de STT/TTS. Fontes e dependências reutilizadas têm atribuição/licenças preservadas.

Ver [investigação Android/Home Assistant](android-hands-free-research.md),
[paridade e mecanismo ARM64](detector-parity.md) e
[resultados estruturados](detector-parity-results.json). A execução Linux,
compilação ARM64 e execução no telefone são evidências distintas. Diferenças
numéricas entre kernels são registradas mesmo quando os eventos concordam.

## Métricas e bateria

Em **Áudio e voz → Hands-free → Exportar métricas**, escolher um destino privado
para JSON. O formato `schema: 1` inclui versão/build, modelo/configuração,
agregados por data local, estado/dispositivo e eventos de transição limitados.
Não inclui comandos, transcrições, áudio, screenshots ou dados de outros apps.

Durações usam relógio monotônico, timestamps carregam fuso e mudanças de relógio
são sinalizadas. Após morte do processo, a lacuna desde o checkpoint é desconhecida;
ela não é somada à captura confirmada. Há retenção local limitada, agregação em
memória e persistência espaçada/eventos de estado. O JSON permite comparar escuta
solicitada, captura/detecção confirmadas, indisponibilidade, pausas, ativações,
resultados de comandos e latências agregadas.

O [baseline permanente](battery/baseline-2026-10-25_30/README.md) conserva os
seis dias originais e estimativas separadas. Seguir o
[protocolo de três dias](battery/three-day-protocol.md), excluindo 31/10 e qualquer
dia parcial de ativação. Não manter ADB/profiling durante o uso nem resetar
batterystats. Consumo baixo com serviço indisponível não representa eficiência.

## Homologação física ainda necessária

- Atualização assinada sobre a instalação do proprietário: identidade, pareamento,
  credenciais, preferências e conversas preservados.
- Replay ARM64 e chamada real sem pausa, silêncio/falsa ativação, voz baixa e ruído.
- Tela bloqueada/desligada, outro aplicativo, processo morto, reboot e retomada.
- Negar/conceder/revogar microfone, chave global, mudar assistente e alternar ativação.
- Ligação telefônica, outro gravador, headset/Bluetooth e mudança de rota sem
  forçar a perda do microfone concorrente.
- Beep não truncando pedido, VAD, timeout/limite, STT/backend/TTS indisponíveis,
  cancelamentos e painel sem desbloqueio.
- Reprodução nativa, conclusão real, continuação antes/depois do limite e nenhuma
  ativação pela própria voz; três dias completos de disponibilidade e bateria.

Não foi declarado funcionamento no aparelho do proprietário. Ver
[verificações, commits e publicação](validation-and-publication.md) para a
evidência de software e o estado efetivo da distribuição de desenvolvimento.
