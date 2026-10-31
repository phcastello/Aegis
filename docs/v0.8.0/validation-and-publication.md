# Verificação e publicação do incremento hands-free

Verificações em 31/10/2026, na worktree isolada `/home/phc/Aegis-hands-free`.
Os logs completos de desenvolvimento ficam em `/tmp/aegis-hands-free-*.log`;
áudios, pesos e relatórios acústicos completos permanecem em armazenamento privado.

| Verificação | Resultado e alcance |
| --- | --- |
| Baseline | ZIP/CRC, caminhos e hashes dos seis JPGs conferidos; dados exatos e cálculos conferidos; originais privados. |
| `dotnet test backend/Aegis.sln --no-restore` | 758 aprovados, 99 opcionais ignorados, zero falhas. Integrações opt-in de banco/provedores não foram executadas nessa chamada. |
| Subconjunto backend de voz/ferramentas/prompts | 54 aprovados, zero falhas/skips. Modelo falso, EF em memória e contratos; sem ações reais. |
| `npm test --prefix apps/aegis-node` | 148 aprovados, zero falhas/skips. |
| `npm test --prefix frontend/aegis-pwa` | 88 aprovados, zero falhas/skips; inclui testes compartilhados já contados no Node. |
| Builds Node e PWA | Typecheck e bundles de produção aprovados, versões sincronizadas em 0.8.0. |
| `test:transcription-browser` | Chromium/MediaRecorder/WebAudio reais com sinal sintético: PWA, Android simulado e Windows simulado aprovados. Inclui configurações hands-free Android, leases, silêncio, cancelamentos, rascunhos e navegação. |
| `test:rust` | 85 aprovados, dois testes de backend real ignorados; zero falhas. |
| `cargo check --target aarch64-linux-android --locked` | Aprovado com compilador NDK, após configurar os caminhos locais; não executa ARM64. |
| `test:release` | 51 contratos de distribuição/versão aprovados. |
| `:app:testArm64DebugUnitTest` | 106 testes em 16 suítes, zero falhas/erros/skips; inclui 14 de transporte, cinco de áudio e nove de métricas. JVM, sem microfone físico. |
| `tauri android build --debug --apk --target aarch64` | APK ARM64 compilado com frontend/TFLM/JNI. Build local usa assinatura de desenvolvimento; não substitui a distribuição oficial para atualização do proprietário. |
| Conteúdo APK local | Bibliotecas Rust, microWakeWord e libc++ presentes; oito assets de licença; nenhum `.tflite`, `.h5`, `.wav` ou `.flac`. |
| JNI/ELF/R8 | Quatro exports JNI verificados no `.so` ARM64, segmentos alinhados em 16 KiB; regras explícitas preservam classes e cinco entradas JNI da Activity. A execução minificada/dispositivo exige a evidência correspondente. |
| Paridade nativa Linux | Reconhecimento e eventos preservados nos denominadores publicados; divergências numéricas estritas registradas, sem ampliar tolerância. Ver relatório específico. |
| Higiene | `git diff --check` e `py_compile` dos scripts de replay aprovados; nenhum peso/áudio privado versionado. |

O SDK temporário anterior estava incompleto. Componentes oficiais foram instalados
em `/tmp/aegis-hands-free-sdk`, com NDK 29.0.14206865 existente e JDK 25 local.
Arquivos gerados do Tauri não são artefatos públicos de teste. O build definitivo
regenera suas fontes; não depende desses caminhos locais.

O teste JVM do transporte usa servidor HTTP em loopback e identidade sintética,
sem acessar o cofre ou os serviços reais. Cobre multipart/WAV, IDs de sessão,
destino fixo, STT/chat/TTS falhos, respostas inválidas/grandes, redirects,
cancelamento, revogação e callbacks atrasados. Os testes de métricas injetam
relógios e persistência: confirmam intervalos desconhecidos após reinício,
mudança de relógio/fuso, divisão diária, bateria observada e transições limitadas
de configuração sem inventar nova ativação ao restaurar preferências.

O [relatório de paridade](detector-parity.md) distingue a comparação principal
com LiteRT/XNNPACK da comparação diagnóstica com kernels de referência. A
comparação numérica principal e a comparação estrita de features brutas têm
divergências explícitas. O replay ARM64 ainda não foi executado; o mecanismo
para isso foi entregue. Compilar o APK não certifica o detector no telefone.

Não havia aparelho conectado ao ADB nem ambiente ARM64 executável disponível.
Permissões, papel, chamadas, concorrência real, bloqueio/tela desligada, áudio
real, retomada após processo/reboot e bateria permanecem para a
[homologação física](hands-free.md#homologação-física-ainda-necessária).
Não houve reset de estatísticas de bateria, profiling contínuo, treino,
alteração de pesos, merge na main ou release estável.

## Commits e distribuição

- `28a421d`: baseline e investigação Android/Home Assistant.
- `d6a59bb`: contrato conversacional e continuação estruturada no mesmo turno.
- `577c238`: configuração e coordenação de áudio do cliente.
- `a000f5e`: sincronização da fonte 0.8.0.
- `856ac37`: runtime Android, assistente/captura/reprodução, modelo privado,
  métricas, testes e mecanismo de replay.

O resultado da publicação autorizada por `scripts/publish-dev.py` será registrado
após push e verificação do workflow/feed. Solicitar a execução não equivale a
publicação concluída; o APK local não é o instalador oficial.
