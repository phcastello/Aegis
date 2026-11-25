# Aegis v0.8.0 — modelo privado e paridade do detector

O modelo permanece congelado. Não houve treino, troca de pesos ou ajuste de
threshold. A verificação usa o núcleo C++ que o JNI Android carrega, compilado
em Linux x86_64. **ARM64 e captura no aparelho ainda precisam de replay e
homologação física; um APK compilado não comprova essa etapa.**

## Artefato e contrato

Artefato canônico privado:
`~/.cache/aegis-wakeword-poc/comparison/microwakeword/model-realneg/aegis.int8.tflite`.
Tamanho **39.552 bytes**, SHA-256
`f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514`.
`training.json` tem SHA-256
`c1cb2115ba5bb6e925e0d566fc2c0ab575113ce1b6b7e670827cd193c999916a`.
O manifesto original de treino corresponde a
`b1b408ddaaaf40c9e74cad74097bd4590c0712fcd239642df23bbc8c0081c726`;
o snapshot FLEURS DEV corresponde a
`39e95adc759332e8a046f5ee3adaa6a0292019da6ef0ee7fbe30997f733a3978`.
Os bytes e os manifestos existentes foram conferidos; não foram recriados.
O frontend e o FFT nativos são as fontes da versão aceita
`pymicro-features 2.0.2`, commit
`96bd69cfad79aa67697e176570d3dd87052c3def`, arquivo de fonte SHA-256
`92ea30a9351418a94f084e2af491140607dd8443dfc91b540c7ba0f48d94340d`.
Somente o DSP C++ e seu FFT são compilados; não há Python no APK. O wrapper e
runtime Micro seguem a adaptação HA. Essa escolha conserva a procedência exata
da PoC; não constitui prova de identidade numérica entre compiladores.

O contrato público sem pesos está em
[`model.json`](../../scripts/hands-free/model.json). Pronúncia de treinamento:
`/aˈɛʒis/`. Entrada `[1,3,40]` INT8, escala `0.10196078568696976`, zero point
`-128`; saída `[1,1]` UINT8, escala `1/256`, zero point `0`. PCM16 mono 16 kHz,
janela 30 ms/passo 10 ms, escala de features `0.0390625`, stride de três frames,
quantização com arredondamento para par, sem normalização por gravação.

A detecção usa média dos últimos cinco scores, threshold **0,5**, liberação do
latch abaixo de **0,3** e intervalo mínimo de **16.000 samples** entre eventos.
Não há descarte inicial artificial. Blocos parciais ficam pendentes até
completar 160 samples; o padding de EOF pertence ao ensaio, não à captura real.
Reset recria variáveis do interpreter a partir dos mesmos bytes em RAM, além
do frontend, stride, janela e latch. Isso reproduz o contrato da PoC; não copia
reset parcial, aquecimento ou cooldown do HA.

## Execuções e diferenças observadas

O TFLite Micro fixado pelo HA carregou, alocou tensores e executou o grafo
Aegis. Não foi necessária alternativa de runtime. O wrapper do frontend, CMake e seu
conjunto pequeno de dependências foram adaptados com licenças e avisos
preservados em `assets/licenses/hands-free/` do APK.

O replay verifica hashes dos WAVs antes de abrir o corpus. Foram usados os
37 WAVs originais (incluindo seis referências separadas), a sequência aceita
com prefixo público de 30 segundos e 31 ensaios sem resets, e o corpus público
completo de 35 minutos. Cauda de 0,5 s nos ensaios individuais e três segundos
entre ensaios contínuos seguem o runner original. Não houve mineração de erros
nem nova seleção de modelo/configuração.

| Verificação | Runner aceito LiteRT/default | Núcleo nativo TFLM |
| --- | ---: | ---: |
| Positivos reservados | 23/23 | 23/23 |
| Negativos originais ativados | 1/8 | 1/8 |
| Ativações nos 35 min públicos | 4 | 4 |
| Positivos contínuos, sem reset entre gravações | 23/23 | 23/23 |

A tolerância numérica inicial foi **1 LSB da saída (`1/256`)**; tolerância de
evento foi um stride (**480 samples / 30 ms**), sem diferença no número de
eventos. **A comparação numérica com o runner aceito/default falhou:** a maior
diferença pontual foi **19 LSB (`0,07421875`)**. As diferenças são esparsas;
os arquivos ativados e as quatro ativações públicas foram conservados. O
maior deslocamento de evento foi 30 ms, e as quatro ativações públicas ocorreram
nos mesmos samples. Esse resultado não foi renomeado como paridade bit a bit
nem teve sua tolerância ampliada depois de observar os dados.

Para distinguir implementação de kernels, o mesmo runner foi executado em
uma comparação **diagnóstica separada** com `LiteRT BUILTIN_REF`, sem alterar
o modelo nem o runner aceito. Os **73 casos passaram** na tolerância de 1 LSB:
todos os scores privados/contínuos e seus eventos foram idênticos; no corpus
público a diferença máxima foi 1 LSB, em 12 observações de score, sem deslocar
nenhum evento. O contraste entre os modos evidencia sensibilidade aos kernels/delegação,
mas não isola sozinho a causa completa. A verificação direta abaixo também
encontra diferenças no frontend. A referência default continua sendo a
comparação principal, com a divergência explícita. Não extrapolar os resultados para ambientes não testados.

Uma comparação direta do frontend de produção com `pymicro-features 2.0.2`
verificou os 37 WAVs e os 35 minutos públicos. **A tolerância estrita zero
falhou:** 290 dos **8.861.800** valores de features diferiram, com máximo
**5,1953125** antes da
quantização. Consumo de samples, número de frames e formato foram iguais.
As diferenças persistiram com a fonte/FFT exatos da versão aceita e flags
conservadoras, portanto não atribuímos uma causa comprovada. Elas não alteraram
os arquivos/eventos observados e o replay com kernels de referência permaneceu
na tolerância de 1 LSB de saída. Não há alegação de frontend bit a bit igual nem
de equivalência garantida em todo áudio possível. O script `frontend-parity.py`
retorna falha enquanto essa divergência estrita existir; o agregado fica no
mesmo resumo público e os detalhes/valores continuam privados.

Reset após estado sujo versus instância nova produziu diferença zero e eventos
idênticos. Blocos de tamanhos 17/257/89/480/3/161 samples preservaram eventos e
scores da respectiva referência; a diferença com default permanece a descrita
acima. Duzentos resets/capturas curtas nativos não aumentaram o RSS observado:
**sem crescimento no ensaio** (valores exatos no resumo estruturado). Isso verifica o ensaio limitado; não substitui dias de
captura, monitoramento de filas ou medição da bateria do telefone.

O executável opcional de replay C++ foi compilado e conferido em um positivo
com a referência `BUILTIN_REF`: 71.200 samples com padding, diferença zero de
score e evento. O WAV contínuo preparado pelo mecanismo também passou:
3.381.760 samples com a cauda explícita, scores/eventos idênticos à referência
diagnóstica. Ele produz CSV de offsets/scores, sem áudio ou transcrição.
O [resumo estruturado](detector-parity-results.json) conserva os números,
tolerâncias, versões e hashes dos relatórios privados. Relatórios completos
permanecem em `~/.cache/aegis-hands-free/parity-final-default/`, `parity-final-reference/`
e `frontend-final/`.

## Reproduzir no host privado

Um host com compilador C++17, CMake e Ninja pode compilar sem modelo embutido:

```bash
cmake -S apps/aegis-node/src-tauri/gen/android/app/src/main/cpp \
  -B "$HOME/.cache/aegis-hands-free/replay-host" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release -DAEGIS_BUILD_REPLAY=ON \
  -DAEGIS_REPLAY_SOURCE="$PWD/scripts/hands-free/replay-native.cpp"
cmake --build "$HOME/.cache/aegis-hands-free/replay-host" \
  --target aegis_microwakeword aegis_mww_replay aegis_frontend_probe
```

Neste servidor faltava g++ completo. Foi usado o compilador RPM extraído em
cache privado, com headers/linker paths locais e sem instalar pacotes globais.
`-fno-use-linker-plugin` contorna a localização do plugin no compilador extraído;
isso não muda kernels nem quantização. O CMake versionado desabilita exceções
no TFLM para seus allocators de memória estática. Os arquivos de log de build
são privados e não constituem configuração exigida do aplicativo.

Usar o Python da PoC que já contém numpy, `pymicro-features` e LiteRT:

```bash
AEGIS_PYTHON="$HOME/.cache/aegis-wakeword-poc/comparison/microwakeword/venv/bin/python"
"$AEGIS_PYTHON" scripts/hands-free/replay-parity.py \
  --library "$HOME/.cache/aegis-hands-free/replay-host/libaegis_microwakeword.so" \
  --output "$HOME/.cache/aegis-hands-free/replay-default-new"
"$AEGIS_PYTHON" scripts/hands-free/replay-parity.py \
  --library "$HOME/.cache/aegis-hands-free/replay-host/libaegis_microwakeword.so" \
  --reference-kernels \
  --output "$HOME/.cache/aegis-hands-free/replay-reference-new"
```

Diretórios de saída devem ser novos e fora de todo checkout Git. O primeiro
comando deve continuar retornando falha se exceder a tolerância numérica;
inspecionar eventos e scores separadamente, sem esconder a divergência.
`--skip-public` serve apenas para iteração curta e não conclui o ensaio.

## Mecanismo ARM64 disponível, execução pendente

O mesmo CMake aceita o toolchain oficial NDK, ABI `arm64-v8a`, API mínima do
aplicativo, `-DANDROID_STL=c++_shared` e
`-DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON`. Usar a versão NDK declarada no
`app/build.gradle.kts`. Ativar `AEGIS_BUILD_REPLAY` e informar
`AEGIS_REPLAY_SOURCE` como acima; compilar o alvo `aegis_mww_replay`.
O build cotidiano do APK mantém essa opção desligada.

O script abaixo automatiza build NDK, verificação de ABI, diretório privado,
transferência, execução, coleta do CSV/metadados e limpeza dos seus próprios
arquivos. Ele exige serial explícito de dispositivo já autorizado; não foi
executado em ARM64 nesta entrega:

```bash
python3 scripts/hands-free/device-replay.py \
  --ndk "$ANDROID_NDK_HOME" --serial <serial-arm64-autorizado> \
  --model "$HOME/.local/share/aegis/private/hands-free-model-20261031/aegis.int8.tflite" \
  --wav <WAV-privado-do-corpus> \
  --output "$HOME/.cache/aegis-hands-free/device-replay-novo"
"$AEGIS_PYTHON" scripts/hands-free/compare-native-csv.py \
  --model "$HOME/.local/share/aegis/private/hands-free-model-20261031/aegis.int8.tflite" \
  --wav <mesmo-WAV-privado> \
  --csv "$HOME/.cache/aegis-hands-free/device-replay-novo/scores.csv" \
  --output "$HOME/.cache/aegis-hands-free/device-replay-novo/comparison.json"
```

A descrição manual a seguir permite inspecionar o mesmo mecanismo.

Em uma sessão técnica curta e separada dos dias de bateria, copiar executável,
`libaegis_microwakeword.so`, a `libc++_shared.so` correspondente, modelo e WAV
para um diretório exclusivo de modo 0700 sob `/data/local/tmp/`, usando ADB já
autorizado. Isso não instala outro app nem backend e não usa root. Não colocar
modelo ou áudios em armazenamento público, assets ou logs. Para cada WAV:

```text
LD_LIBRARY_PATH=<diretório privado> <diretório privado>/aegis_mww_replay \
  <modelo privado> <WAV privado> <CSV privado novo>
```

O comando valida formato mono PCM16/16 kHz, conserva estado durante o arquivo,
acrescenta a mesma cauda de 0,5 s e produz scores a cada 160 samples.
Trazer o CSV para a área privada do host e executar:

```bash
"$AEGIS_PYTHON" scripts/hands-free/compare-native-csv.py \
  --model <modelo-privado> --wav <mesmo-WAV> --csv <CSV-do-aparelho> \
  --output <arquivo-JSON-privado-novo>
```

Repetir separadamente com `--reference-kernels` para localizar deriva de
kernels, mantendo o resultado default. Para o teste contínuo, preparar o WAV privado pelo script
`prepare-continuous-wav.py --output <WAV-privado-novo>` usando o Python da PoC.
Ele verifica o inventário e usa os mesmos 30 segundos de prefixo público,
31 ensaios e três segundos entre ensaios do runner original, sem reset. O CLI
acrescenta explicitamente sua cauda final de 0,5 s; o comparador aplica a mesma
cauda. Não resetar entre gravações. Comparar também reset, parciais e ausência de
crescimento sob carga contínua. Registrar ABI, aparelho/Android, versão/build,
SHA da fonte e hash da biblioteca que produziu cada CSV; o comparador não
consegue deduzir essa procedência. Remover apenas o diretório temporário
criado para esse ensaio ao terminar. Não deixar profiling/ADB ligados durante
a coleta naturalística e não confundir esse replay com captura real.

## Importação privada no Android

O APK público não leva os pesos. A menor distribuição é importação explícita
do arquivo pelo seletor oficial, com SHA e contrato congelados no aplicativo.
A cópia pronta neste servidor está em
`~/.local/share/aegis/private/hands-free-model-20261031/aegis.int8.tflite`, modo
0600, em pasta 0700; `model.json` ao lado descreve a proveniência. O proprietário
pode transferir esse arquivo ao telefone pelo acesso privado autenticado já
usado no servidor e selecioná-lo na configuração hands-free. Não foi criado
endpoint público nem serviço permanente para distribuir o modelo.

Para preparar outra cópia privada, sem treinamento:

```bash
python3 scripts/hands-free/export-private-model.py \
  --output "$HOME/.local/share/aegis/private/hands-free-model-nova-copia"
```

O script rejeita modelo diferente, proveniência divergente, DEV manifest
alterado, saída existente e qualquer saída dentro de Git. Ele copia apenas os
mesmos bytes e um manifesto; não publica nada. O Node importa o `.tflite`
original, não uma configuração livre do detector. Modelo ausente, corrompido ou
incompatível deve deixar o hands-free indisponível com diagnóstico claro.
