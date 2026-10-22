# Distribuição, previews e estágios de CI

Este documento descreve os caminhos implementados e distingue suas pendências operacionais. Os instaladores v0.7.1 publicados permanecem inalterados; editar workflows não demonstra uma distribuição nova.

## Atualização estável e previews

`node-preview/latest.json` conserva sua URL histórica porque Android e Windows já distribuídos dependem dela. Seu contrato passa a ser **somente estável**. O publicador existente também suporta prereleases versionadas `node-vX.Y.Z-unstable.N`, com instaladores e manifesto imutáveis. A preview rápida descrita abaixo usa somente artefatos do Actions e não chama esse publicador. Não existe adesão automática a previews nem seletor de canal nesta manutenção.

`publish-release.mjs` verifica versão, assinatura declarada no manifesto, código Android, URLs e bytes dos assets antes de publicar a distribuição. A verificação criptográfica da assinatura Windows e do certificado Android continua no workflow de release. Apenas uma distribuição estável pública e novamente verificada avança o feed compatível. A preview não consulta nem modifica esse feed, mesmo se ele estiver ausente. Isso protege também clientes antigos que não filtram prereleases.

Uma preview futura no inventário de releases não bloqueia um patch estável de uma linha mantida. Um manifesto histórico de preview no feed também não define o piso estável: pode ser substituído pelo patch estável verificado. Instalações de preview mais novas ignoram versões menores pela comparação da versão instalada e do `versionCode`. Um feed ou uma distribuição estável mais nova continua impedindo retrocesso do canal.

Clientes novos usam o mesmo filtro conservador nos caminhos Android e Windows: candidato estável válido, estritamente posterior à versão instalada e com alocação Android crescente. Assim, `0.7.1 → 0.7.2-unstable.1` é recusado e `0.7.2-unstable.1 → 0.7.2` é permitido. O Android mantém a validação da URL, do código do pacote e do certificado na instalação; o Windows mantém URL versionada e assinatura do updater com a chave existente. Fixtures `stage.N` continuam cobrindo parsing e migração históricos.

Falhas durante upload deixam o draft recuperável e não avançam o feed. Reexecuções com os mesmos bytes completam assets ausentes; bytes diferentes são recusados, inclusive para releases já públicas. Uma falha após publicar a distribuição permite retomar somente o avanço do feed. O GitHub não oferece substituição atômica de um asset por outro de mesmo nome: `--clobber` pode deixar uma janela sem `latest.json` se a transferência falhar depois da exclusão. A aplicação informa feed indisponível; não trata isso como versão atual. Recupera-se repetindo o publicador com a mesma distribuição verificada, sem trocar assets versionados. Os testes simulam interrupções antes da mutação e retry; não prometem transação atômica do GitHub.

O teste do instalador Windows assinado roda para todo lançamento, inclusive futuras versões e previews. Seleciona o último baseline estável sobrevivente estritamente anterior ao candidato: v0.7.1 para versões posteriores a ela, mantendo v0.7.0 no caso histórico de upgrade para v0.7.1. O SHA-256 da v0.7.1 (`f4cda9fc477258ff6c5f3bf420b0430c5ac3fbd2e76c01d145a3466657d494c3`) foi conferido nos metadados do asset GitHub `660986488` e nos bytes baixados. O upgrade original de v0.7.0 a partir da preview removida permanece evidência histórica; o publicador não tenta recriar essa distribuição.

Nenhum desses testes publica feeds artificiais ou altera releases/tags reais.

## Fluxo de trabalho

**Desenvolver → Validar → Push → Gerar preview → Testar → PR pronto → Integrar → Homologar release.**

1. Crie a branch designada a partir de `origin/main`, por exemplo `git switch -c fix/assunto origin/main`. Se ela já existir, reutilize-a e sincronize sem sobrescrever alterações locais; preserve outras worktrees.
2. Execute as verificações dos componentes afetados antes do commit/push, conforme [AGENTS.md](../AGENTS.md). Para UI: testes, typecheck/build e expectativas visuais; para backend: camadas/integrações pertinentes; para capability: contratos de todas as plataformas. Informe limitações do ambiente.
3. Faça commit e `git push -u origin <branch>`. Push de desenvolvimento não inicia a matriz remota. Abra ou mantenha o PR em draft enquanto estiver iterando.
4. Solicite uma preview manual do SHA completo pelo procedimento abaixo. Gerar instaladores não atualiza o backend.
5. Instale deliberadamente sobre uma instalação compatível e teste os dispositivos reais, preservando identidade e dados.
6. Quando a alteração estiver pronta, marque o PR como pronto para revisão. Confira **Aegis Node CI result** no candidato atual antes da integração autorizada.
7. Antes de lançar, solicite homologação de release proporcional ao risco ou a matriz completa. A publicação estável continua sendo outra operação protegida e autorizada.

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

## Preview nativa: bootstrap e configuração pendentes

O novo [workflow](../.github/workflows/aegis-native-preview.yml) não estava registrado no GitHub na inspeção (404). `workflow_dispatch` precisa existir na branch padrão para aparecer e ser acionado normalmente, conforme a [documentação do GitHub](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_dispatch).

A primeira ativação exige um **PR de infraestrutura independente**, baseado em `main`, contendo somente seletor, workflows, scripts/testes da preview, helper curto de upgrade Windows, este procedimento e AGENTS. Não incluir funcionalidades, migration ou metadados v0.7.2. Reutilizar os commits de infraestrutura evita bloquear o bootstrap na homologação funcional do PR #19. Manter o PR de infraestrutura em draft até revisão, executar suas verificações e integrar somente com autorização, respeitando o ruleset. **Não fazer merge automático.** Depois do bootstrap, o workflow é sempre despachado de `main`, enquanto o SHA candidato pode pertencer à branch de desenvolvimento.

Antes da primeira assinatura, o proprietário precisa:

- Criar o environment **`aegis-native-signing`** com reviewer obrigatório e aprovação explícita do proprietário. Restringir deployments à branch exata `main` e, para o publicador existente, tags `node-v*`. Desabilitar bypass de administradores na UI; a API utilizada não comprova essa opção. Num projeto de um só proprietário, não impedir que ele aprove sua própria solicitação, ou designar outro reviewer.
- **Mover** as chaves oficiais hoje no escopo do repositório para esse environment: `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`, `TAURI_SIGNING_PRIVATE_KEY` e senha do updater se existir. Remover as cópias de escopo geral após conferir a migração. O workflow de release também passa a pedir esse environment. Enquanto as chaves permanecerem globais, outro workflow de branch ainda pode referenciá-las; o isolamento da preview não resolve essa configuração global.
- Criar a variável **`AEGIS_ANDROID_FIREBASE_CLIENT_B64`** com o `google-services.json` **de cliente Android existente**, correspondente a `com.aegis.node`/projeto `aegis-1a20c`. Esse dado já integra o APK; nunca fornecer JSON de service account, chave privada ou credencial administrativa nessa variável. O segredo de cliente usado pelo publicador existente pode ser preservado.
- Conferir o catálogo de releases e histórico de runs antes de escolher N; conservar esse histórico para a reserva monotônica. O plano verifica reviewers e allowlist por API e recusa configuração ausente, permissões insuficientes, falha de API ou referência inválida. A política de environment precisa estar disponível no plano GitHub utilizado; se indisponível, manter assinatura no ambiente confiável do proprietário, sem liberar chaves a builds de branch.

Essas ações são administrativas e não foram executadas. A preview só pode ser chamada de operacional após bootstrap, configuração e a primeira execução controlada com verificação dos logs/artefatos.

## Solicitar e obter a preview

Depois do bootstrap e da configuração:

```bash
git fetch origin
git rev-parse origin/fix/v0.7.2-ux-proactivity
# Copie os 40 caracteres, confira o diff e escolha N novo; exemplo deliberado:
gh workflow run aegis-native-preview.yml --repo phcastello/Aegis --ref main \
  -f sha=<SHA-completo> -f branch=fix/v0.7.2-ux-proactivity \
  -f version=0.7.2-unstable.1 -f platform=both
```

`platform` aceita `android`, `windows` ou `both`. O SHA precisa ser alcançável pela branch indicada no próprio repositório; URLs e fontes externas são recusadas. O plano fixa origem, versão e SHA do controle confiável. A versão temporária é sincronizada pelo `version.mjs` existente apenas no checkout do runner; nenhum commit de versão é enviado à branch.

N deve estar entre **1 e 998** e ser maior que todos os números reservados na linha. `0.7.2-unstable.1` usa **7002001**, acima de 0.7.1 (**7001999**) e abaixo da futura estável 0.7.2 (**7002999**). Planos admitidos reservam N mesmo quando builds ou assinaturas posteriores falham ou são cancelados; solicitações recusadas antes do plano não reservam versões. Escolha N novo ao repetir uma solicitação e não use Re-run para gerar outros bytes com a mesma versão. Uma preview anterior da mesma linha exige N maior. O código estável definitivo não é consumido. O plano consulta todas as páginas de releases, runs e seus planos admitidos e recusa retrocesso em relação ao inventário conhecido; instalações privadas fora desse inventário devem ser conferidas antes da instalação. Não apague o histórico de reservas para reutilizar versões.

A compilação roda sem segredos de assinatura e sem código de outro repositório. Android usa chave descartável só para produzir o APK intermediário; esse artefato **não serve para atualizar a instalação oficial**. O job de assinatura usa outro runner, checkout confiável da `main`, ferramentas fixadas e environment aprovado. Ele não executa instaladores nem scripts do candidato: verifica os bytes e reassina o APK com o certificado oficial. Windows recebe assinatura de updater com versão vinculada e verificação criptográfica pela chave pública existente. **Assinatura de updater não é Authenticode**; o projeto não provisiona certificado de assinatura de código Windows, e esta mudança não promete remover SmartScreen.

Android verifica package ID, versão/código, certificado oficial, ARM64, release não depurável, ausência de instrumentation/fixtures, política de rede e recursos Firebase. Windows conserva produto, identificador, perfil/instalação e configuração do updater; antes da assinatura, o job sem chaves instala o NSIS num runner descartável e verifica versão, WebView e preservação do perfil anterior. A continuidade de NodeId/cofre/credenciais e hardware permanece uma verificação específica de integração e homologação, sem alegar uso físico do aparelho do proprietário.

Abra **Actions → Aegis Native Preview → execução**, aprove conscientemente o environment após revisar SHA/artefatos, e baixe somente **`Preview-<versão>-<plataforma>-<SHA>`**. Cada artefato final inclui instalador, `preview.json`, `SHA256SUMS` e, no Windows, `.sig`. O manifesto registra SHA de fonte/controle, run, número, plataforma e hashes. Os builds intermediários `Untrusted-build-*` têm retenção de um dia; os finais, 14 dias. Nenhuma release, tag ou feed é criado. **`node-preview/latest.json` continua somente estável**, sem adesão automática a previews.

Antes de instalar, confira hash, versão/código e identidade com a instalação existente. Android: mesmo pacote/certificado, código maior; instalar por cima, sem desinstalação ou `pm clear`. Windows: encerrar também tray, manter escopo/usuário/diretório, Credential Manager e perfil WebView2; instalar por cima. Registre NodeId/pareamento antes e depois sem registrar tokens. Pare diante de divergência. A futura estável pode substituir a preview compatível; downgrade para 0.7.1 tem código menor e não constitui rollback seguro.

## Backend e lançamento estável

Preview do aplicativo não implica deploy da API. Para teste integrado da v0.7.2 contra a infraestrutura privada existente, a API correspondente e a migration **`20261022133709_ReminderInterventions`** são pré-requisitos. Não é necessário manter outro ambiente permanente.

Obtenha autorização explícita para atualizar a infraestrutura real. Siga [operação privada](operacao-privada.md): registre imagem/commit anterior; preserve projeto Compose, volumes, Data Protection, FCM e configurações; pare escritores numa janela autorizada; produza backup e valide restauração em destino descartável; compile a imagem aprovada; execute o migrador administrativo **antes de iniciar a API nova**; valide health, schema, pareamento, lembretes e intervenções; só então teste os Nodes preview. Em falha, pare escritores e recupere imagem/esquema compatíveis ou restaure backup em destino controlado. Não execute testes destrutivos nem migrations de CI nos bancos reais. Nenhum desses comandos é executado pela preview.

Depois de integração e aceite físico, prepare a release estável com versão sincronizada, tags e publicação somente mediante autorização. **Aegis Node Release** resolve o tag existente uma vez, usa esse SHA na matriz completa, exige o environment para build assinado, verifica assinatura e distribuição e só depois executa o publicador protegido. As verificações de instalador Windows assinado e APK oficial foram preservadas. O caminho legado de release ainda executa build do candidato com chaves após aprovação; a preview acrescenta isolamento sem reescrever esse publicador. Por isso a revisão do código/tag autorizado continua necessária.

## Verificação da infraestrutura e desempenho

```bash
node --test .github/scripts/*.test.mjs
python3 -m unittest discover -s .github/scripts -p '*_test.py'
npm run test:release --prefix apps/aegis-node
npm run test:rust --prefix apps/aegis-node
# Com actionlint disponível:
actionlint
```

Os testes do seletor cobrem eventos draft/pronto, novos commits/base, subsistemas, dependências, preview/release, falha de histórico e gates omitidos/falhos. Os testes dos workflows verificam concorrência, isolamento, ausência de publicação/feeds e preservação dos gates de release. Os testes de preview cobrem SHA/branch, versão monotônica, sincronização temporária, procedência e falha de API/configuração. A validação sintática usa actionlint. Não se executa a matriz de duas horas para validar YAML.

Evidência anterior: run [39803911696](https://github.com/phcastello/Aegis/actions/runs/39803911696), Android API 35 **2h02min31s** (15:22:12–17:24:43 UTC); API 26 **23min01s**; ARM64 **11min47s**. A remoção das seis janelas de dez minutos e duas de repouso do estágio de integração elimina **80 minutos programados**, além da recompilação do baseline. Isso não é uma medição de duração final: compilação, testes framework, filas e caches continuam variáveis. Preview tampouco tem prazo garantido antes da primeira execução real. O push/draft deste trabalho comprovará apenas o caminho leve; homologação e assinatura continuam pendentes até sua execução autorizada.
