# Verificação e publicação do incremento hands-free

Verificações em 31/10/2026, na worktree isolada `/home/phc/Aegis-hands-free`.
Os logs completos de desenvolvimento ficam em `/tmp/aegis-hands-free-*.log`;
áudios, pesos e relatórios acústicos completos permanecem em armazenamento privado.

| Verificação | Resultado e alcance |
| --- | --- |
| Baseline | ZIP/CRC, caminhos e hashes dos seis JPGs conferidos; dados exatos e cálculos conferidos; originais privados. |
| `dotnet test backend/Aegis.sln --no-restore` | 758 aprovados, 99 opcionais ignorados, zero falhas. Integrações opt-in de banco/provedores não foram executadas nessa chamada. |
| Subconjunto backend de voz/ferramentas/prompts | 54 aprovados, zero falhas/skips. Modelo falso, EF em memória e contratos; sem ações reais. |
| `npm test --prefix apps/aegis-node` | 151 aprovados, zero falhas/skips, após a correção do harness de identidade. |
| `npm test --prefix frontend/aegis-pwa` | 91 aprovados, zero falhas/skips; inclui testes compartilhados já contados no Node. |
| Builds Node e PWA | Typecheck e bundles de produção aprovados, versões sincronizadas em 0.8.0. |
| `test:transcription-browser` | Chromium/MediaRecorder/WebAudio reais com sinal sintético: PWA, Android simulado e Windows simulado aprovados. Inclui configurações hands-free Android, leases, silêncio, cancelamentos, rascunhos e navegação. |
| Identidade visual da versão | 30 cenários dos Apps reais em Chromium e seis versões de runtime aprovados; cinco testes focados de identidade aprovados. Mantém a identidade conhecida 0.7 e o fallback previsto para 0.8, sem inventar codinome. |
| `test:rust` | 85 aprovados, dois testes de backend real ignorados; zero falhas. |
| `cargo check --target aarch64-linux-android --locked` | Aprovado com compilador NDK, após configurar os caminhos locais; não executa ARM64. |
| `test:release` | 51 contratos de distribuição/versão aprovados. |
| `:app:testArm64DebugUnitTest` | 107 testes em 16 suítes, zero falhas/erros/skips; inclui 15 de transporte, cinco de áudio e nove de métricas. JVM, sem microfone físico. |
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
- `4b8fa5c`: instruções de ativação, limitações e protocolo operacional.
- `61bbccd`: invalidação imediata de cancelamento antes da limpeza de rede e
  reset determinístico ao retornar à wake word sem comando.
- `f916703`: registro dos testes finais da primeira tentativa de publicação.
- `446992d`: janela nativa explicitamente limitada ao painel inferior, conforme
  dimensões padrão confirmadas no código do Android.
- `3ba0410`: harness de identidade compatível com a família efetivamente instalada.

Todos esses commits foram enviados a `origin/feat/v0.8.0-hands-free`, sem force
push, PR automático ou merge na main. A branch experimental e a worktree original
permaneceram preservadas.

Primeira operação: `python3 scripts/publish-dev.py --ref feat/v0.8.0-hands-free`,
fonte `f9167035572e58d771b87672a13db828f90107d3`, evidência privada
`~/.local/state/aegis/publish-dev/20261031T135953Z-5612372c/`.
API e PWA ficaram saudáveis nesse SHA, com HTTPS, service worker/API v1 e workers
verificados. Nenhuma migration estava pendente; não houve backup, ensaio de
restauração ou migrador desnecessário.

A operação despachou o
[run 41208280622](https://github.com/phcastello/Aegis/actions/runs/41208280622),
reservando `0.8.0-unstable.1`, código `8000001`. O build Android release com
R8/NDK terminou; o smoke Windows falhou porque o harness exigia literalmente o
codinome da família 0.7 em uma versão 0.8. A instalação e os checks anteriores de
perfil/armazenamento concluíram nessa execução, mas a matriz completa não passou.
A assinatura/publicação foi pulada e o recomendado anterior permaneceu intacto.
A reserva foi consumida e não será reutilizada.

O harness passou a distinguir versão instalada, família conhecida e fallback,
conservando as verificações de armazenamento, IPC, versão e layout. Também foi
corrigido o dimensionamento real da janela de assistente: o framework criava
MATCH_PARENT e somente gravity não limitava a altura. Os testes/browser e o
APK ARM64 foram repetidos nas camadas alteradas. Não houve alteração do modelo
ou do detector nessa correção.

Segunda operação solicitada com a fonte
`3ba041005111689f07547afaa8ce3f6de5424822`, pelo mesmo `publish-dev`.
Evidência privada:
`~/.local/state/aegis/publish-dev/20261031T141656Z-d7cba2bf/`.
A operação atualizou somente PWA e nativos; a API permaneceu no SHA `f916703`,
pois não houve novo diff de backend. A PWA ficou no SHA `3ba0410`; os checks de
HTTPS, service worker e API v1 passaram novamente, sem migrations pendentes.

**Publicação concluída:**
[run 41209494408](https://github.com/phcastello/Aegis/actions/runs/41209494408)
terminou com sucesso, incluindo build Android release/minificado, build Windows,
instalação real no runner Windows sobre o perfil anterior, assinatura isolada e
publicação. O estágio extenso de acceptance foi intencionalmente pulado no fluxo
de preview; isso não foi contado como homologação de release ou teste físico.

O [feed recomendado](https://raw.githubusercontent.com/phcastello/Aegis/aegis-distribution/latest.json)
avançou efetivamente para **0.8.0-unstable.2**, código Android **8000002**,
fonte **3ba041005111689f07547afaa8ce3f6de5424822**,
compatibilidade `api: 1, nodeData: 1`. A versão está disponível pelo fluxo normal
de Atualizações do Node e por instalação manual oficial sobre a instalação atual.

| Artefato público conferido | SHA-256 dos bytes baixados, igual ao feed |
| --- | --- |
| [Android ARM64](https://github.com/phcastello/Aegis/releases/download/node-v0.8.0-unstable.2/Aegis-Android-arm64.apk) | `13e6c868394ea01b05d2654f451a49208497d45482fb9db9fffceb2e58c7cbd2` |
| [Windows x86_64](https://github.com/phcastello/Aegis/releases/download/node-v0.8.0-unstable.2/Aegis-Windows-x86_64-Setup.exe) | `4c7a3cf7833ae649d00707d89282b5f8f54d04642d635a802c48137055388fde` |

O APK publicado passou na verificação criptográfica do `apksigner`: pacote
`com.aegis.node`, versão/código corretos, um único certificado oficial SHA-256
`df5286c556657ddcee6ce79c510b967678bac8ced18d082db0e318bc47debb31`,
igual ao APK oficial v0.7.2 baixado e verificado separadamente. A inspeção com
AAPT2 conferiu flags de release/cleartext e ausência de instrumentação; o guard
de arquivos passou. O verificador APKAnalyzer local não estava instalado, e não
foi declarado como executado localmente; o workflow mantém seus guards próprios.

O APK oficial contém `libaegis_microwakeword.so` ARM64 e oito arquivos de licença
idênticos aos versionados. Não contém o modelo privado, áudios de avaliação,
CLI/probes ou bridge Python. O resumo público verificável está em
[publication.json](publication.json); o relatório detalhado permanece em
`~/.cache/aegis-hands-free/publication-audit/0.8.0-unstable.2-audit.json`.

Não foi instalado no telefone do proprietário. Homologação física, replay ARM64,
comportamento de rotas/chamadas e os três dias de bateria continuam pendentes,
com mecanismos e protocolo entregues. O modelo privado precisa ser transferido
e importado uma vez, e o proprietário precisa conceder microfone/selecionar o
assistente pela interface oficial do Android.
