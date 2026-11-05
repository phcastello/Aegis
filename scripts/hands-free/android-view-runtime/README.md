# Regressão do painel Android no runtime real

Este harness executa as classes Kotlin de produção já compiladas, sem copiar
a implementação do painel e sem chamar `draw()` ou `onDraw()` no fixture.
Usa Robolectric 4.17, Android API 35, gráficos nativos, ViewRoot e Choreographer.
O tempo de vsync é controlado em passos de 16 ms; `OnDrawListener` conta as
travessias reais e as amplitudes são lidas depois da apresentação.

Precisa do JDK usado no build Android e de um `classes.jar` produzido pelo
build da mesma revisão. As dependências são apenas de teste e não entram
no APK. O primeiro uso baixa o runtime Android instrumentado e o motor
gráfico nativo; use um cache com espaço disponível.

Na raiz da worktree, depois do build Android:

```bash
apps/aegis-node/src-tauri/gen/android/gradlew \
  -p scripts/hands-free/android-view-runtime \
  -PproductionJar=/caminho/absoluto/para/classes.jar \
  test --console=plain --max-workers=2
```

O JAR pode ser obtido em
`app/build/intermediates/runtime_app_classes_jar/universalDebug/bundleUniversalDebugClassesToRuntimeJar/classes.jar`
(ou no diretório de build externo configurado para o app). Não copie o JAR
para o Git. Para comparar duas revisões, preserve os JARs fora do repositório
e execute o mesmo comando com cada caminho.

Os testes cobrem o ramo de economia de bateria com animações ativadas,
quadros entre entregas acústicas de 200 ms, redução explícita de movimento,
limpeza de silêncio, troca de estado, sessão antiga, esconder/reabrir e
`FLAG_KEEP_SCREEN_ON` na Session e Window reais. A fronteira de anexação
do serviço é simulada; o fixture não abre microfone, Node runtime ou TTS.

Essa execução comprova lógica e agendamento do Android em Robolectric.
Não comprova fluidez ou permanência da tela em um aparelho físico.
