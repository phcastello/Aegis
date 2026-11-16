# Correções de wake word e consumo — v0.8.0, 16/11/2026

Esta entrega trata as correções da etapa 1. A confirmação adicional da wake word
fica disponível ao importar o pacote privado `Strong + verificador`.

## Comportamento

O microWakeWord Strong continua detectando continuamente. Cada candidato recebe
uma segunda verificação local com o openWakeWord já treinado. O aplicativo abre
o painel, emite o aviso e inicia o comando somente depois da confirmação.
A verificação utiliza uma thread de CPU, sem spinning e com prazo de dois segundos.
Durante esse trabalho, o microfone continua sendo lido e o começo do pedido fica
retido desde a decisão original. Falhas e resultados atrasados não autorizam o comando.

A importação verifica conteúdo, hashes e os dois runtimes antes da substituição
atômica do modelo. A importação dos TFLite anteriores continua disponível.
O runtime ONNX acompanha o aplicativo; pesos, gravações e fixtures permanecem
privados. A inicialização automática de telemetria foi removida do manifesto
mesclado e a telemetria também é desligada na criação das sessões.

## Evidência

- A exportação recente contém 540 eventos sem rótulos humanos. Cancelamento,
  falha e transcrição vazia não foram tratados automaticamente como negativos.
- Nos 260 contextos já rotulados e conhecidos, Strong reconheceu 97/107 chamadas
  e ativou em 65/153 negativos. A cascata reconheceu as mesmas 97 chamadas e
  ativou em 28 negativos. Não houve treinamento novo nesta rodada.
- A escolha do limiar foi registrada na validação antes da consulta aos
  resultados de teste desta rodada. Os dados já foram usados anteriormente;
  não constituem uma avaliação cega nova.
- Compilação Kotlin/Java real do aplicativo e 189 testes JVM em 17 suítes:
  zero falhas, erros ou testes ignorados. Os modelos e dados privados estavam
  presentes nos testes de importação e verificação.
- O ONNX Runtime JVM 1.31.1 comparou os frames de 164 janelas com a referência
  upstream, tolerância de 0,0001, com as mesmas decisões de aceitação.
- Os 19 testes existentes dos contratos hands-free em Node passaram.
- Os três scripts Python de reprodução passaram na verificação de sintaxe.
- O manifesto Android mesclado não contém o provider de telemetria do ONNX.

Os testes JVM executam a implementação Kotlin e ONNX no servidor 22. Eles
não substituem a homologação do microfone, do tempo de confirmação e do gasto
energético no aparelho. O resultado offline não é uma taxa prevista de alarmes
por hora. As dez chamadas que o Strong não reconhecia continuam dependendo de
melhorias no detector primário.

## Bateria

A captura usa cópias contíguas no buffer circular e agrega suas métricas com uma
única resolução de relógio e identidade por bloco. Microbenchmarks das fontes
reais mostraram menos CPU nessas duas rotinas, com resultados equivalentes.
O custo absoluto observado é pequeno; a autonomia do telefone ainda precisa
ser medida antes de declarar o problema resolvido.

Uma amostra do custo de CPU e do tempo do detector principal é registrada a
cada cinco segundos. Ela permite separar parte do trabalho da inferência do
restante do consumo. As métricas antigas não mediam CPU ou energia e não
explicam o custo do serviço de áudio do Android. As novas métricas também não
representam energia de todo o aparelho.

## Ativação e reversão no telefone

1. Atualizar o aplicativo para a versão de desenvolvimento que contém esta
   correção, em Configurações → Atualizações.
2. Copiar o arquivo privado `aegis-strong-verifier-20261116.zip` para o telefone
   e selecioná-lo no fluxo existente de importação do modelo hands-free.
3. Confirmar a identidade `Strong + verificador` e testar chamadas completas,
   inclusive começando o pedido imediatamente após dizer Aegis.
4. Comparar o comportamento em conversas, música, vídeos e silêncio. Registrar
   os rótulos humanos dos disparos e exportar métricas após uso normal.
5. Para retornar ao Strong anterior, importar o arquivo privado
   `aegis-strong-sem-verificador.int8.tflite` pelo mesmo fluxo.

O pacote tem 2.278.302 bytes e SHA-256
`47df896407b8fc7735253f606e66d747c6c043ddb1cfa64c1b8079355aa6c555`.
O TFLite de retorno tem 39.552 bytes e SHA-256
`1e7c686420f65d8da892f02c71d945f03a8dd528a1005de8c9c8482dce52ad2e`.
Os arquivos privados são entregues separadamente do APK e não entram na release pública.

A homologação física dos falsos alarmes e da bateria continua sendo um requisito
para encerrar estas correções da etapa 1. O cancelamento de música/eco e as demais
funcionalidades da etapa 2 seguem no planejamento, depois dessas correções.

## Relatórios detalhados

- [Wake word: protocolo, seleção e resultados](wakeword-verifier-evaluation.md).
- [Bateria: dados, microbenchmarks e métricas](hands-free-battery-20261116.md).
