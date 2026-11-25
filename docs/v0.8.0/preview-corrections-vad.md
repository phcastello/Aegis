# Correção de evidência de fala, endpointing e sinal local

A preview `0.8.0-unstable.2` forçava `hasSpeech=true` na ativação. Isso não
comprovava a existência de pedido e também interrompia a adaptação de ruído.
Um único frame de energia aceitava o início da continuação; ruído constante
podia manter a captura até o limite. Os testes anteriores de silêncio não
usavam a inicialização real do controlador.

## Implementação

Wake word, evidência temporal de fala, áudio preservado e endpoint são conceitos
separados. Não existe mais `seedSpeech()`. O controlador inicializa
`HandsFreeCommandCapture` a partir do ring completo e usa esse mesmo helper nos
testes. Nenhum sample do pedido é apagado para eliminar o sinal de ativação.

O endpointing usa o pequeno **libfvad/WebRTC VAD**, modo 2, PCM16 mono 16 kHz,
frames internos de 20 ms. O wrapper consome as leituras bloqueantes existentes
de 200 ms e retém blocos parciais; não cria timers Kotlin de 20 ms, captura
concorrente, STT contínuo ou novos modelos. O classificador é processado somente
no comando e na continuação, incluindo o pre-roll na ativação.

Foi escolhida essa biblioteca porque energia isolada não separava ruído
estacionário e fala sem acrescentar regras acústicas improvisadas. A fonte fixa
é `532ab666c20d3cfda38bca63abbb0f152706c369`, arquivo SHA-256
`bf639e4a5bc37b1c90e047c42524e80a1d0cecadeb6e0a61a33580fca22a00ab`.
Não houve comparação de múltiplas bibliotecas, treino ou alteração do detector
de wake word. O libfvad usa GMM/filtros locais do WebRTC, sem pesos externos.
A compilação C isolada de referência no host resultou em biblioteca de cerca de
30 KiB; isso não equivale ao tamanho final incremental do APK nem a consumo
medido no telefone. Licença BSD-3-Clause, AUTHORS e concessão de patentes ficam
nos assets do APK. Fontes primárias:
[projeto libfvad](https://github.com/dpirch/libfvad/tree/532ab666c20d3cfda38bca63abbb0f152706c369),
[contrato C](https://github.com/dpirch/libfvad/blob/532ab666c20d3cfda38bca63abbb0f152706c369/include/fvad.h).

São necessários **80 ms de evidência consecutiva** de classificador e sinal
variável acima do piso adaptativo. Zero digital/DC e frames de hangover do VAD
sem sinal não viram evidência nova. O piso pode continuar a adaptar depois de
fala confirmada. O endpoint ocorre após 800 ms sem evidência, com limite de
20 s após a ativação, além do pre-roll. Sem fala, os três segundos de espera
começam na ativação; os 2,5 s de pre-roll não consomem esse orçamento.

`hasUsefulAudio` também preserva pre-roll variável incerto com pelo menos 40 ms,
mesmo se a classificação de fala não confirmar uma palavra curta ou voz baixa.
Silêncio digital comprovado e DC sem fala não são enviados. Uma invocação real
já contém fala: o VAD não consegue separar semanticamente toda wake word
isolada de todo pedido curto. Esses casos são preservados para STT; transcrição
vazia ou somente a invocação continua sem conversa e sem chamada ao LLM.

Na continuação, `HandsFreeContinuationGate` lembra a posição do início
provisório. Um início dentro de dois segundos pode receber os frames seguintes
necessários para confirmar os 80 ms. Uma fala confirmada depois do limite só é
aceita se tiver começado antes dele; um estalo isolado não estende a janela.
Os dois segundos não limitam a duração do pedido aceito.

## Sinal da Aegis

O tom padrão Android foi substituído por PCM local, armazenado em memória:
duas notas ascendentes de 16 ms, separadas por 8 ms, com envelope suave; duração
total de 40 ms. `HandsFreeActivationCue` escreve esse PCM em AudioTrack numa
fila de tamanho máximo um, fora da thread de UI/captura. Não espera o som
terminar antes de ouvir. Cancelamento invalida tarefas antigas e libera o player.
Não há rede, TTS ou silêncio artificial do microfone durante o sinal.

## Verificações executadas

- **11 testes Kotlin puros** usam o mesmo helper de ativação do controlador:
  silêncio após ativação, pre-roll longo sem gastar o timeout, pedido curto e
  baixo já inteiro no ring, fronteira de blocos, 20 ms de pulso/hangover,
  continuação começando em 1,98 s e confirmada depois de 2 s, timeout, limite de
  captura, parciais e conservação exata dos samples/WAV.
- **Cinco testes reais de JNI/libfvad no host** executam o código Kotlin de
  produção e o classificador nativo, sem mock do VAD. O sinal gerado e um pulso
  de 20 ms, em quatro alinhamentos, não confirmaram fala nem áudio útil quando
  o pre-roll era silencioso. Ruído gaussiano sustentado acima do limiar antigo
  encerrou antes de quatro segundos, sem atingir os 20 s.
- Reset após estado sujo teve as mesmas classificações que uma instância nova,
  preservando modo 2 e taxa de 16 kHz após o reset do libfvad.
- Uma gravação privada positiva, reduzida a 8% de amplitude, foi preservada
  inteira no pre-roll. Sinal e voz misturados atravessaram a ativação sem mute,
  corte ou mudança dos samples recebidos.
- Oito negativos originais disponíveis e os 30 s finais de ambiente real do
  corpus público existente foram processados. As nove capturas respeitaram
  limites e conservaram o pre-roll; **nenhuma atingiu o limite duro de 20 s**.
  Negativos de wake word podem conter fala: isso não é um teste de rejeição
  semântica pelo VAD nem uma nova avaliação do detector congelado.

Resultado: **16 testes aprovados**, sem falhas. Fonte da suíte adicional:
[`NodeHandsFreeRealVadTest.kt`](../../scripts/hands-free/NodeHandsFreeRealVadTest.kt).
Ela fica fora da suíte JVM Android ordinária porque exige JNI de host e o corpus
privado. O relatório/log dessa execução permanece em
`~/.cache/aegis-hands-free/vad-correction-final-20261031/`, sem exportação de
áudio, texto de comando ou conteúdo de outros aplicativos.

Para repetir, compilar a biblioteca de host com um JDK e
`-DAEGIS_BUILD_VAD_HOST_JNI=ON`, usando o mesmo CMake já entregue, e executar:

```sh
python3 scripts/hands-free/run-vad-host-tests.py \
  --library-dir "$HOME/.cache/aegis-hands-free/native-host" \
  --output "$HOME/.cache/aegis-hands-free/vad-tests-nova-execucao"
```

O script utiliza o compilador Kotlin e JUnit do cache das dependências Gradle
do projeto; não instala componentes permanentes nem inclui os WAVs no APK.
Um compilador C++17 completo e os headers JNI do JDK são necessários ao build
de host. O servidor de desenvolvimento usou os compiladores previamente
disponíveis no cache; a compilação Android usa o NDK existente.

O tamanho da biblioteca, testes sintéticos e execução JNI Linux não comprovam
classificação, latência acústica, roteamento ou reprodução no aparelho. Ruído
variável, música/TV, reverberação do próprio sinal e voz baixa com SNR reduzido
podem produzir erros. A homologação física e o replay ARM64 permanecem
identificados separadamente; não houve mudança de pesos ou tolerâncias da PoC.
