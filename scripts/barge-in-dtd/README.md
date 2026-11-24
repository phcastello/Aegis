# Detector de fala sobreposta do modo bidirecional

O modelo AGD4 é um candidato experimental para detectar fala independente
durante a reprodução da Aegis. O cancelamento de eco continua sendo uma etapa
própria do pipeline. O classificador não altera o PCM, não depende de um
locutor cadastrado e não recebe áudio remoto. Os pesos são compartilháveis
entre conversas, mas os estados acústico e recorrente são privados de cada
instância e descartados ao reiniciar a sessão.

## Proveniência

- Microsoft AEC Challenge, conjunto público ICASSP 2022, gravações
  farend-singletalk. Commit 8f861f0f8ffca26a3d1d16f17f2594ea89abe7ac.
  O manifesto sources.json lista exclusivamente os 144 IDs usados no treino
  e os 32 IDs usados na validação do modelo, com os SHA-256 dos WAVs originais.
  Os conjuntos independentes de bancada não são usados no treino ou na seleção
  de parâmetros.
- Referências naturais de voz são emprestadas apenas de outro ID do mesmo
  conjunto. A fala portuguesa sintética usa frases e vozes separadas para o
  treino e a validação, geradas por eSpeak NG 1.51.1. Não há áudio privado.
- Os cenários incluem eco real estacionário e com movimento, alterações de
  ganho, saturação, referência digital ativa com microfone sem eco, silêncio,
  ruído, fala curta e fala simultânea. Os casos adicionais sempre começam no
  início das gravações. Os primeiros 1,2 s dos recortes arbitrários ficam fora
  da função de perda, pois a referência anterior pode não estar disponível.
  A validação continua incluindo esses recortes como teste de robustez.
- O AEC3 nativo da Aegis é executado em bancada pelo protocolo BatchEcho.kt.
  A saída linear recebe mais 64 amostras de atraso para coincidir com a
  saída com supressão residual à latência de 8 ms. O classificador também
  aprende com simulações de captura já processada pelo Android.
- O enriquecimento usa apenas as gravações públicas e a voz conhecida
  injetada. As máscaras espectrais auxiliares de treino são a razão entre a
  magnitude da fala independente e a soma das magnitudes da fala e do eco,
  com estabilizador 32. Essas máscaras e as amostras de áudio não são
  distribuídas no aplicativo.

Referência do conjunto de dados: Cutler et al., *ICASSP 2022 Acoustic Echo
Cancellation Challenge*, ICASSP 2022.
[Microsoft AEC Challenge](https://github.com/microsoft/AEC-Challenge).
A licença MIT do repositório de origem está em
apps/aegis-node/src-tauri/gen/android/app/src/main/assets/licenses/AEC-Challenge.txt.

## Candidato congelado

O binário do Android está em app/src/main/assets/hands-free-barge-in/doubletalk.bin
(na árvore Android gerada). O arquivo doubletalk-model.json ao lado descreve
a arquitetura, limiares, janela de confirmação, métricas de validação e hash.

- 403.072 bytes, SHA-256
  548fbc2679660dceed0aa47e5f737e5797ac7165e7d8f7fdb494969e5f77c0b7.
- Época 13 de 22. Treino em 633.163 blocos, 2.016 casos.
  Validação em 151.756 blocos, 480 casos.
- 16 kHz mono, blocos de 320 amostras, FFT de 512 pontos com janela Hann de
  320 amostras. O encoder causal compartilhado reúne três quadros de 192
  amplitudes espectrais e RMS. O modelo compara os embeddings com os 61
  quadros de referência, incluindo histórico de 1,2 s, e mantém um GRU de
  64 estados. Só o classificador é distribuído. A cabeça de máscara é usada
  no treino.
- A confiança exportada é sigmoid(logit/3), um escore monotônico. Após dois
  quadros iniciais, a política validada exige 6 evidências em uma janela de
  12 quadros e evidência no quadro atual. Limiar software 0,76368054, limiar
  captura já processada 0,76940306.

Resultados de validação do modelo, considerando a primeira confirmação
(com margem final de 120 ms após a fala injetada):

| Modo | Acertos | Controles de eco/ruído com falso corte |
| --- | ---: | ---: |
| Software, fala sobre eco em recortes | 20/128 | 0/96 |
| Software, fala sobre eco no início de gravações | 18/64 | 0/32 |
| Software, eco atenuado/mute | 24/32 | — |
| Captura já processada, simulação de hardware limpo | 40/64 | 0/64 |
| Captura já processada, cenário de AEC ruim | 6/128 | 0/96 |
| Captura já processada, eco atenuado/mute | 25/32 | — |

Também não houve confirmações fora da janela de fala antes de uma confirmação
válida nesses casos de validação. Os resultados ainda mostram sensibilidade
baixa em sobreposição real de voz. A validação física e a bancada independente
permanecem separadas do desenvolvimento do modelo.

A regressão Kotlin reproduz as confianças PyTorch/CPU em 241 blocos sintéticos
de cada modo, passando por mais de três voltas do histórico de referência
e reinicialização de estado. Os vetores e pesos congelados não contêm áudio
de usuários.

## Reprodução

A preparação precisa de Python, NumPy 2.2.6, SciPy 1.15.3 e eSpeak NG 1.51.1.
O treino desta iteração foi executado com Python/PyTorch 2.11.0 CUDA 13 e
NumPy 1.26.4 em GPU RTX A2000, sem serviço faturável. Os arquivos de treino
são gravados exclusivamente no diretório de trabalho explicitamente escolhido.

1. Compile a biblioteca JNI Linux do AEC3 correspondente ao código Android
   e o wrapper Kotlin NativeHandsFreeEcho.kt junto de BatchEcho.kt.
   Crie um JSON contendo a lista de argumentos do executável Java
   (por exemplo java -Djava.library.path=<lib> -cp <classes:stdlib>
   com.aegis.node.BatchEchoKt). O runner recebe o número de amostras como
   inteiro big-endian, seguido dos dois fluxos PCM16 little-endian. Retorna o
   mesmo número de amostras, a saída do supressor e a saída linear. Um zero
   encerra o processo. Cada caso abre uma instância AEC nova.
2. Defina AEGIS_DTD_WORKDIR apontando para um diretório de pesquisa
   descartável e AEGIS_DTD_AEC_BATCH_ARGV para o JSON do runner.
   Execute python prepare.py. Os downloads são verificados pelos hashes
   do manifesto. A preparação usa quatro processos por padrão e pode ser
   limitada por AEGIS_DTD_WORKERS.
3. Em ambiente PyTorch, mantenha AEGIS_DTD_WORKDIR apontando para o
   diretório com split.json, tensors e masks, e execute python train.py.
   Esse treinamento usa janela causal com prehistórico, perda por quadro,
   perda para confirmação de 4/7 quadros, máscaras auxiliares e mineração
   de negativos do próprio treino. A escolha final de 6/12 e dos limiares
   foi feita somente na validação do modelo.
4. Execute python export.py. Os arquivos neural-candidate.bin,
   neural-candidate.json e parity-scores.json registram o candidato
   gerado. A reprodução é controlada por sementes fixas, mas kernels
   diferentes no GPU podem gerar pesos distintos. O hash congelado
   identifica precisamente os pesos usados na aplicação.
5. Valide o candidato em Kotlin/Android, incluindo a paridade numérica
   dos novos vetores, antes de substituir o binário e a regressão do
   aplicativo. Reserve o conjunto independente para avaliação final.

Os scripts não publicam, promovem ou instalam uma atualização no aparelho.
