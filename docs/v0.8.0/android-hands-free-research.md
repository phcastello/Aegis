# Aegis v0.8.0 — investigação Android e Home Assistant

Investigação em 31/10/2026, anterior à decisão de integração. O snapshot
`4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267` existe e foi obtido por Git e
conferido pela API do GitHub. É o HEAD observado da `main` nessa consulta,
commit de 30/10/2026 sobre Fastlane. Os links de código abaixo fixam esse SHA;
referências de issues e APIs representam o estado consultado nesta data.
A investigação orienta a implementação; não demonstra funcionamento físico.

## Respostas que orientam a implementação

1. **Menor conjunto reutilizável.** O módulo `microwakeword` separa frontend C,
   engine C++ com TFLite Micro, JNI e um wrapper Kotlin. Reutilizar esse núcleo
   com atribuição, dependências fixadas e adaptações de contrato é menor que
   importar o aplicativo, Hilt, Compose ou a infraestrutura de servidores do HA.
   Captura, sessão, rede e TTS permanecem responsabilidades da Aegis.
   [Módulo](https://github.com/home-assistant/android/tree/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/microwakeword).

2. **Compatibilidade do modelo.** A engine verifica FlatBuffer, entrada INT8
   `[1,stride,40]`, saída UINT8 `[1,1]`, usa resource variables e registra os
   operadores streaming. Isso é compatibilidade de interface esperada, não
   prova de execução correta do modelo privado de 39.552 bytes. O replay nativo
   contra o runner congelado deve verificar grafo, memória e scores; compilar
   ARM64 também não prova paridade ARM64. O CMake fixa TFLM
   `2747abd5c82a95fb1624106a946fc671c31f16e8`, FlatBuffers, gemmlowp, ruy e
   kissfft por SHA e hash do arquivo. Os testes instrumentados upstream validam
   entradas inválidas; não executam o modelo Aegis.
   [Engine](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/microwakeword/src/main/cpp/MicroWakeWordEngine.cpp),
   [CMake](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/microwakeword/src/main/cpp/CMakeLists.txt),
   [testes JNI](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/microwakeword/src/androidTest/kotlin/io/homeassistant/companion/android/microwakeword/MicroWakeWordInstrumentedTest.kt).

3. **Carga, reset, aquecimento e eventos.** O HA copia o modelo uma vez em RAM,
   lê stride e quantização da entrada, acumula features e invoca a cada stride.
   Frontend: PCM16 mono 16 kHz, janela 30 ms, passo configurável, 40 bins,
   125–7500 Hz, noise reduction/PCAN/log fixados; converte saída do frontend por
   `0.0390625` antes da quantização. A regra HA usa média UINT8 e cutoff
   truncado para 0–255, comparação estrita, 100 inferências abaixo do cutoff
   antes de liberar detecção e cooldown Kotlin de 200 blocos de 10 ms.
   `reset()` limpa frontend, stride e janela, mas não recria interpreter nem
   as variáveis do grafo. `processAudio()` retorna no primeiro evento e pode
   descartar frames posteriores do bloco. Essas regras diferem da PoC: a Aegis
   deve preservar sua média de cinco scores, threshold 0,5, latch/histerese,
   refratário de um segundo, tratamento de parciais e reset equivalente a
   instância nova. Copiar regras HA silenciosamente mudaria o detector.
   [Frontend](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/microwakeword/src/main/cpp/MicroFrontendWrapper.cpp),
   [listener](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/app/src/main/kotlin/io/homeassistant/companion/android/assist/wakeword/WakeWordListener.kt),
   [referência PoC](../v0.8.0-wakeword-comparison.md).

4. **Uma captura.** O `VoiceAudioRecorder` usa mutex e flow compartilhado para
   fornecer o mesmo `AudioRecord` aos consumidores. Leitura bloqueante roda em
   IO; o último unsubscribe libera a captura. No snapshot, `replay=0` com
   `WhileSubscribed()` sem atraso perde áudio quando não há coletor. O serviço
   cancela o listener antes de abrir a sessão. A Aegis deve manter o proprietário
   nativo e o buffer circular vivos na passagem wake word → comando, coordenar
   gravação manual e não depender da inscrição de uma UI. O fallback HA de
   44,1 kHz interpola cada bloco isoladamente; não copiá-lo sem validar fase e
   continuidade entre blocos parciais.
   [Recorder](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/common/src/main/kotlin/io/homeassistant/companion/android/common/util/VoiceAudioRecorder.kt).

5. **Papel e permissões.** O Android mantém o `VoiceInteractionService`
   selecionado pelo usuário para hotwords. O serviço deve ser leve; trabalho
   de sessão pertence ao serviço associado. HA inicia FGS de microfone com
   notificação e verifica `RECORD_AUDIO`. Declarar FGS/microphone não elimina
   restrições while-in-use: a documentação lista explicitamente o app que
   fornece `VoiceInteractionService` entre as exceções. O consentimento ocorre
   pelas configurações oficiais de assistente; não há concessão silenciosa.
   [API VIS](https://developer.android.com/reference/android/service/voice/VoiceInteractionService),
   [restrições/exceções](https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start),
   [tipos FGS](https://developer.android.com/develop/background-work/services/fgs/service-types),
   [seleção HA](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/app/src/main/kotlin/io/homeassistant/companion/android/settings/assist/DefaultAssistantManager.kt).

6. **Apresentação sobre apps e bloqueio.** `showSession()` abre a sessão ligada
   ao interactor. Manifest HA protege serviço, sessão e recognizer com
   `BIND_VOICE_INTERACTION`; XML declara sessão/recognizer, `supportsAssist` e
   `supportsLaunchVoiceAssistFromKeyguard`. O recognizer é mínimo, retornando
   erro de cliente; não troca STT. HA abre `AssistActivity` na sessão e agenda
   `finish()` depois de `onShow()` para não invalidar o token antes da janela.
   A Aegis deve apresentar seu indicador na sessão nativa, sem lançar WebView
   para capturar ou exigir `SYSTEM_ALERT_WINDOW`; não solicitar screenshots nem
   conteúdo de outros apps.
   [Manifest](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/app/src/main/AndroidManifest.xml),
   [XML](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/app/src/main/res/xml/voice_interaction_service.xml),
   [sessão HA](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/app/src/main/kotlin/io/homeassistant/companion/android/assist/service/AssistVoiceInteractionSession.kt),
   [API Session](https://developer.android.com/reference/android/service/voice/VoiceInteractionSession),
   [API SessionService](https://developer.android.com/reference/android/service/voice/VoiceInteractionSessionService).

7. **Recuperação.** Inicializar quando `onReady()` confirmar o binding; liberar
   em shutdown/destroy, inclusive em falhas. O Android documenta `onReady()`
   tardio após destruição; HA usa latch de teardown para impedir reinicialização
   da instância morta. Troca de assistente invalida a captura; permissão negada,
   chave global do microfone, chamada e captura silenciada devem aparecer como
   indisponibilidade. Recuperar por binding oficial, mudança real de condição
   e ação explícita, sem loop de retries. Após morte do processo, intervalo
   anterior sem evidência é desconhecido, não captura confirmada. A seleção de
   assistente pode ser desfeita nas configurações do Android como recuperação.
   [Serviço HA](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/app/src/main/kotlin/io/homeassistant/companion/android/assist/service/AssistVoiceInteractionService.kt).

8. **Limites do sistema e upstream.** Papel de assistente não concede DSP nem
   privilégios do assistente pré-instalado. A política de compartilhamento
   pode entregar silêncio; `AudioRecordingConfiguration.isClientSilenced()` e
   callbacks de gravação/rota ajudam a distinguir isso de silêncio ambiente.
   Chamadas recebem prioridade. A Aegis não deve tomar o microfone de outro
   app. HA considera a feature experimental e avisa sobre consumo de CPU e
   bateria; seus resultados não estimam consumo no telefone do proprietário.
   [Compartilhamento](https://developer.android.com/media/platform/sharing-audio-input),
   [guia HA](https://www.home-assistant.io/voice_control/android/).

## Relatos de perda e atraso: estado e correção proposta

A issue [#6943](https://github.com/home-assistant/android/issues/6943) estava
**aberta**. O relato original usa Android 9 e HA 2026.4.4-minimal. Um comentário
posterior associa a perda ao intervalo sem coletor e a `replay=0`, medindo cerca
de 636–649 ms em cold start em outro aparelho. Propõe replay em RAM e atraso
na liberação. Trata-se de reprodução e proposta do relator, não aceite geral
nem correção já integrada.

A issue [#7683](https://github.com/home-assistant/android/issues/7683) também
estava **aberta**. Relata áudio atrasado, acúmulo de fila e mensagens após o
handler STT ter encerrado, em mais de um tablet. O PR
[#7720](https://github.com/home-assistant/android/pull/7720), **aberto e não
integrado**, foi examinado no head imutável
`b1c6a434b6fd8fa0a8654b5bdbfd28ba6dd3c8c0`. O diff move encaminhamento para
background, agrega blocos por envio, interrompe a fila ao fim de STT e impede
que consumidor lento bloqueie o recorder com buffer limitado/drop-oldest.
O diff continua usando um canal ilimitado para envio e não conserva o recorder
na passagem entre serviço e sessão. Portanto, mesmo essa proposta não é a
implementação de continuidade e limites escolhida para a Aegis.
O autor mediu a passagem pela main como gargalo no aparelho de teste;
comentários posteriores relatam melhora, um outlier e outro dispositivo ainda
sem teste do PR. Não atribuir uma causa única a todas as versões.

A Aegis evita essa arquitetura: leitura/inferência fora da main, captura do
comando limitada e envio WAV ao STT existente, sem fila infinita de pacotes de
10 ms. A UI não recebe frames de áudio. O evento mantém a captura e os samples
já recebidos enquanto a sessão aparece. O sinal local não pode ser aguardado
antes de continuar a leitura. A resposta suspende a detecção até o fim real
da reprodução; continuação usa até dois segundos para **início**, não duração
total da fala. Essas são decisões da Aegis, não cópia do PR.

## Licenças e limites de reaproveitamento

O repositório HA contém [Apache-2.0](https://github.com/home-assistant/android/blob/4c02fda3645e9a19bc0c52f8c6c994dd3fb1a267/LICENSE.md).
Preservar texto da licença, autoria/proveniência do módulo e identificação das
adaptações. O CMake puxa TFLM/FlatBuffers/gemmlowp/ruy (licenças próprias) e
kissfft (BSD); conservar seus avisos na distribuição e a referência dos
commits. Não incluir pesos privados, áudios nem modelos públicos do HA.
O runtime e frontend são reutilizáveis; cutoff, aquecimento, reset e eventos
precisam obedecer ao contrato Aegis, sem assumir equivalência por usar `.tflite`.

## Evidência exigida depois da integração

Replay com WAVs privados e teste contínuo deve registrar hashes/configuração,
diferença de scores, eventos e tolerância, incluindo reset versus instância
nova e blocos parciais. Resultado de host x86 não substitui ARM64. Sessão sob
bloqueio, concorrência, Bluetooth/rotas, revogação, reboot, chave global,
ausência de autoativação pelo TTS e disponibilidade de dias completos exigem
homologação física. Não há alegação de aceite do aparelho neste documento.

Medições curtas separadas podem usar [dumpsys](https://developer.android.com/tools/dumpsys)
para serviços, áudio e bateria, guardando dumps completos em privado.
[Power Profiler](https://developer.android.com/studio/profile/power-profiler)
usa ODPM apenas em hardware compatível; a documentação cita Pixel 6 ou posterior,
portanto não se presume disponibilidade em Samsung/One UI. O acompanhamento
naturalístico usa exportação agregada e capturas diárias, sem ADB contínuo,
reset de batterystats ou profiling permanente.
