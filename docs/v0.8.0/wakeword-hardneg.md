# Aegis v0.8.0 — treinamento com hard negatives reais

Experimento local de 05/11/2026, branch `feat/v0.8.0-wakeword-hardneg`,
worktree `/home/phc/Aegis-wakeword-hardneg`, base `6c201ab`.
O checkout original tinha alterações locais e foi preservado. As worktrees
paralelas de TTS e aplicativo não foram modificadas. Este trabalho prepara
pesos candidatos privados; não altera o detector Android, a allowlist de
importação, o pipeline conversacional, `aegis-tts` ou o backend.

**Resultado: NO-GO para substituir o baseline.** Foram executados três fits,
com exportação INT8 e replay LiteRT/nativo. A redução de falsos positivos foi
expressiva, mas os três perderam uma chamada legítima da validação. O
retreinamento completo também perdeu um positivo antigo. Pesos e evidências
privados foram preservados para pesquisa, sem aprovação de promoção. No diagnóstico agregado das 15 chamadas novas,
o baseline reconheceu 15 e as candidatas reconheceram 11–12; manter o baseline.

## Dados e auditoria

Foi usado somente `aegis-dataset-2026-11-04_11-22-24-0300-datado.zip`, SHA-256
`e7bd46bff467a4b7248841e0782f4c9d17b2b139ca41ecac82ee0e1edce09357`.
São 161 eventos (146 `false_positive`, 15 `correct`), 322 WAVs e três arquivos
de inventário. CRC, caminhos, tipos de membros, duplicatas normalizadas,
CSV/JSONL, formatos, amostras e SHA-256 declarados passaram. A extração preserva
os bytes originais; nenhum rótulo humano foi alterado.

Todos os WAVs são PCM16 mono/16 kHz. Contextos somam 345,590 s; os áudios
posteriores somam 725,027 s, com sobreposição. Dez contextos têm menos de dois
segundos anteriores à decisão; nove são eventos falsos, incluindo cinco no
período de treino. Esses cinco ficam excluídos do treinamento. Não foram
observadas outras anomalias de captura pelos critérios instrumentais usados.
Todos os eventos têm versão `0.8.0-unstable.10`, build `8000010`, SHA do baseline
e parâmetros operacionais esperados. Identificadores, datas de captura,
encerramento, exportação e classificação passaram na verificação temporal.

O código de captura e a igualdade de PCM confirmam **200 ms de sobreposição
em todos os 161 pares**. O contexto contém até dois segundos anteriores à
decisão mais essa margem posterior; o pós começa na decisão. Os arquivos não
foram concatenados. Somente contexto anterior à decisão entra no treino.

Não há duplicatas PCM exatas. A busca de correlação em todos os pares de
arquivos de eventos diferentes encontrou 19 pares de pós-ativação com
NCC ≥ 0,90 por pelo menos 0,75 s. Todos desapareceram ao retirar os primeiros
500 ms. A concentração de energia em aproximadamente 40 ms indica provável
influência do sinal de ativação; **não foram classificados como conversas
duplicadas**. Nenhum par de contextos atingiu esse critério. Correlação de
forma de onda não encontra toda semelhança de fala nem identifica ambientes.

Os 161 `groupId`/`sessionId` são distintos. Foram formados 16 grupos pela união
de metadados, intervalos consecutivos de até 30 min e candidatos acústicos.
Nenhum grupo atravessou os períodos abaixo. A permanência em um mesmo ambiente
através de intervalos maiores não pode ser descartada.

| Split | Períodos locais UTC−03 | Falsos positivos | Legítimos |
| --- | --- | ---: | ---: |
| Treino | 03/11, 13–19 h | 57 | 4, somente avaliação |
| Validação | 03/11, 08–13 h | 25 | 4, somente avaliação |
| Teste | Demais períodos de 02–04/11 | 64 | 7, somente avaliação |

A separação foi congelada antes de qualquer treinamento. As lacunas naturais
entre os períodos reduzem mistura de conversas próximas. As 19 relações
acústicas já estavam em uma única sessão de teste. Todas as derivações herdam
o split. O replay do detector no teste só é executado depois da seleção da candidata.
Os positivos de teste receberam inspeção ASR prévia como auditoria de captura,
sem uso para treinar ou selecionar pesos; não se afirma que seu áudio tenha
ficado completamente intocado. A expressão "test unopened" do protocolo
refere-se ao replay do detector, com essa ressalva de auditoria.

## Corpus recuperado e desenho experimental

O baseline imutável é o MixConv de 13.057 parâmetros e 39.552 bytes, SHA-256
`f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514`.
O checkpoint `best.weights.h5` foi encontrado e carregado na arquitetura
original: o fine-tuning usa esses pesos, com Adam novo. Não se tenta recuperar
pesos treináveis a partir do TFLite quantizado.

Foram conferidos todos os 4.696 arquivos da proveniência anterior: 4.304 Piper,
seis recortes de referência e 386 FLEURS DEV. Os manifestos sintético e DEV
continuam respectivamente `b1b408dd…` e `39e95adc…`, com hashes completos no
manifesto privado. Os 37 WAVs antigos e o WAV público de 35 min mantêm seus
hashes. Os 23 positivos, oito negativos e FLEURS TEST/35 min públicos não entram
no treinamento, extração de negativos ou seleção desta rodada.

A fonte microWakeWord permanece limpa no commit
`4665173cd35f1cff9a61e06fc427f124766c488e`. Ambiente recuperado:
TensorFlow CPU 2.18.1, Keras 3.15.1, NumPy 2.0.2, pymicro-features 2.0.2,
LiteRT 2.3.0 e SciPy 1.14.1. Não houve instalação no aplicativo ou global.

Todos os 15 positivos novos foram preservados fora do treinamento. A inspeção
Whisper small local, sem prompt, com timestamps, é apenas auxiliar: omitiu o
nome em chamadas humanas legítimas e não comprova limites fonéticos nem
ausência da palavra. Também foi aplicada aos 57 contextos falsos do período
de treino para exclusão conservadora de tokens suspeitos. Não houve escuta
humana adicional. O rótulo do evento fundamenta exclusivamente as janelas
próximas à decisão, sem afirmar que cada segundo seja negativo. Nenhum pós é
usado para treinar. Esta supervisão temporal ainda pode conter ruído de rótulo.

O frontend processa o PCM inteiro em blocos de 160 amostras, mantendo seu
estado antes de selecionar janelas de 186 frames. As janelas terminam na
própria decisão ou 30/60 ms antes dela. Ganhos 0,85/1/1,15 são aplicados apenas
a cópias de treino, sem normalização ou reamostragem. A saída do frontend C é
a mesma utilizada na PoC anterior; a divergência numérica nativa já documentada
em [detector-parity.md](detector-parity.md) continua sujeita a verificação.

| Experimento | Inicialização | Épocas | Learning rate |
| --- | --- | ---: | ---: |
| A | Checkpoint original, otimizador novo | 8 | 0,0001 |
| B | Pesos aleatórios, mesma arquitetura | 20 | 0,001 |
| C | Checkpoint original, otimizador novo | 1 | 0,0001 |

Os 8.869 exemplos anteriores de treino e 1.857 de validação são preservados.
Cada época adiciona 1.024 sorteios de hard negatives, aproximadamente 10,35%
das janelas apresentadas. A amostragem escolhe sessão, evento e derivação,
entre fontes com capacidade restante. Cada evento recebe no máximo duas vezes
a expectativa uniforme arredondada para cima. Isso impede que a sessão com um
único evento ocupe um quarto dos novos negativos. Há somente quatro sessões
de treino: as augmentações não aumentam a independência das observações.

Checkpoint: menor `0,75 × BCE original + 0,25 × BCE hard negatives` em validação,
com reconhecimento de positivos originais em validação no máximo dois pontos
percentuais abaixo do checkpoint baseline. O protocolo v2 acrescentou o limite
por evento antes de qualquer fit, após revisão do inventário; somente os
resultados do baseline em validação já tinham sido observados. O protocolo v1
foi preservado. Não houve busca de thresholds ou mineração de teste.

Candidata: menor fração de falsos positivos reativados perto da decisão, entre
A/B que preservem as quatro reativações legítimas da validação; empate prefere
A. LiteRT/default é a referência principal da PoC; o replay nativo é uma
verificação adicional de compatibilidade. A seleção fica registrada antes do
teste e da regressão antiga. Se o critério falhar, a exportação permanece uma
candidata rejeitada para promoção.

Após A/B falharem na mesma chamada legítima da validação, foi congelado o
protocolo v3, antes de qualquer replay de teste/regressão. Ele autoriza somente
um follow-up C de uma época, com seed, learning rate, features, splits e
sampler iguais aos de A. Os arrays foram copiados com igualdade de SHA; os
manifestos anteriores continuam preservados. C também perdeu aquela chamada.
`selection-ab-v1.json` conserva a rejeição A/B; `selection-v1.json` sela C
como **artefato conservador de pesquisa, NO-GO**, às 12:28:09 UTC, antes de
`final-replay-launch.json`. Não houve novo treinamento ou troca de candidata
após consultar o teste. A designação C segue esse protocolo, não representa
aprovação por ter o melhor resultado no teste.

## Interpretação da avaliação

Cada contexto novo inicia com estado limpo e recebe somente seu PCM original,
sem cauda artificial. Reportam-se eventos em qualquer posição e eventos perto
da decisão original (−500/+200 ms). O contexto de dois segundos não recupera
o histórico completo do detector que coletou os eventos: o baseline precisa
ser reproduzido para que a supressão pareada tenha significado.

A coleta é condicionada a ativações do baseline. As frações de reativação
**não são falsos positivos por hora**. Os positivos são chamadas que já foram
reconhecidas; sua redetecção **não é recall populacional** e não permite medir
chamadas que o baseline perdeu. Sete positivos no teste e quatro na validação
limitam fortemente qualquer conclusão de preservação de sensibilidade.

O conjunto antigo usa a cauda de 0,5 s e a sequência contínua com prefixo
público de 30 s e três segundos entre tentativas, sem reset. Seus resultados
são regressão: os dados já foram consultados na seleção da arquitetura anterior.
A avaliação pública preserva os 35 min completos; nenhum erro observado foi
reutilizado como dado de treino.

## Evidência e reprodução privada

Os artefatos desta rodada ficam em `~/.cache/aegis-wakeword-hardneg/`, fora de
qualquer checkout Git, com diretórios 0700 e arquivos 0600. O inventário de cada
evento está em `audit-v1/inventory.json`; seu papel efetivo e exclusões aparecem
em `features-v1/features.json`. Áudios, transcrições, IDs individuais, hashes de
voz, features, pesos e scores por frame não entram no Git. O resumo público
contém somente métricas agregadas e hashes de proveniência do experimento.

A auditoria foi repetida em `audit-recheck-v2/`: resumo byte a byte idêntico e
inventários iguais após normalizar apenas a raiz de extração. Isso também
resolve a procedência de um endurecimento do bloqueio de NUL feito enquanto a
primeira auditoria estava em execução. `audit-recheck-launch.json` e
`audit-recheck-comparison.json` conservam os hashes efetivamente executados.
`metadata-temporal-verification.json` e `similarity-cue-sensitivity.json`
complementam a auditoria congelada.

Pontos de entrada reproduzíveis em `scripts/wakeword-poc/`:

1. `hardneg_audit.py --zip <ZIP-classificado> --output <nova-raiz>/audit-v1`.
2. `split_hardneg.py --audit <inventário> --output <nova-raiz>/protocol-v2.json`.
3. `inspect_hardneg.py --dataset <audit-v1/dataset> --model
   ~/.cache/aegis-wakeword-poc/tools/whisper-small --output <nova-raiz>/inspection-v1`,
   usando `~/.cache/aegis-wakeword-poc/tools/venv/bin/python`.
4. `train_hardneg.py prepare --audit <inventário> --inspection <inspection-v1/report.json>
   --protocol <protocolo> --output <nova-raiz>/features-v1`.
5. `train_hardneg.py train --features <features-v1> --protocol <protocolo>
   --strategy A --output <nova-raiz>/model-A-v1`; repetir B, com seu diretório.
6. `evaluate_hardneg.py --model <TFLite> --audit <inventário>
   --split-manifest <protocolo> --split validation --output <novo-diretório>
   --library ~/.cache/aegis-hands-free/activation-native-20261101/libaegis_microwakeword.so`,
   para baseline/A/B, com nomes `eval-{baseline,A,B}-validation-v2`.
7. `select_hardneg.py --protocol <protocolo> --baseline-evaluation <JSON-baseline>
   --a-evaluation <JSON-A> --b-evaluation <JSON-B> --a-training <model-A-v1/training.json>
   --b-training <model-B-v1/training.json> --output <nova-raiz>/selection-v1.json`.
8. Somente depois da seleção, repetir avaliação baseline/A/B com `--split test
   --legacy`, em `eval-{baseline,A,B}-final-v2`.
9. `summarize_hardneg.py --root <nova-raiz> --selected <estratégia-selada>
   --output <nova-raiz>/candidate-v1` confere a seleção e exporta a cópia privada.

Exceto ASR, usar o ambiente preservado
`~/.cache/aegis-wakeword-poc/comparison/microwakeword/venv/bin/python`.
Para reproduzir o follow-up: guardar a seleção A/B em `selection-ab-v1.json`;
`prepare_hardneg_followup.py --parent-protocol <protocol-v2.json> --features
<features-v1> --prior-selection <selection-ab-v1.json> --output <nova-raiz-C>`
produz `protocol-v3.json` e `features-v2`. Executar `train_hardneg.py train` com
esses artefatos, `--strategy A` (estratégia interna) e saída `model-C-v1`.
Reavaliar baseline/C somente na validação v3; `select_hardneg.py --protocol
<protocol-v3.json> --baseline-evaluation <baseline-validation-v3/evaluation.json>
--followup-evaluation <C-validation-v3/evaluation.json> --followup-training
<model-C-v1/training.json> --prior-selection <selection-ab-v1.json> --output
<selection-v1.json>` sela C, incluindo seu eventual NO-GO. Depois repetir a
etapa final baseline/A/B/C em `eval-{baseline,A,B,C}-final-v3`; o exportador usa
`--selected C`, com os relatórios/manifestos na mesma raiz privada e os nomes
desta rodada. O helper cria uma raiz nova; copiar os resultados sob novos
nomes é permitido, preservando todos os pais. A derivação implementada foi
conferida contra os bytes dos manifestos efetivamente usados.

Todos os destinos devem ser novos; nunca repetir esses comandos sobre a rodada
existente. Os manifestos conferem hashes do baseline, checkpoint, corpus,
revisão limpa, versões de dependências, splits e arrays. `training.json`
conserva histórico/seleção e hashes; `selection-v1.json` sela decisão por
validação antes do replay final. Os hashes dos relatórios estão no pacote
exportado. Mesmos dados, seeds e bibliotecas permitem repetir o procedimento;
não se promete identidade de bytes do export TensorFlow entre máquinas.

Verificações locais: dez testes instrumentais de arquivo/integridade/correlação,
`py_compile` dos novos scripts e `git diff --check`. Os fits carregam realmente
o checkpoint e a arquitetura; export verifica entrada INT8, saída UINT8 e
ausência de tensores float. O replay nativo usa a biblioteca de produção
preservada, sem modificar seu código. Testes de aplicativo/backend e builds de
APK não são pertinentes porque nenhum código desses componentes mudou.

## Resultados congelados

Referência principal: LiteRT/default, threshold 0,5, média de cinco scores,
liberação abaixo de 0,3, refratário de 16.000 amostras. Sem ajuste de regras.
Valores novos contam contextos com evento perto da decisão original.

| Modelo | FP validação /25 | Legítimos validação /4 | FP teste /64 | Legítimos teste /7 | Positivos antigos /23 | Eventos públicos /35 min |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline | 23 | 4 | 58 | 7 | 23 | 4 |
| A, fine-tuning 8 épocas | 1 | 3 | 9 | 7 | 23 | 1 |
| B, retreinamento 20 épocas | 0 | 3 | 8 | 7 | 22 | 1 |
| C, fine-tuning 1 época | 3 | 3 | 21 | 7 | 23 | 2 |

B ativou em nove contextos de teste ao contar qualquer posição, sendo oito
perto da decisão. Nos demais modelos, os dois critérios dão a mesma contagem.
Os quatro mantêm o confusor antigo: 1/8 negativos ativados. A sequência antiga
sem resets mantém as mesmas contagens de positivos/negativos da avaliação
individual para cada modelo. B perdeu o mesmo positivo antigo em ambos os
modos. Isso demonstra uma regressão adicional do retreinamento.

A reduziu a contagem de contextos falsos reativados em 84,48% (58→9); C reduziu
63,79% (58→21), no LiteRT. Esses percentuais usam o baseline reproduzido nos
mesmos 64 contextos reservados; seis falsos positivos originais não se
reproduziram com reset. Não são taxas de falso positivo por hora. O resumo
estruturado inclui a análise pareada de supressões/novas reativações e intervalo
exploratório por bootstrap de sessões, sem pressupor 64 ambientes independentes.

O núcleo nativo Linux carregou todos os modelos sem alterações. No teste, o
baseline teve 58 FP, A teve **10**, B teve **10 perto da decisão /11 no contexto**,
e C teve 21. Os sete legítimos do teste foram reconhecidos por todos. As
contagens públicas/antigas nativas constam no resumo estruturado. As diferenças
numéricas entre kernels/frontend continuam explícitas; não se declara paridade
bit a bit, nem equivalência física ARM64. O ganho de A é 82,76% (58→10) nesse
runtime. Uma conclusão sobre implantação precisa levar essa diferença em conta.

Também foi executado, **depois da seleção**, um diagnóstico dos quatro
positivos novos que pertencem aos períodos de treino. Nenhum deles foi usado
no fit, mas compartilham ambientes com os negativos treinados: não são teste
independente. O baseline reconheceu 4/4, A/B reconheceram 1/4 e C reconheceu 2/4.
Somando descritivamente todos os 15 contextos legítimos novos: baseline 15/15,
A/B 11/15, C 12/15, tanto no LiteRT quanto no host nativo. **Essas perdas são
o motivo para manter o baseline**, apesar do resultado favorável nos sete
positivos reservados e da grande redução de reativações falsas.

A comparação pareada do teste não encontrou novos contextos falsos ativados
fora dos 58 reproduzidos pelo baseline. A suprimiu 49, B suprimiu 50 e C
suprimiu 37. Bootstrap com 10.000 reamostragens das oito sessões com negativos,
seed 20261105: redução absoluta de fração de contextos para C de 57,81 pontos
percentuais, intervalo exploratório 47,62–69,23; para A, 76,56 pontos, intervalo
70,21–88,00. O agrupamento é heurístico e a amostra foi selecionada por ativação;
o intervalo não oferece garantia para conversas futuras ou taxa por hora.

Nos contextos de teste, a diferença máxima de score LiteRT/nativo foi
20 LSB no baseline, 21 em A, 11 em B e 20 em C. Houve diferença de eventos
em um contexto de A e dois de B; baseline e C preservaram os eventos dentro
de um stride. A tolerância numérica anterior de 1 LSB falha, inclusive no
baseline, e não foi ampliada. Não foi feito replay ARM64 nem captura física.

| Experimento | Checkpoint por BCE | Tempo treino/export, segundos | SHA-256 do TFLite |
| --- | ---: | ---: | --- |
| A | 8 | 98,09 | `11ea711b55107f6a56fd16bd54dd2d13bdea1bccc3c9628abb763cee28633130` |
| B | 17 | 201,33 | `439bf4eadfc70bb1a8075d1b3c75246e64a8954948d5621e22804e14a11155e6` |
| C | 1 | 22,82 | `cb050f025d40daa779269922d65a05868e6b4541022f945c89d7ead8eecb44dd` |

São tempos por processo, com A/B simultâneos; não incluem os 472,85 s da
inspeção ASR nem a preparação das features. Todos os modelos têm 39.552 bytes,
13.057 parâmetros, entrada `[1,3,40]` INT8 com escala `0.10196078568696976` e
zero point −128, saída `[1,1]` UINT8 com escala `1/256` e zero point 0, sem
tensores float. Os pesos originais e resultados anteriores permanecem intactos.

Pacote final privado: `~/.cache/aegis-wakeword-hardneg/candidate-v2/`, contendo
`aegis.int8.tflite` (C), `model.json` com **NO-GO**, `summary.json` e
`evidence-sha256.json`. O primeiro export foi preservado em `candidate-v1`;
a segunda cópia executa também a conferência do hash do arquivo copiado.
A/B continuam em seus diretórios experimentais. O importador Android permanece
congelado no SHA do baseline e não aceita essas candidatas. Não houve instalação,
APK, publicação, deploy, PR ou merge.

Para incluir o diagnóstico agregado no export reproduzido, executar também o
avaliador com `--split train`, sem `--legacy`, para baseline/A/B/C, nas saídas
`eval-{baseline,A,B,C}-train-v3`, somente após selar a seleção. Os positivos
desses períodos seguem avaliação apenas; os negativos são diagnóstico de
ajuste aos dados usados no treinamento. O exportador confere a cronologia em
`final-replay-launch.json` antes de produzir o pacote.

O próximo ensaio útil depende de positivos reais com limites da palavra
confirmados por escuta e representação suficiente das chamadas frágeis. Eles
podem permitir adaptar a discriminação sem simplesmente suprimir essas chamadas.
Uma nova coleta contínua reservada, com exposição conhecida e chamadas perdidas
anotadas, é necessária para avaliar comportamento prospectivo. Não há fundamento
para promover os pesos desta rodada apenas pelos sete acertos de teste.

Métricas agregadas: [wakeword-hardneg-results.json](wakeword-hardneg-results.json).
