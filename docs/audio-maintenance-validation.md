# Captura de áudio após a v0.7.1

A01 e A02 foram corrigidos nesta branch; o diagnóstico e a escolha de entrada de
A03 foram implementados. O problema físico do microfone Windows do proprietário
continua com aceite pendente. Os instaladores publicados da v0.7.1 não recebem
estas mudanças até uma distribuição posterior.

Cada tentativa possui stream, recorder, chunks, timers, listeners, contexto de
áudio e AbortController próprios. Descartar invalida a tentativa antes de parar
recursos; callbacks antigos não podem iniciar upload, inserir texto nem encerrar
uma gravação nova. Parar libera o microfone imediatamente, aguardando os eventos
finais do recorder; um watchdog de cinco segundos trata a ausência de `onstop`.
Ocultar a página ou desmontar também cancela captura e transcrição. Abort é uma
tentativa de interromper o transporte; não desfaz bytes já enviados.

Em **Configurações → Sobre → Microfone**, a preferência fica no armazenamento
local daquela instalação. A seleção se aplica à próxima captura. Não há pedido
automático de permissão ao abrir as configurações. Nomes podem permanecer ocultos
até o usuário permitir uma gravação. Um ID salvo removido/alterado que gere
`NotFoundError` ou `OverconstrainedError` é descartado, e a captura tenta a entrada
padrão com aviso. Uma preferência nova salva enquanto essa tentativa aguarda
permissão é preservada para a próxima captura. Negar permissão não causa tentativa com outra entrada.

O nível usa amostras do stream realmente capturado. Ausência de sinal dá
orientação; `track.muted` indica falta temporária de dados e não prova mute físico.
Permissão negada, nenhum dispositivo, entrada salva indisponível, track encerrada,
falha do recorder, arquivo vazio, silêncio digital, transporte e erro de
transcrição têm caminhos distintos. Não são registrados áudio, transcrições ou
identificadores de entrada em logs.

Antes do upload, a gravação inteira é decodificada quando o formato é suportado.
Somente silêncio digital comprovado é bloqueado, sem limiar de fala: o piso
numérico de `1e-30` (-600 dBFS) tolera resíduos de ponto flutuante de Opus/WebAudio,
muito abaixo da resolução de PCM de 24 bits. O teste real do Chromium observou
aproximadamente `2e-34` ao decodificar uma fonte digital zero. Sinal sintético de
`1e-12`, muito menor que fala baixa, continua aceito. Uma palavra curta, silêncio
inicial e sinal em outro canal não são rejeitados. A amostragem esparsa do medidor
não é usada como prova de silêncio de toda a gravação. Decodificação indisponível
ou inconclusiva permite seguir com aviso quando aplicável; não afirma ausência de
fala nem elimina todas as possíveis alucinações do STT. Nenhum detector altera o
texto, o provedor STT, o protocolo de voz ou o TTS.

## Regressões locais

Use `npm ci` nos três pacotes com seus lockfiles. Na raiz:

```sh
node --test packages/aegis-client/tests/transcription.test.mjs
node packages/aegis-client/tests/transcription-browser.mjs
npm --prefix frontend/aegis-pwa run build
npm --prefix apps/aegis-node run build
```

A suíte de estado usa TypeScript 5.9.3 do lockfile para transpilar os módulos atuais.
Seus 17 testes cobrem os dois controles e três races históricas com expectativas
seguras, permissões resolvidas/rejeitadas em ambas as ordens, descarte antes e
durante inspeção/upload, retorno tardio, callbacks repetidos, reinício rápido,
visibilidade/unmount, recorder vazio/falho/sem `onstop`, track encerrada,
preferência/fallback, PCM silencioso/baixo/curto/após silêncio e limite máximo.
Verifica também ausência de timers, contexts e tracks ativos ao retornar ao repouso.
Todas as interfaces de mídia e rede dessa suíte são mocks.

O teste browser passou em Chromium nos aplicativos reais web e nativo, com IPC
Android e Windows simulado. Usa oscilador de amplitude 0.005 e fonte digital zero,
AudioContext/MediaRecorder reais e respostas STT locais simuladas. Confere medidor,
transcrição do sinal baixo, bloqueio do silêncio, descarte, preferência e cleanup.
Ele não abre o microfone físico nem usa APIs pagas. É possível indicar um Chromium
compatível por `PLAYWRIGHT_CHROMIUM_EXECUTABLE`.

O harness do ZIP foi executado somente em pasta isolada, usando a toolchain do
projeto por `NODE_PATH`, sem instalação histórica/global. O Git blob permaneceu
`6e33517b9e8f030cb81a8738e41d1b6fa1a28958`; os três defeitos antigos foram
reproduzidos. O exit code zero desse harness significa reprodução, não aprovação.

## Aceite físico pendente

O ambiente desta execução é Linux com Chromium; não disponibiliza Windows/WebView2,
aparelho Android, emulador Android ou o microfone do proprietário. O smoke Windows
instalado e o teste de áudio Android no hardware permanecem pendentes. As fixtures
de plataforma não equivalem a esses sistemas. Não foi diagnosticada a causa física
relatada; entrada padrão, permissões, WebView2, driver, mute e RDP são hipóteses.

No equipamento afetado: selecionar a entrada, gravar frase curta/baixa com silêncio
inicial, conferir o medidor e o texto; testar negar/reconceder permissão, desconectar
a entrada salva e cancelar/reiniciar rapidamente. Repetir em Android e confirmar
que ocultar/fechar libera o indicador de microfone do sistema. Fazer qualquer
captura física somente por ação explícita do proprietário.
