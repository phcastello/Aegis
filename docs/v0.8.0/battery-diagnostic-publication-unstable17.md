# Publicação e validação física do diagnóstico de bateria — unstable.17

A versão **0.8.0-unstable.17** foi publicada em 16/11/2026 às 20:33 BRT pelo fluxo
oficial e instalada no S20 FE autorizado para a primeira validação funcional.

- [Release oficial](https://github.com/phcastello/Aegis/releases/tag/node-v0.8.0-unstable.17).
- [Workflow aprovado](https://github.com/phcastello/Aegis/actions/runs/43978977592).
- Fonte: `08a9fc8258adf54ea48c0f0cbedfa1b19ceccd9a`.
- Android: `com.aegis.node`, código `8000017`.
- APK oficial: 62.320.585 bytes; SHA-256
  `2e095ffad6c02a7ae88118c05053b69ccf58583ef2be980ab4f48b0702d6ffff`.

O publicador selecionou somente aplicativos nativos, sem migrations pendentes.
Android/Windows, assinatura isolada e atualização do feed terminaram com sucesso.
A tag/source, reserva, manifesto/feed/checksums e certificado oficial foram
conferidos. O certificado permanece
`df5286c556657ddcee6ce79c510b967678bac8ced18d082db0e318bc47debb31`.
Os nove payloads de manifest/DEX/bibliotecas coincidem entre candidato e assinado.

## Verificações de software

- Compilação Kotlin/Java real e 217 testes em 19 suítes, sem falhas, erros ou skips.
- Incluem 20 testes do coletor, oito do PowerMonitor e 164 janelas privadas de
  paridade ONNX. Os dados/modelos privados estavam presentes na execução local.
- Interface/cliente: 188 testes; contratos de distribuição/JNI: 79 testes.
- Typecheck/build Vue e browser mobile/desktop aprovados. Saída real do coletor
  também foi validada pelo parser da interface.
- Dois testes portáteis do helper Rust, parser do arquivo completo e compilação
  nativa dos targets no CI aprovados.
- APK final não-debuggable e profileable por shell, com os dois métodos de
  controle de bateria públicos/preservados no DEX.
- Guards de ABI/fixtures, ausência de pesos privados, ONNX/R8 e alinhamento ZIP
  e ELF de 16 KiB aprovados.

## Aceite funcional no aparelho

O APK assinado foi atualizado por ADB sem apagar dados, no SM-G781B/Android 13.
Os três botões e o encerramento foram exercitados na interface real.

- A: sem captura/CPU dos detectores; o AudioIn atribuído à Aegis desapareceu.
- B: captura real, sem execução dos detectores; AudioIn voltou.
- C: captura e Strong instalado executaram; os contadores registraram CPU do
  detector. O modelo instalado neste aparelho era Strong bruto, portanto o
  verificador openWakeWord não foi exercitado nessa prova física.
- Os três encerramentos restauraram as configurações normais, com active=null,
  requiresResume=false e gate durável removido.
- Exportação JSON pela tela oficial de arquivos do Android concluída, sem áudios.
- Aumento temporário do tempo de tela usado na conferência foi desfeito e
  verificado; o teste longo deve ocorrer com a tela apagada.

As janelas duraram aproximadamente 14 s, 17 s e 67 s, com tela ligada e ferramentas
conectadas. Todas ficaram corretamente marcadas como não elegíveis por
short_window e screen_on. **Elas comprovam funcionamento e isolamento dos modos,
mas não medem economia nem identificam a parcela causal de descarga.**

Um trace privado de aproximadamente 8 s na .17 registrou 40 seções fechadas
AegisWakeDetector; nenhum evento de perda/overrun/overwrite foi indicado.
As seções do verificador estavam ausentes, como esperado para o modelo bruto.
O aparelho oferece scheduling/frequência/idle; dois eventos solicitados de
wakeup_source não são suportados pelo kernel. As durações dos marcadores são
tempos de parede, não energia nem substitutos do contador de CPU.

## Próxima evidência necessária

Executar A/B/C por cerca de 30 minutos cada, fora do carregador e com tela apagada,
seguindo o [guia](battery-diagnostic-user-guide.md). O relatório permite estimar
B−A e C−B com carga do telefone e examinar CPU/tempo acordado separadamente.
A hipótese de custo da captura é sustentada pela presença de AudioIn em B, mesmo
sem inferência; sua magnitude energética ainda exige essas janelas comparáveis.

HealthStats representa contabilidade do sistema, que pode atualizar depois do
CPU do processo. Corrente instantânea pode exigir verificação da escala do OEM;
o relatório preserva a leitura nominal e não a usa para integrar energia.
O cálculo principal de mAh/h usa a diferença do contador de carga e duração.
Dados indisponíveis permanecem nulos e períodos inválidos não entram nas diferenças.

Evidências brutas, relatórios físicos e traces ficam privados, fora do Git.
No 22: battery-release-unstable17/official-release-verification.json, sob
`/var/tmp/aegis-wakeword-battery-20261116-data/`; os relatórios do aparelho e
Perfetto ficam na mesma área privada e na toolchain.

[APIs e limites pesquisados](battery-investigation-20261116.md).
