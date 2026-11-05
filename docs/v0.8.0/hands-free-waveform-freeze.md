# Aegis v0.8.0 — economia de bateria, waveform e tela

Correção após o proprietário informar que a waveform da `unstable.14` ainda
travava e que a tela apagava enquanto ele falava. O proprietário confirmou
**economia de bateria ativa**. Também informou que o TTS está temporariamente
desativado para pesquisas: ele não foi reativado, chamado ou modificado.

Branch isolada `fix/v0.8.0-hands-free-waveform-freeze`, base `2a415b0` da
`feat/v0.8.0-hands-free-waveform`, worktree
`/home/phc/Aegis-hands-free-waveform-freeze`.

## Causa reproduzida

Na `.14`, `handsFreePanelMotionAllowed` exigia `!powerSaver`, mesmo quando
`ANIMATOR_DURATION_SCALE=1`. Por isso, ativar economia de bateria colocava o
View em `reduceMotion=true`. Nesse ramo, a waveform só recebia `invalidate()`
na chegada dos blocos de áudio de aproximadamente 200 ms: **cerca de 5 Hz**.
O histórico de 20 ms existia, mas sua apresentação intermediária era eliminada.

O fixture anterior em Chromium exercitava o sinal Kotlin e uma reprodução da
geometria, sem executar essa preferência ou o scheduler da View Android. Ele
não comprovava a fluidez no telefone e não detectou o caminho de economia.

A tela apagava porque a janela limpava `FLAG_KEEP_SCREEN_ON` e nunca aplicava
uma política de retenção durante a fala. A política anterior evitava manter o
display ligado em qualquer circunstância; o novo relato do proprietário requer
retê-lo apenas durante a interação visível.

## Correção

Economia de bateria deixa de ser interpretada como pedido explícito de remover
animações. O valor global de animação continua sendo a autoridade; se indisponível,
usa-se `ValueAnimator.areAnimatorsEnabled()` como fallback. Uma escala explícita
zero continua respeitada. A waveform mantém seu agendamento por vsync durante
a interação, sem exigir que o proprietário desligue a economia de bateria.
O sistema ainda pode limitar a frequência física da tela; o aplicativo não
impõe mais a apresentação de 5 Hz por causa da economia.

`updateScreenPolicy()` aplica `FLAG_KEEP_SCREEN_ON` somente se houver painel
visível, sessão vinculada e estado capturing, continuation, processing ou
speaking. Idle, erro, cancelamento, fechamento e destruição liberam a flag.
Não há nova permissão, wake lock de tela ou mecanismo de desbloqueio. A
solicitação pontual `TURN_SCREEN_ON` existente continua separada: o broadcast
de tela desligada cancela essa solicitação sem transformar a retenção de timeout
em um novo despertar.

A flag pertence à janela real da `VoiceInteractionSession`; não foi criada
Activity de cobertura. O [WindowManager do AOSP](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android16-release/services/core/java/com/android/server/wm/DisplayContent.java)
aplica a retenção às janelas com superfície exibida e `FLAG_KEEP_SCREEN_ON`.
Veja também a [orientação Android sobre liberar a retenção](https://developer.android.com/develop/background-work/background-tasks/awake/screen-on).

O produtor PCM, histórico, suavização, cores, 32 barras, modelo Strong e sua
compatibilidade com baseline permanecem. VAD, STT, TTS, captura, backend,
configurações persistidas, metadados e dados existentes não foram alterados.

## Validação do Android de produção

Foi criado um harness Robolectric isolado, com Android API 35 e gráficos nativos.
Ele carrega **o JAR realmente compilado do aplicativo**. Não há clone da View,
do scheduler ou da Session. O `ViewRoot` executa os `onDraw` em resposta às
invalidações reais. O tempo do Choreographer é controlado explicitamente para
comparação determinística, sem forçar chamadas de `onDraw` no fixture.

Uma cópia imutável do JAR `.14` foi preservada antes de compilar a correção.
Nesse baseline, o mesmo teste de economia+escala1 reproduziu um único desenho,
contra 11 desenhos e 11 alterações de amplitude após a correção, em 187 ms
simulados entre duas entregas de áudio. O caso sem economia já fazia 11 desenhos:
não foi encontrado defeito adicional nesse caminho de vsync.

Os testes da Session usam a janela Android criada por
`ShadowVoiceInteractionSession.create()`. Somente a fronteira do serviço
(`visible`/sessão vinculada) é injetada; os métodos de atualização, erro,
cancelamento, ocultação e destruição são os métodos de produção. Os testes não
chamam `onShow`, não criam o runtime de voz e não abrem microfone ou TTS.

| Verificação | Resultado |
| --- | --- |
| Kotlin/JVM hands-free | 162 testes, zero falhas/erros/skips |
| Contratos JS hands-free/IPC/painel | 19 aprovados |
| View Android real com economia | Antes: 1 desenho; corrigido: 11 desenhos/11 mudanças em 187 ms simulados |
| Window/Session real | Oito casos aprovados: fases ativas, idle/erro, sessão ausente, oculto, hide/destroy, cancelamento, wake pontual e callback antigo |
| Ciclo de vida da View | Quatro casos aprovados: fechar/reabrir, áudio antigo, troca de estado e redução explícita de movimento/silêncio |
| Harness Android completo | 14 testes aprovados contra as classes compiladas da correção |

Esta é evidência de framework Android, não homologação física do telefone.
ADB continuou sem aparelho conectado. Não se declara FPS físico medido.

Comandos executados na worktree:

```bash
cd apps/aegis-node/src-tauri/gen/android
ANDROID_HOME=/tmp/aegis-polish-sdk ./gradlew :app:testUniversalDebugUnitTest \
  --tests 'com.aegis.node.NodeHandsFree*Test' --console=plain --max-workers=2
```

Na raiz:

```bash
node --test apps/aegis-node/scripts/hands-free-contract.test.mjs \
  apps/aegis-node/scripts/hands-free-panel-contract.test.mjs
apps/aegis-node/src-tauri/gen/android/gradlew \
  -p scripts/hands-free/android-view-runtime \
  -PproductionJar=/caminho/privado/fixed-production.jar \
  test --console=plain --max-workers=2
```

O [README do harness](../../../scripts/hands-free/android-view-runtime/README.md)
explica a obtenção do JAR e a comparação com a revisão anterior. Evidências
e JARs imutáveis foram preservados em
`/home/phc/.cache/aegis-hands-free-waveform-freeze/validation-v1/`.

## Arquivos e operação

Produção: `NodeVoiceInteractionSession.kt` e `NodeHandsFreePanel.kt`.
Regressões: `NodeHandsFreePanelMotionTest.kt`, contratos do painel e o harness
de runtime Android documentado junto aos testes. Não foram adicionadas
dependências Robolectric ao aplicativo ou ao APK: elas existem apenas no projeto
de teste separado. JARs, logs e resultados ficam em armazenamento de evidências,
sem pesos privados ou áudio pessoal.

## Publicação concluída

O plano de `publish-dev` confirmou `components=["native"]`, `pending=[]` e
fonte `e90d9b4f2c7213a036a385f63eb3029d3ce4a86e`. O publicador escolheu a próxima
numeração no ledger: **`0.8.0-unstable.15`**, código Android **`8000015`**.
Não houve reserva manual ou mudança da versão estável.

O [workflow oficial](https://github.com/phcastello/Aegis/actions/runs/42127213752)
terminou com sucesso: builds Android ARM64/Windows e assinatura oficial.
O fluxo padrão publica ambos os aplicativos; as alterações de produção desta
rodada estão restritas ao Android. Não foram modificados workflows ou chaves.

- [APK oficial ARM64](https://github.com/phcastello/Aegis/releases/download/node-v0.8.0-unstable.15/Aegis-Android-arm64.apk)
- SHA-256: `1f139b673c3f9fe7f966b9eac24fa31bda196f285b2a92169c23d09c853e9e50`
- Tamanho: 27.852.221 bytes.
- Certificado oficial SHA-256: `df5286c556657ddcee6ce79c510b967678bac8ced18d082db0e318bc47debb31`.
- [Feed recomendado](https://raw.githubusercontent.com/phcastello/Aegis/aegis-distribution/latest.json)
  conferido pela API e pelo endereço público; ambos apontam para os mesmos bytes.

O APK baixado passou por `apksigner`, verificação do manifesto real com versão,
código e identidade, `zipalign` de 16 KB, inspeção do arquivo e ABI ARM64.
O hash do APK coincide com o feed e com `SHA256SUMS`; a tag corresponde à fonte.
As identidades autorizadas Strong/baseline continuam no DEX. Não há pesos privados,
áudios ou keystore no instalador; foi verificada também a ausência dos bytes dos
dois modelos privados, independentemente do nome de arquivo.

Evidência da preparação:
`/home/phc/.local/state/aegis/publish-dev/20261105T230747Z-47d756a0/`.
Verificação do artefato e cópia oficial privada:
`/home/phc/.cache/aegis-hands-free-waveform-freeze/official-0.8.0-unstable.15/`.

Uma revisão independente comparou os dez containers em execução antes e depois:
IDs, imagens e horários de início permaneceram idênticos. Não houve migrations,
deploy ou reinício de backend, TTS ou bancos. O TTS desativado para pesquisas
permanece desativado. Outras worktrees e pesquisas foram preservadas.

## Instalação e limite de homologação

No telefone, usar **Configurações → Atualizações** para instalar a `.15` pelo
fluxo normal, ou abrir o APK oficial. A atualização é sobre a instalação existente;
não exige desinstalar, limpar dados, refazer o pareamento ou importar novamente
o modelo. Strong, baseline e registros anteriores continuam com seus contratos
existentes.

Não havia aparelho conectado ao ADB. A atualização não foi instalada ou observada
fisicamente nesta rodada. Permanecem pendentes a observação da waveform com
economia de bateria e a permanência da tela durante fala real no telefone.
Os testes de estados de reprodução não reproduziram áudio nem reativaram TTS.
Não se declara correção fisicamente homologada: a evidência local reproduz a falha
e valida a correção no framework Android; a CI valida a distribuição oficial.
