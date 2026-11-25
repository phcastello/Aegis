# Avaliação do verificador de wake word em 16 de novembro de 2026

A alternativa avaliada mantém o microWakeWord Strong como detector contínuo e
executa um segundo detector, openWakeWord, **somente quando o Strong produz um
candidato**. O candidato só é apresentado ao usuário depois da confirmação.
Nenhum peso foi alterado ou treinado nesta rodada: os modelos já existentes
foram reutilizados, com uma nova política de decisão e validação nos dados
rotulados pelo proprietário.

A cascata escolhida preservou todas as 97 chamadas que o Strong redetectou nos
107 contextos positivos e reduziu os contextos negativos ativados de 65 para
28, nos 153 negativos: **37 rejeições adicionais, sem perda adicional observada
de chamadas**. A redução de 56,9% descreve este conjunto conhecido, não uma
promessa de redução de alarmes por hora no uso cotidiano.

## Protocolo e seleção

- Os 260 eventos conservam os rótulos humanos e os splits fornecidos: treino
  91 positivos e 130 negativos; validação 11/15; teste 5/8.
- Os conjuntos já foram consultados em rodadas anteriores. O teste desta rodada
  não é um novo holdout cego e as chamadas são condicionadas às ativações que
  o detector original conseguiu registrar.
- Desfechos como transcrição vazia, cancelamento ou falha não são convertidos
  em rótulos de falso positivo. Dados novos sem rótulo humano não foram usados
  como verdade de treino ou de avaliação.
- A comparação usa o replay nativo Strong preservado, com conferência dos hashes
  de modelo, inventário e PCM. Os scores por frame reproduzem exatamente os
  eventos anteriores antes de aplicar qualquer política nova.
- Antes de abrir os resultados desta rodada, foram definidos dois verificadores
  openWakeWord, thresholds 0,5/0,7, e confirmações temporais de três/cinco strides.
- A seleção foi feita na validação: openWakeWord 0,5 manteve as dez chamadas
  reconhecidas pelo Strong e rejeitou cinco dos sete falsos contextos. O threshold
  0,7 empatou; ficou 0,5, sem aumentar a exigência quando não havia ganho.
- Essa escolha foi registrada em `selection-before-test.json` antes do replay
  completo. O resultado posterior de 0,7 não foi usado para trocar a escolha.

A confirmação temporal de três strides perdeu uma chamada na validação sem
reduzir os sete falsos contextos. Cinco strides perderam três chamadas e
rejeitaram somente um falso contexto adicional. Essas alternativas foram
rejeitadas. O grid de thresholds do Strong de 05/11 já mostrava que aumentar
apenas o threshold não oferecia um compromisso melhor na validação; ele não foi
repetido como se fosse uma nova solução.

## Resultados da cascata selecionada

Cada célula de positivos usa reconhecimento perto da decisão original. Negativos
contam qualquer evento no contexto inteiro. Os resultados abaixo são do runtime
nativo do Strong e do verificador ONNX CPU 1.20.1.

| Split | Positivos Strong | Positivos da cascata | Negativos ativados Strong | Negativos ativados pela cascata |
| --- | ---: | ---: | ---: | ---: |
| Treino | 84/91 | 84/91 | 52/130 | 25/130 |
| Validação | 10/11 | 10/11 | 7/15 | 2/15 |
| Teste conhecido | 3/5 | 3/5 | 6/8 | 1/8 |
| Total diagnóstico | 97/107 | 97/107 | 65/153 | 28/153 |

As dez chamadas já perdidas pelo Strong continuam perdidas. A cascata complementa
o detector; não recupera chamadas que não chegam a produzir um candidato.

## Regressões conhecidas e controles

Os mesmos pesos e threshold 0,5 foram usados sem ajustes após a seleção.

| Protocolo | Strong | Cascata selecionada |
| --- | --- | --- |
| PoC isolada, chamadas | 22/23 | 22/23 |
| PoC isolada, negativos ativados | 0/8 | 0/8 |
| PoC contínua, chamadas | 22/23 | 22/23 |
| PoC contínua, negativos ativados | 1/8 | 1/8 |
| Público completo, 35 minutos | 3 eventos | 0 eventos |

A PoC isolada conserva a cauda de 0,5 segundo do protocolo original. A contínua
conserva o prefixo público de 30 segundos e os intervalos artificiais de três
segundos, sem reset do Strong. O verificador não acrescenta padding à sua janela.
Esses resultados não são somados aos 260 eventos como novos exemplos independentes.

O corpus público contém 30 minutos de fala lida em português brasileiro
(FLEURS) e cinco minutos de sala/TV (DEMAND-DLIVING). Os três eventos Strong
rejeitados pela cascata pertenciam à fala lida. Na faixa de sala/TV já não havia
eventos Strong, portanto não se mede uma melhoria nesse cenário. Não existe
corpus separado de música nesta avaliação. Os 153 negativos humanos dos 260
eventos não têm categoria de fonte; não podem ser divididos retrospectivamente
em pessoas, música, TV ou vídeos sem nova anotação humana.

Dois controles adicionais de 42.560 amostras foram verificados. Na região de
confirmação, silêncio teve score máximo 0,0000428 e ruído determinístico de baixa
amplitude 0,00000251. São controles restritos, sem equivaler a ambientes reais.

## Janela e implementação

Para uma decisão Strong na amostra absoluta `E`:

- Início da referência: `max(0, E - 40000)`, até 2,5 segundos antes.
- Fim: `E + 2560`, até 160 ms depois, limitado ao áudio realmente disponível nos
  replays de arquivos. Não há preenchimento com zeros.
- O verificador consome blocos inteiros de 1.280 amostras, ou 80 ms. Uma sobra
  final parcial não é preenchida nem processada.
- Considera-se o maior score dos blocos cujo fim esteja entre `E - 7680` e
  `E + 2560`, com aceitação em 0,5.
- A referência Python preserva o pré-processamento do openWakeWord: mel `/10+2`,
  76 frames por embedding, 16 embeddings por classificação e cinco previsões
  iniciais zeradas. A inicialização usa um estado fixo de ruído determinístico,
  exportado em 6.144 bytes, sem gravações pessoais.

O port ONNX direto produziu os mesmos scores do adapter upstream, com diferença
máxima **zero**, em 162 janelas reais de candidatos e dois controles. Foram
exportadas 164 fixtures privadas e a paridade foi executada com Kotlin/JVM e
ONNX Runtime **1.31.1**: todos os frames ficaram na tolerância de `1e-4` e as
decisões de aceitação em 0,5 foram idênticas. A suíte hands-free real passou
com **189 testes em 17 suítes, zero falhas e zero skips**, incluindo os quatro
testes do verificador e os seis testes do helper assíncrono. A cobertura inclui
a remoção de candidatos cancelados da fila enquanto o worker permanece ocupado.
Pesos, PCM e scores
individuais ficam fora do Git e do APK.

A confirmação ocorre em um worker com no máximo um candidato pendente.
A thread do microfone continua lendo enquanto a inferência executa. O contexto
anterior e todas as amostras do comando desde a decisão original são preservados
em buffers limitados. O helper não apresenta beep, painel ou STT antes da
aceitação; rejeição, falha, cancelamento e timeout não autorizam um comando.

`HandsFreePendingWakeVerification` preserva até 320.000 amostras de comando,
com timeout padrão de dois segundos. Uma conclusão posterior ao limite é
rejeitada. Tarefas canceladas são removidas da fila do executor para que um worker lento
não deixe um candidato expirado bloqueando a próxima submissão.
O cancelamento não zera a janela enquanto uma chamada nativa ainda
pode utilizá-la: o worker limpa sua própria cópia ao sair. A aceitação conserva a
decisão `E`; não desloca o início do comando para depois da inferência.

## Custo e limites da medição

Nos 162 candidatos do replay completo, o loop ONNX executou por 13,86 segundos,
ou média de **85,6 ms por candidato**, no CPU do servidor 22. A medição exclui
criação das sessões e reset do adapter upstream. Ela não mede o Android físico,
o atraso acústico do beep, o gasto de bateria nem percentis de latência. O replay
Strong usa o host nativo preservado; a paridade JVM do verificador não equivale
a testar a captura e o runtime completos em um Android físico. Em um
aparelho que exceda o prazo de dois segundos, o candidato é rejeitado por
timeout e a ocorrência entra nas métricas; não há aceitação automática por demora.

Não há inferência do verificador durante a espera pela wake word. As sessões
Kotlin usam CPU com uma thread, execução sequencial e spinning desabilitado;
a telemetria do runtime é desligada. As sessões carregadas ainda ocupam memória.
O consumo adicional das confirmações deve ser confrontado com a economia de
ativações rejeitadas e com o custo da captura contínua no telefone.

O algoritmo não cancela música ou eco: essa funcionalidade continua pertencendo
à etapa 2. Aqui, a melhoria é a confirmação local do candidato da primeira etapa.

## Pacote privado e reprodução

Raiz dos artefatos no 22:
`/var/tmp/aegis-wakeword-verifiers-20261116/`.
O pacote é `package-v1/aegis-strong-verifier-20261116.zip`, com 2.278.302 bytes e
SHA-256 `47df896407b8fc7735253f606e66d747c6c043ddb1cfa64c1b8079355aa6c555`.

| Entrada | Bytes | SHA-256 |
| --- | ---: | --- |
| `aegis.int8.tflite` | 39552 | `1e7c686420f65d8da892f02c71d945f03a8dd528a1005de8c9c8482dce52ad2e` |
| `melspectrogram.onnx` | 1087958 | `ba2b0e0f8b7b875369a2c89cb13360ff53bac436f2895cced9f479fa65eb176f` |
| `embedding_model.onnx` | 1326578 | `70d164290c1d095d1d4ee149bc5e00543250a7316b59f31d056cff7bd3075c1f` |
| `aegis.onnx` | 414743 | `ec21cd554897f2f3cbb80962cfc2dd9ab32f4800692dfabf4e50ea2a1659da56` |
| `initial_features.f32le` | 6144 | `b4ee1271c05d64fed3032f8ddac4d9731c06e03e142052cf231858657e86df16` |

O manifesto tem schema 1, kind `aegis-wake-verifier`, versão
`aegis-strong-oww-2026-11-16`, threshold 0,5 e os cinco hashes/tamanhos.
A importação de TFLite bruto continua disponível para rollback sem verificador.
O pacote privado não é um asset público da release.

Entradas de reprodução:

- `evaluate_wakeword_verifiers.py`: compara as políticas na validação e no replay
  Strong preservado; escreve protocolo e resultados privados fora do checkout.
- `openwakeword_verifier_reference.py package`: exporta o pacote privado e estado.
- `openwakeword_verifier_reference.py fixtures`: confere paridade upstream e
  exporta janelas/scores para o teste Kotlin.
- `evaluate_legacy_wakeword_verifier.py`: aplica a configuração congelada às
  regressões conhecidas, sem nova seleção.
- `NodeHandsFreeVerifierTest`: paridade de frames e decisões, entrada intacta,
  isolamento entre candidatos, cancelamento e contratos de tamanho.
- `NodeHandsFreePendingWakeVerificationTest`: captura enquanto o worker está
  pendente, rejeição sem comando, deadline, limites e propriedade do PCM.

A ativação no telefone e a redução real dos alarmes permanecem verificações
físicas. O pacote de métricas mais recente sem rótulos não substitui essa
avaliação humana prospectiva.
