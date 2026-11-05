# Aegis v0.8.0 — promoção do Strong época 2

O proprietário autorizou explicitamente o uso cotidiano do microWakeWord Strong
época 2 no Android, preservando rollback ao baseline. Esta rodada integra e
distribui essa decisão; não refaz treinamento, seleção, avaliação acústica ou
busca de threshold. Os resultados anteriores continuam preservados na branch
`feat/v0.8.0-wakeword-tradeoff`, commit `d60baf6`, em
[relatório](https://github.com/phcastello/Aegis/blob/d60baf6/docs/v0.8.0/wakeword-tradeoff.md)
e [resultados](https://github.com/phcastello/Aegis/blob/d60baf6/docs/v0.8.0/wakeword-tradeoff-results.json).
A nova autorização substitui a recomendação operacional daquela rodada, sem
reescrever seus resultados históricos.

Branch isolada `feat/v0.8.0-wakeword-strong-production`, base `cd35c3f` de
`feat/v0.8.0-hands-free-polish`. Worktree
`/home/phc/Aegis-wakeword-strong-production`. As outras branches e alterações
locais foram preservadas. O polish de continuidade de dez segundos, painel,
sincronização e coleta de ativações permanece na base.

## Modelos autorizados

| Nome nas configurações | modelVersion | SHA-256 dos pesos |
| --- | --- | --- |
| Baseline | `aegis-mww-fleurs-dev-2026-10-31` | `f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514` |
| Strong — época 2 | `aegis-mww-strong-epoch-02-2026-11-05` | `1e7c686420f65d8da892f02c71d945f03a8dd528a1005de8c9c8482dce52ad2e` |

São exatamente dois hashes aceitos, ambos de 39.552 bytes, MixConv com entrada
INT8 `[1,3,40]`, saída UINT8 `[1,1]` e estado streaming. Threshold 0,5, média de
cinco scores, liberação abaixo de 0,3 e refratário de 16.000 amostras permanecem.
Nenhum peso, frontend acústico, VAD, STT ou TTS foi modificado. Os pesos não
entram no Git nem no APK.

## Integração e preservação

O catálogo em `NodeHandsFreeModel.kt` deriva nome/versão do hash calculado nos
bytes reais. Modelo de tamanho/hash inválido continua sendo rejeitado. O núcleo
nativo verifica a compatibilidade antes da substituição. A gravação temporária
fica no mesmo diretório privado, é sincronizada e só então renomeada sobre o
modelo anterior. Falhas anteriores à troca preservam o arquivo instalado.

O runtime coordena a substituição com a parada da escuta e a identidade do
detector carregado. As novas ativações guardam `modelSha256` e `modelVersion`
correspondentes aos pesos usados na decisão. As métricas mantêm os registros
`identities`, `identityBoundaries` e `days.*.byIdentity`: mudar de modelo cria
uma nova atribuição, sem converter totais antigos em dados do Strong.
WAVs, rótulos, JSONL e metadados de ativações históricas não são reescritos.

As configurações mostram somente o nome do modelo instalado. O seletor privado
existente faz a importação; não foi criada outra interface de gerenciamento.
Em estado desatualizado, a interface identifica o último modelo informado.
Pareamento, credenciais, preferências, histórico de conversas, coleta e métricas
mantêm seus mecanismos e locais existentes. Não há migration, limpeza de dados,
recriação do Node ou desinstalação.

## Arquivos privados e importação

Arquivos preparados no servidor, em diretório 0700 e arquivos 0600:

`/home/phc/.cache/aegis-wakeword-strong-production/transfer/`

- `aegis-strong-epoch02.int8.tflite`: novo modelo para importar.
- `aegis-baseline-rollback.int8.tflite`: baseline intacto para retorno.
- `manifest.json`, `SHA256SUMS.txt`, `IMPORTAR-E-ROLLBACK.txt`.

Ambos foram copiados dos snapshots privados congelados, conferindo os hashes
da tabela. Antes de transferir, executar `sha256sum -c SHA256SUMS.txt` nesse
diretório. Transferir somente pelo acesso privado habitual ao servidor, por
exemplo SCP/SFTP para o computador e cabo/armazenamento privado para o telefone.
Não publicar esses arquivos como assets da release nem anexá-los a um PR.

No telefone:

1. Atualizar o Node pela distribuição oficial desta rodada, por cima da
   instalação existente, preservando pacote, certificado e dados.
2. Desmarcar **Escutar “Aegis”** nas configurações hands-free.
3. Importar `aegis-strong-epoch02.int8.tflite` pelo seletor existente.
4. Confirmar **Modelo instalado: Strong — época 2**.
5. Reabilitar **Escutar “Aegis”** e confirmar a captura/ativação.
6. Com a coleta local já autorizada habilitada, fazer uma chamada nova e exportar
   **métricas e ativações (ZIP)**. Conferir no evento novo o hash Strong e
   `modelVersion=aegis-mww-strong-epoch-02-2026-11-05`. Os eventos anteriores
   devem continuar com a identidade original. Em `metrics.json`, a identidade
   corrente e seu período `byIdentity` devem corresponder ao Strong.

A atualização do APK aceita o Strong, mas **não troca os pesos privados já
instalados**. Só se confirma o Strong ativo no telefone depois dessa verificação
de uma ativação nova. ADB foi localizado em
`/tmp/aegis-hands-free-sdk/platform-tools/adb`; `adb devices -l` não encontrou
aparelho conectado nesta preparação. Não se declara importação ou ativação física.

## Rollback imediato dos pesos

Desabilitar a escuta, importar `aegis-baseline-rollback.int8.tflite`, confirmar
**Baseline** e reabilitar a escuta. Uma ativação nova deve registrar SHA
`f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514` e versão
`aegis-mww-fleurs-dev-2026-10-31`. Não exige downgrade de APK, reinstalação,
limpeza de dados ou mudança de pareamento. O aplicativo atualizado continua
aceitando ambos os modelos.

## Validação e distribuição

| Verificação local | Resultado |
| --- | --- |
| Kotlin/JVM hands-free | 146 testes, zero falhas/erros/skips |
| Importação/identidade dos modelos | Oito testes com os dois TFLites privados reais, sem pesos no código; inclui ida/volta, integridade, tamanho, falha de runtime e preservação do arquivo anterior |
| Métricas e coleta | 19 testes de métricas e 13 da coleção; novas identidades e metadados, bytes dos registros históricos preservados |
| Interface Node | 177 testes; typecheck e build Vite aprovados |
| Configurações em Chromium | Baseline, Strong, rollback e estado desatualizado aprovados |
| Contratos hands-free/IPC/R8/painel | 15 testes aprovados |
| Carga real Strong no núcleo original Linux | Create/reset/destroy aprovados, sem processar áudio; contrato INT8 conferido |

O teste de transação JVM injeta somente o verificador de runtime para não
depender de JNI Android no host; os bytes/hashs usados são os modelos privados
reais, e a carga nativa é verificada separadamente. Os casos privados não foram
ignorados na execução local. Isso não afirma importação ou retomada física do
AudioRecord no telefone. A preparação local precisou copiar somente scaffolds
Tauri gerados/ignorados da worktree anterior; logs de bootstrap foram preservados
e a compilação/testes finais passaram. Não houve alteração de fontes por essas
falhas de ambiente.

O plano preliminar do publicador oficial confirmou somente `native`, sem
migrations ou publicação de backend/PWA. A versão será reservada pelo ledger
no lançamento do fluxo oficial; não se escolhe ou reutiliza manualmente um N.
O fluxo existente publica os instaladores nativos assinados e o feed, preservando
o isolamento das chaves oficiais. Não há release estável final da v0.8.0.

O resultado final, versão, SHA de fonte, execução oficial, hash/certificado do
APK e verificações serão acrescentados após a publicação. Não se confundem
solicitação aceita, build concluído e distribuição efetivamente disponível.
