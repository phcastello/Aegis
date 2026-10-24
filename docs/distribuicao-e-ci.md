# Distribuição, atualizações e estágios de CI

A Aegis tem um único fluxo recomendado para Nodes compatíveis. Previews,
estáveis e rollbacks descrevem eventos de distribuição; não são canais
selecionáveis. Publicar torna a distribuição disponível. Cada usuário autoriza
a instalação no seu Node com interface.

## Estado verificado e transição

O PR #20 foi integrado à `main` em `1170881`. A primeira preview,
`0.7.2-unstable.1`, código Android `7002001`, foi produzida pelo
[run 40038742314](https://github.com/phcastello/Aegis/actions/runs/40038742314),
fonte `ee95f43`. Ela usa o contrato anterior e só existe como artefato do Actions.
As releases nativas públicas inspecionadas em 23/10/2026 são `node-v0.7.0` e
`node-v0.7.1`. O feed legado recomenda `0.7.1`, código `7001999`.

A implementação v2 deste PR ainda precisa de bootstrap e distribuição real
para ser considerada operacional. Esta tarefa não publica instaladores,
tags, branch de estado, feeds, migrations ou deploys.

| Instalação | Migração |
| --- | --- |
| v0.7.1 Android e Windows | Continua no feed histórico. Pode receber uma futura **estável** com o updater v2 pelo aplicativo. Para entrar antes nas previews v2, exige uma única instalação manual assinada, por cima da existente. |
| `0.7.2-unstable.1` do primeiro run | Ainda usa o updater anterior e exige a mesma instalação única para receber previews v2. |
| Primeira distribuição contendo updater v2 | Consulta o recomendado; próximas previews, estáveis e recuperações compatíveis voltam ao fluxo interno. |
| Previews históricas `stage.N`/`unstable.N` anteriores | O parsing de origem é preservado. A migração depende de uma estável com versão e código superiores, ou da instalação manual única assinada compatível. |

Nunca colocar previews ou rollbacks no feed histórico
`https://github.com/phcastello/Aegis/releases/download/node-preview/latest.json`.
Clientes recentes o filtram como estável e clientes mais antigos podem não filtrar.
Ele permanece como ponte de compatibilidade, sem seleção na interface.

## Contrato recomendado

Android e Windows consultam o mesmo endereço fixo:

`https://raw.githubusercontent.com/phcastello/Aegis/aegis-distribution/latest.json`

Os instaladores permanentes ficam em releases `node-v<version>` e têm URLs
exatas e versionadas. Artefatos do Actions são somente intermediários para
homologação e promoção; sua retenção não define a disponibilidade do updater.
Não usar links de ZIPs do Actions no manifesto.

O manifesto mantém os campos do updater Tauri (`version`, `notes`, `pub_date`,
`platforms.windows-x86_64`) e a extensão `android.aarch64`. O contrato v2 exige:

- `schema: 2`, `version`, `versionCode`, `kind: normal|rollback`;
- `sourceCommit`, `sourceVersion`, `publishedAt` igual a `pub_date`;
- `compatibility: { api: 1, nodeData: 1 }`, contratos de protocolo e persistência;
- `rollbackOf` e `restoresVersion` somente para recuperação;
- URL fixa de cada plataforma, SHA-256 dos instaladores e assinatura Windows;
- `android.aarch64.versionCode` igual ao código interno da distribuição.

`sourceVersion` é a versão original do checkout; `restoresVersion` identifica
a distribuição funcional selecionada. Podem divergir: uma preview pode ter sido
produzida de um checkout cujo package.json ainda dizia `0.7.2`. O SHA efetivo é
registrado no plano, manifesto e runtime compilado, sem depender das notas livres.
`publishedAt` é a data fixa atribuída à distribuição no plano de build/assinatura,
não o instante de avanço do feed; permanece idêntica em promoções e retries.

Os clientes exigem SemVer superior, código interno superior ao instalado,
metadados consistentes, compatibilidade reconhecida e URLs exatas do repositório.
Android verifica também hash dos bytes, pacote, versão, código e certificado
oficial antes de abrir o instalador. Windows preserva a verificação criptográfica
do plugin Tauri com a chave existente e verifica o SHA-256 dos mesmos bytes antes
de liberá-los para instalação. Nenhum caminho apaga dados, identidade,
NodeId, pareamento, cofre ou preferências.

## Versionamento e recuperação

A versão SemVer identifica a distribuição. O código interno é uma reserva
separada e monotônica. As versões novas aceitas são `X.Y.Z`,
`X.Y.Z-unstable.N`, `X.Y.Z-unstable.N.rollback.M` e `X.Y.Z-rollback.M`.
Eventos positivos são limitados a 65535; não se aceita build metadata para
precedência nem rollback aninhado.

Uma preview defeituosa `0.7.2-unstable.5` é recuperada por
`0.7.2-unstable.5.rollback.1`. Recuperar essa recuperação gera
`0.7.2-unstable.5.rollback.2`, com `rollbackOf` apontando para o evento anterior.
A evolução normal continua em `0.7.2-unstable.6`.

Uma estável defeituosa `0.7.2` é recuperada por `0.7.3-rollback.1`, seguida,
se necessário, de `0.7.3-rollback.2`. É uma recuperação explícita do evento
anterior, não uma correção funcional `0.7.3`. A próxima preview normal continua
em `0.7.3-unstable.1`. Todas essas ordens são verificadas em JS e Rust/SemVer.
No limite nativo de patch 999, uma recuperação de estável exige procedimento
revisto para outra linha; o publicador bloqueia a geração automática.

O NSIS do Tauri conserva a versão SemVer de exibição/comparação e usa
`major.minor.patch.0` no campo numérico `VIProductVersion`; esse campo não
substitui a comparação SemVer do updater. O comportamento consta do
[código da versão de CLI utilizada](https://github.com/tauri-apps/tauri/blob/tauri-cli-v2.12.1/crates/tauri-bundler/src/bundle/windows/nsis/mod.rs).
A instalação real de recuperação em Windows ainda exige homologação.

Exemplo projetado a partir das reservas reais conhecidas, sem executar reservas:

| Evento | Distribuição | Código interno |
| --- | --- | --- |
| Estável já publicada | `0.7.1` | `7001999` |
| Preview já gerada | `0.7.2-unstable.1` | `7002001` |
| Primeira preview v2 | `0.7.2-unstable.2` | `7002002` |
| Preview v2 defeituosa | `0.7.2-unstable.3` | `7002003` |
| Recuperação da .3 para o código da .2 | `0.7.2-unstable.3.rollback.1` | `7002004` |
| Próxima evolução normal | `0.7.2-unstable.4` | `7002005` |
| Estável | `0.7.2` | `7002999` |
| Recuperação da estável | `0.7.3-rollback.1` | `7003001` |
| Próxima preview normal | `0.7.3-unstable.1` | `7003002` |

Somente as duas primeiras linhas são histórico efetivo. Falhas ou outras
reservas alteram os números futuros. Não editar códigos de APKs históricos.

## Reserva e publicação resistentes a falhas

A branch de estado `aegis-distribution` contém `ledger.json` e, após primeira
publicação, `latest.json`. Cada reserva é adicionada ao ledger com versão,
código, SHA, run, controle confiável e procedência. Não há serviço ou banco novo.
O piso inclui releases e runs admitidos anteriores, inclusive o primeiro run.

A reserva é gravada **antes** da compilação. Uma falha ou cancelamento não
devolve a versão nem o código. Nova tentativa de build usa nova distribuição;
não reexecutar assinatura com o mesmo nome para produzir bytes diferentes.
A promoção pode ser repetida com os mesmos artefatos assinados.

O contador avança a partir do maior código conhecido, preservando o slot
histórico `999` das estáveis. Prereleases não o consomem; se esgotarem os códigos
inferiores, o plano bloqueia e exige outra linha de versão. O limite Android é
`2100000000`. A alocação de uma estável exige seu slot histórico superior a
todas as reservas, preservando a migração dos clientes antigos.

Os workflows compartilham concorrência e a gravação usa commits com pai fixado
e avanço de ref com `force:false`. Duas reservas concorrentes não sobrescrevem
uma à outra; o perdedor relê o ledger e valida novamente. O mecanismo segue o
[contrato GitHub de atualização fast-forward](https://docs.github.com/en/rest/git/refs#update-a-reference).
Preservar a branch e seu histórico; não apagar ou retroceder o ledger para
reaproveitar códigos. Restringir alterações administrativas dessa branch ao
publicador e ao proprietário, sem reescrever seu histórico.

A promoção cria ou completa um draft versionado, recusa bytes diferentes em
assets existentes e baixa os bytes publicados para compará-los com os arquivos
verificados. Só depois torna a distribuição pública e avança o recomendado.
Falha parcial deixa o draft retomável e o último recomendado intacto. Se a
release já estiver pública, a repetição verifica os mesmos bytes e retoma
somente o que falta. Código novo jamais é uma simples renomeação de APK antigo.

Estáveis também atualizam o feed legado, após verificar a distribuição completa.
Esse asset histórico ainda usa a operação antiga `--clobber`, que não é atômica
no GitHub: falha após exclusão pode deixá-lo temporariamente ausente. Repetir a
promoção dos mesmos bytes recupera esse feed. O ponteiro recomendado v2 não
sofre essa janela e permanece válido. Previews e recuperações não tocam o legado.

## Gerar e autorizar uma distribuição

Após bootstrap dos controles revisados na `main`, use sempre `--ref main`.
O código candidato é fixado pelo SHA completo de uma branch deste repositório.
Push, PR draft e build não recomendam uma atualização.

```bash
gh workflow run aegis-native-preview.yml --repo phcastello/Aegis --ref main \
  -f sha=<SHA-completo-revisado> -f branch=fix/v0.7.2-ux-proactivity \
  -f version=0.7.2-unstable.2 -f kind=normal -f platform=both
```

O plano reserva versão/código. O build usa somente chave Android descartável,
sem credenciais oficiais. A assinatura ocorre em runners separados, controlados
pela `main`, com aprovação de `aegis-native-signing`; não executa o candidato.
Valida certificado, pacote, ARM64, distribuição release, Firebase, rede,
fixtures, identidade Windows e assinatura vinculada à versão. A assinatura
Windows do updater não é Authenticode e não elimina SmartScreen.

As opções `android`/`windows` continuam úteis para homologação focada; uma
recomendação exige `both`, para que o mesmo manifesto tenha todos os artefatos
suportados. Não há assinatura nem build privilegiado iniciado por clientes.

Depois de uma execução v2 bem-sucedida, revisão e autorização explícita:

```bash
gh workflow run aegis-native-promote.yml --repo phcastello/Aegis --ref main \
  -f run_id=<run-v2-bem-sucedido-com-ambas-plataformas> -f publish=true
```

Aprove novamente o environment após revisar versão/SHA/artefatos. A promoção
reconfere reserva, origem do workflow e sucesso, baixa os finais, verifica
hashes e assinaturas, publica a release imutável e só então recomenda.
`publish=false` não publica. O run antigo `40038742314` não tem reserva v2 e
não pode ser promovido por esse caminho. Artefatos expirados exigem uma nova
reserva/build; não reconstruir outros bytes sob uma versão já reservada.

Uma estável usa o mesmo caminho com `version=0.7.2`; nesse caso o workflow
exige a matriz completa de release antes do build. Previews e recuperações
não exigem automaticamente as janelas longas de consumo. O workflow antigo
`Aegis Node Release` permanece manual para tags históricas, somente com controle
da `main` e fonte ancestral da `main` revisada; não aceita código arbitrário de
branches como atalho para assinatura. Distribuições v2
são direcionadas ao caminho unificado. Criar tag não publica automaticamente.

## Gerar um rollback

Selecione duas distribuições v2 públicas: a defeituosa e a funcional. O plano
consulta seus manifests versionados, exige compatibilidade e SHA fonte exato,
valida a sequência de recuperação e reconstrói o código escolhido com nova
versão, novo código e novas assinaturas. Não se instala diretamente um histórico.

```bash
gh workflow run aegis-native-preview.yml --repo phcastello/Aegis --ref main \
  -f sha=<sourceCommit-da-distribuicao-funcional> \
  -f branch=fix/v0.7.2-ux-proactivity \
  -f version=0.7.2-unstable.5.rollback.1 -f kind=rollback \
  -f rollback_of=0.7.2-unstable.5 -f restores_version=0.7.2-unstable.4 \
  -f compatibility_confirmed=true -f platform=both
```

Esses nomes são exemplo, não distribuições existentes. Depois do build e da
aprovação de assinatura, promover seu run pelo mesmo `aegis-native-promote.yml`.
O SHA restaurado deve ser alcançável pela branch indicada e coincidir com a
procedência da distribuição funcional. Rollback de outro rollback incrementa
o evento e aponta `rollback_of` para a distribuição defeituosa publicada.
Se um evento for reservado e o build falhar, pule seu número: `.rollback.2` pode
recuperar a mesma distribuição original quando `.rollback.1` nunca foi publicada.
Nunca reutilize a reserva nem invente uma versão normal para contornar a falha. Fontes anteriores ao contrato v2 exigem preparação/revisão específica;
não contornamos as verificações para assiná-las automaticamente.

`compatibility_confirmed=true` é uma declaração explícita do operador, não
uma prova automática de compatibilidade de banco. Conferir API atual, protocolo,
configuração e persistência antes de selecioná-lo. Divergência dos contratos
bloqueia. Quando não houver garantia, preparar procedimento específico e manter
o rollback bloqueado. Nunca executar downgrade destrutivo de esquema.

## Instalar e recuperar o próprio Node

Em **Configurações → Atualizações**, a consulta automática discreta e a consulta
manual usam o mesmo fluxo. Uma preview publicada aparece normalmente. Uma
recuperação aparece como **Atualização de recuperação disponível**, identifica
o evento defeituoso e a distribuição restaurada e usa o mesmo botão de instalar.
Download, progresso e erros são preservados; não há instalação silenciosa.

**Recuperar este Node** consulta o recomendado e oferece a recuperação já
publicada. Para uma versão histórica, explica a reconstrução e aprovação no
GitHub e oferece o acesso ao workflow. Não despacha assinatura, não promete
recuperação imediata e não tenta instalar código Android inferior.

Se o aplicativo não abrir, usar outro dispositivo/navegador para acessar a
release de recuperação publicada, conferir `latest.json`/`SHA256SUMS` e
identidade, baixar somente o instalador da plataforma e instalar por cima.
Android: mesmo pacote/certificado, código superior, sem desinstalar ou `pm clear`.
Windows: encerrar processo/tray, manter usuário/escopo/diretório/perfil e cofre.
Se não existir recuperação instalável, o proprietário publica um novo evento
pelo procedimento acima. Não reutilizar instalador antigo nem apagar o perfil.
Não há watchdog ou supervisor novo.

## Bootstrap necessário

O bootstrap #20 já funciona para previews v1. Para ativar v2, os novos controles
confiáveis precisam estar na `main`: workflows preview/promote, scripts de
reserva/publicação/assinatura e suas dependências de versionamento/manifests.
Isso pode ocorrer pela integração autorizada do PR #19 ou por backport de
infraestrutura revisto separadamente. Esta tarefa não abre outro PR nem altera
`main`. Um workflow de branch não recebe chaves oficiais como atalho.

Preservar `aegis-native-signing`, seu reviewer, allowlist de `main`/`node-v*` e
chaves apenas no environment. A configuração registrada no bootstrap de
23/10/2026 tem reviewer `phcastello`, bypass administrativo desabilitado e as
cinco credenciais oficiais removidas do escopo geral. Preservar a variável
pública `AEGIS_ANDROID_FIREBASE_CLIENT_B64` do cliente `com.aegis.node`, jamais
usar service account no build.

O token de `plan` pode gravar somente a reserva pelo controle confiável;
o candidato recebe permissões de leitura e nenhum segredo oficial. A promoção
recebe escrita após aprovação. A branch de estado será criada apenas na primeira
reserva autorizada; nenhum passo desta implementação a cria remotamente.

## Estágios e seleção

A seleção permanece em [select-node-ci.mjs](../.github/scripts/select-node-ci.mjs). Ela registra separadamente `affected` (backend, UI, Windows, Android, distribuição, geolocalização, notificações, áudio, energia, identidade e dependências) e `stage`. Falha de histórico seleciona os subsistemas conservadoramente, mas respeita a profundidade do estágio.

| Estágio | Gatilho e verificações |
| --- | --- |
| Desenvolvimento | Push em `fix/*`, `feat/*` ou outra branch não dispara workflow. Eventos de PR draft executam apenas testes dos scripts de CI e checagem documental, sem builds nativos ou medições. O gate de integração fica **falhando deliberadamente**, com mensagem de que draft não é aceite; não apresenta um skip como aprovação. |
| Preview | Somente **Aegis Native Preview**, manual, com SHA e versão explícitos. Compilação, contratos essenciais, identidade, versão, distribuição e assinatura isolada; sem emuladores de consumo, baselines recompilados ou homologação integral. |
| Integração | PR aberto/pronto, `ready_for_review`, `synchronize`, `reopened`, `edited` e `merge_group/checks_requested`; também push em `main` e execução manual. Seleciona backend/migrations/transportes, interfaces, Windows/Android e identidade pertinentes. Android conserva framework API 26/35 debug e minificado; Windows conserva instalação/upgrade/inicialização e armazenamento. **Sem janelas longas de consumo nem recompilação de baseline para idle.** |
| Release | Execução manual `stage=release`, com `base_ref` completo para selecionar riscos, ou `full_matrix=true`. Mudanças de geofencing/background/energia/áudio, dependências e escopo desconhecido conservam medições relacionadas. UI/documentação conhecidas não justificam bateria. O publicador continua exigindo a **matriz completa**, incluindo identidade e validações de release assinada. |

UI comum seleciona builds/testes dos hosts e navegador; backend seleciona .NET, PostgreSQL descartável, migrations, Compose apenas por `config` e contratos reais de transporte. Kotlin seleciona Android; Rust compartilhado também seleciona Android e Windows. Alterações de identidade conservam browser/WebView2/WebView Android; contratos de notificações/transportes conservam nativos. Lockfiles podem selecionar compilação ampla de integração sem habilitar consumo prolongado. Mudanças desconhecidas e na infraestrutura de CI continuam conservadoras.

O workflow de identidade é reutilizado pelo CI principal, sem outro disparo automático duplicado. O agregador verifica todos os jobs selecionados, inclusive o resultado desse workflow. Falha, cancelamento, ausência ou skip inesperado de job selecionado impedem aprovação; não há `continue-on-error` nos gates. O SHA de origem e o SHA de evidência (merge de teste do PR ou merge queue) são registrados; todos os jobs usam o SHA resolvido. O gate consulta novamente o PR e recusa head/base diferentes, PR fechado ou retorno a draft. Concorrência por branch/PR cancela execuções substituídas.

`Aegis Node CI result` é o contexto estável de integração. Release usa **Aegis Release Acceptance result**; preview e modos focados não substituem integração. Filtros manuais antigos continuam disponíveis para diagnóstico, com gate final recusando aceite parcial. `base_ref` só limita a homologação de release; integração manual permanece conservadora, impedindo escolher um diff vazio para obter aceite.

Uma atualização da base pode exigir nova execução mesmo sem mudar o head. Configure o requisito de branch atualizada descrito abaixo e atualize a branch/reexecute o candidato quando necessário; não reutilize um resultado anterior ao novo merge de teste.

Para homologação extensa deliberada:

```bash
gh workflow run aegis-node-ci.yml --repo phcastello/Aegis \
  --ref <branch-ou-tag-revisada> -f stage=release -f full_matrix=true
```

Para homologação por risco, use `stage=release`, `full_matrix=false` e `base_ref=<SHA-base-completo>`. Sem base verificável, o seletor conserva a matriz completa do estágio, incluindo consumo. Para v0.7.2, geofencing e execução contextual exigem as medições antes do lançamento estável; elas não se repetem a cada preview.

## Proteção real da main

Inspeção em **22/10/2026**: ruleset [Protect main, 19912815](https://github.com/phcastello/Aegis/rules/19912815) ativo, sem bypass, exigindo PR e impedindo exclusão/force push. **Nenhum status check obrigatório e nenhuma merge queue estavam configurados.** O endpoint de proteção clássica respondeu 404 porque a proteção vem de ruleset. Nenhuma regra, permissão, variável ou segredo foi alterado nesta implementação.

Após validar a infraestrutura no GitHub, o administrador deve editar esse ruleset preservando suas regras existentes e adicionar **Require status checks to pass → Aegis Node CI result**, origem **GitHub Actions** (app observada `15368`), com **Require branches to be up to date**. Não exigir jobs condicionais individuais, o resultado de preview ou o contexto de release. Se adotar merge queue posteriormente, o workflow já trata `merge_group`.

Antes de considerar a proteção operacional: confirme um PR pronto com o candidato atual aprovado; prove em PR descartável que uma falha/omissão selecionada bloqueia o gate; confirme que novo commit/base exige nova evidência. Os testes locais provam a política dos scripts; **não demonstram a aplicação administrativa do requisito**, que permanece pendente. O PR #19 permanece draft, sem promoção deliberada para testar a matriz.

## Backend e lançamento estável

Preview do aplicativo não implica deploy da API. Para teste integrado da v0.7.2 contra a infraestrutura privada existente, a API correspondente e a migration **`20261022133709_ReminderInterventions`** são pré-requisitos. Não é necessário manter outro ambiente permanente.

Obtenha autorização explícita para atualizar a infraestrutura real. Siga [operação privada](operacao-privada.md): registre imagem/commit anterior; preserve projeto Compose, volumes, Data Protection, FCM e configurações; pare escritores numa janela autorizada; produza backup e valide restauração em destino descartável; compile a imagem aprovada; execute o migrador administrativo **antes de iniciar a API nova**; valide health, schema, pareamento, lembretes e intervenções; só então teste os Nodes preview. Em falha, pare escritores e recupere imagem/esquema compatíveis ou restaure backup em destino controlado. Não execute testes destrutivos nem migrations de CI nos bancos reais. Nenhum desses comandos é executado pela preview.

## Nodes headless futuros

O contrato descreve distribuição, instalação, plataformas e procedência sem
exigir GUI. Um futuro runtime headless ou supervisor local deverá executar
atualização/recuperação, manter o estado após desconexão da GUI e reportar saúde.
Uma GUI poderá solicitar operações e acompanhar o resultado. Não implementamos
runtime Linux/headless, novos tipos de Node, protocolo remoto, armazenamento ou
serviço privilegiado nesta etapa.

## Verificação e limites

```bash
node --test .github/scripts/*.test.mjs
python3 -m unittest discover -s .github/scripts -p '*_test.py'
npm run test:release --prefix apps/aegis-node
npm test --prefix apps/aegis-node
npm run build --prefix apps/aegis-node
npm run test:rust --prefix apps/aegis-node
actionlint
```

Os testes locais usam GitHub em memória para concorrência, imutabilidade,
interrupção de upload e avanço do ponteiro; manifests simulados não substituem
uma publicação real. Não se executa homologação de duas horas para validar YAML.
UI deve ser validada nos dois hosts e no layout móvel, inclusive recuperação.

Antes de declarar o mecanismo operacional: bootstrap autorizado, build v2,
aprovação, promoção real, conferência de todos os bytes/manifest e detecção nos
clientes. Depois, homologação física Android/Windows sobre instalação existente:
NodeId, credenciais, pareamento e preferências preservados; preview detectada;
recuperação instalada; próxima atualização normal instalada; funcionalidades
existentes operacionais. Emulação/browser/mocks não comprovam esses resultados.

O registro de testes desta implementação fica em
[v0.7.2 — implementação](v0.7.2-implementation.md). Nenhuma migration ou implantação
real é autorizada implicitamente pela assinatura ou publicação do aplicativo.
