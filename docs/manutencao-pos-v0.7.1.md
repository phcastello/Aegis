# Manutenção pós-v0.7.1

Base: `origin/main` em `5552de452c8465ec2eeacdef32308f45d5aa981d`, conferida por
fetch em 21/10/2026. Branch `chore/post-v0.7-maintenance`, worktree separado
`../Aegis-maintenance`; checkout original e alterações locais preservados.
Não houve merge, deploy, release, alteração de tags ou dos feeds públicos.

## Checkpoints e retomada

| Checkpoint | Commit | Resultado |
| --- | --- | --- |
| 1 — Base/documentação | `bf453d3` | Versão instalada no badge correto, email com arquivos explícitos, README atual e história preservada. |
| 2 — Áudio | `04dcd67`, revisão `9d11037` | Recursos por sessão, cancelamento/abort, diagnóstico e preferência de entrada sem corridas. |
| 3 — Web Push | `2d8edf0` | Encerrada ativação/envio; somente ações antigas e fila preservadas. |
| 4 — Distribuição/operação/CI | `4f9e4d8` | Feed estável seguro, Compose privado, migrations explícitas e seleção de jobs testada. |
| 5 — Redução/fechamento | Commit que contém este registro | Dois métodos órfãos e três declarações redundantes removidos; matriz final abaixo. |

Estado: implementação concluída e regressão local aprovada por área. Próximo passo:
regressão em checkout novo no SHA final, envio e PR, matriz CI explicitamente acionada.
Verificar os checks do PR; skips de seleção não representam aceite físico.

## Matriz A01–A16

| ID | Estado | Evidência e pendência |
| --- | --- | --- |
| A01 | Corrigido | Descarte invalida antes de `stop`; zero upload iniciado após descarte e zero inserção tardia. Abort não desfaz bytes enviados. |
| A02 | Corrigido | Sessões possuem tracks, recorder, chunks, timers, listeners e controller; permissões invertidas não afetam a nova captura. |
| A03 | Validação pendente | Seleção local, medidor real e silêncio digital implementados/testados; causa/aceite físico Windows desconhecidos. |
| A04 | Corrigido | Reproduzido timeout em Sobre; badge exige exatamente `v${appVersion}` sem chamar formatador de produção; paridade e passos posteriores passaram. |
| A05 | Compatibilidade temporária | Novas inscrições recebem 410; UI/sender/pacote retirados. Restam ações/tokens antigos e fila, sem presumir banco vazio. |
| A06 | Corrigido | VAPID removido de DI/startup/config/Compose/exemplo/gerador; Production sobe mesmo com ambiente legado inválido. Poll nativo e Data Protection preservados. |
| A07 | Corrigido | Preview manual nunca toca feed compatível; filtros stable Android/Windows, monotonicidade de canal e retry testados. Sem publicação real. |
| A08 | Corrigido | README 78.414 → 5.888 bytes, 578 → 80 linhas; guia nativo atual em português e índice técnico. |
| A09 | Corrigido | Comando antigo retornava sucesso/zero testes; dono compartilhado executa nove testes email, alias PWA preservado e alvos verificados. |
| A10 | Corrigido / já corrigido antes | Baseline Windows do PR17 já era estável com SHA; links de previews retiradas agora apontam a commits preservados, binários indisponíveis. Baseline Android por SHA mantido. |
| A11 | Corrigido | Override Production, binds explícitos, banco novo/existente e migrador da imagem testados isoladamente; implantação real não executada. |
| A12 | Corrigido | Métodos genéricos sem consumidores diretos/por interface/reflexão removidos; loop com ferramentas, títulos e tipos usados preservados. |
| A13 | Corrigido | PWA removeu apenas declarações duplicadas de dompurify/markdown-it/tipos, com ci/build por passo; shared mantém renderer e sanitização. |
| A14 | Corrigido | Seletor com casos docs/UI/backend/Kotlin/Rust/locks/build/workflows; agregadores detectam falha/skip indevido; manual full e gates de release preservados. |
| A15 | Adiado deliberadamente | Legibilidade melhorou nos fluxos alterados de áudio; RemindersPanel, PlacesPanel e CSS amplo têm consumidores e ficam para ciclo próprio. |
| A16 | Adiado deliberadamente | Caso a reproduzir: intenção temporária de lembrete salva como memória duradoura. Investigar distinção pedido/fato, origem e retenção; sem refatorar ou apagar/reindexar memória. |

## Evidências e limites

- **Base:** timeout de 30 s reproduzido na asserção exata antiga. Após reparo,
  paridade web/nativa, 91 casos visuais de updater, nove variações de Lugares e
  seis de Lembretes passaram. A falha posterior na paginação era ordem da fixture;
  resposta agora é liberada após conferir estado pendente, sem aumentar timeout.
- **Áudio:** harness histórico isolado com TypeScript do lockfile, blob original
  `6e33517b9e8f030cb81a8738e41d1b6fa1a28958`, reproduziu os três defeitos antigos
  (exit0 significa reprodução). Dezessete regressões atuais exigem comportamento
  seguro. Chromium com MediaRecorder/WebAudio reais e sinal sintético passou nos
  hosts web e fixtures Android/Windows. Não abriu microfone físico nem STT pago.
- **Backend/operação:** .NET8 com PostgreSQL 16 descartável: 697 passaram, 6 ignorados
  que requerem Qdrant/Neo4j, 0 falhas. NodeAdmin da imagem aplicou 26 migrations em
  banco novo e esquema inicial existente, preservou conversa sintética e repetiu
  sem reaplicar. Production isolada: health 200, Swagger 404, configuração push
  inativa e inscrição 410, inclusive com variáveis VAPID inválidas.
- **Web Push:** teste browser atualiza worker histórico para o novo, preserva
  IndexedDB/localStorage alheios e fila; 503 fica pendente e 410 termina; cache,
  navegação offline e exclusão de `/api` aprovados. Native OK/Adiar mantém seus testes.
- **Distribuição/CI:** publicador em memória cobre stable/preview, versões menores/
  iguais, metadata incoerente e interrupção/retry. Rust portável: 78 passaram e 2
  ignorados existentes. Seletor 15/15, baseline assinado 3/3 e actionlint nos três
  workflows passaram. Windows/emulador/medições longas dependem dos checks nativos.
- **Dependências:** removidas uma a uma, sem mudar versões dos pacotes restantes.
  Builds e testes dos dois hosts passaram. Avisos npm residuais de ferramentas/SSR
  estão registrados em [dependências](reducao-dependencias.md), sem atualização geral.
- **Links:** checagem local automatizada; releases estáveis e commits preservados
  relevantes confirmados pela API GitHub. Tags remotas de preview foram retiradas;
  cópias locais não foram consideradas garantia de publicação.

## Operação e distribuição futura

Documentação/testes não exigem instalar um aplicativo novo. Lifecycle/diagnóstico
precisam de novos apps/PWA; encerramento Web Push precisa da API/PWA; filtros dos
clientes precisam de nova distribuição nativa. A proteção de clientes antigos vem
principalmente do publicador, que deve usar esta política na próxima release.
A versão e os links continuam 0.7.1; nenhum instalador modificado foi publicado.

A API Production requer migrations antes do startup, pelo comando one-off documentado
em [operação privada](operacao-privada.md). Backup, mesmo projeto/volumes/key ring e
recuperação em banco separado são requisitos do procedimento; nada foi aplicado no
servidor do proprietário. [Distribuição/CI](distribuicao-e-ci.md) descreve a janela
não atômica de upload do asset GitHub e retry com os mesmos bytes verificados.

Sem inventário autorizado dos bancos/instalações reais, a compatibilidade Web Push
continua somente até comprovar última emissão por todas as implantações antigas e
expiração máxima de 30 dias. Critério exato em [legado](web-push-legado.md); nenhuma
migration, assinatura persistida ou fila foi apagada para presumir encerramento.

## Documentação e pendências

O README anterior fica em [arquivo](arquivo/README-ate-v0.7.1.md), com links relativos
ajustados. Stages e relatórios mantêm seus caminhos para preservar consumidores.
[Cliente de modelo](reducao-cliente-modelo.md) documenta símbolos removidos e usados;
[áudio](audio-maintenance-validation.md) separa mocks, sinal sintético e hardware.

O aceite no microfone Windows do proprietário permanece pendente. Conferir entrada,
permissões, WebView2, mute/driver e RDP são hipóteses de diagnóstico, sem causa física
comprovada. Android físico e medições do novo binário não podem ser inferidos das
fixtures ou do percurso antigo aceito. Geofencing, modelos/STT/TTS, dados de memória,
identidades, chaves, migrations e histórico foram preservados.
