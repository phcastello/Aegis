# Como comparar o consumo da escuta no Android

O diagnóstico fica em **Configurações → Áudio e voz → Investigar consumo de bateria**.
As três janelas usam o mesmo telefone, configuração e versão. Nenhuma gravação
é guardada ou enviada por esses testes. A Aegis não responde a chamadas durante
a janela, inclusive no cenário C.

## Preparação

1. Atualizar para a versão de desenvolvimento que contém este diagnóstico.
2. Manter a Aegis selecionada como assistente e a permissão de microfone concedida.
   Para C, importar também o modelo privado desejado pelo fluxo normal.
3. Começar preferencialmente com 50–80% de bateria. Desconectar o carregador e
   aguardar alguns minutos se o aparelho estiver quente.
4. Usar a mesma rede, sinal e ambiente nos três testes. Evitar música, vídeos,
   jogos, downloads, chamadas e outras interações no primeiro comparativo.
   Manter a mesma configuração de economia de bateria e de tela sempre ativa.
5. Usar um relógio externo para acompanhar os 30 minutos. O aplicativo não cria
   alarme ou wake lock para encerrar a referência com a escuta desligada.

Não é preciso deixar o computador conectado para o ensaio. USB/depuração são
recursos opcionais para análise avançada, não requisitos do coletor no app.

## Três testes de aproximadamente 30 minutos

| Botão | O que funciona na janela |
| --- | --- |
| A · Escuta desligada | Observação de bateria; captura e detectores desligados. |
| B · Somente microfone | Mesmo AudioRecord MIC, PCM16 mono a 16 kHz, leituras de 200 ms; sem carregar Strong/openWakeWord ou executar ativação. |
| C · Detecção local | Captura e detecção local completas, incluindo o verificador quando instalado; sem beep, painel, coleta de áudio, transcrição ou resposta. |

Para cada cenário:

1. Tocar o botão e verificar o nome do teste ativo.
2. Apagar a tela logo após começar. Em B/C, o indicador de microfone do Android
   pode permanecer visível, pois a captura é real.
3. Deixar o aparelho sem uso por cerca de 30 minutos.
4. Desbloquear, voltar à mesma tela e tocar **Encerrar teste e voltar às
   configurações anteriores**.
5. Repetir para o próximo cenário.

O encerramento restaura a preferência de escuta que o usuário mantém nas
configurações. Um desligamento explícito feito durante o ensaio é respeitado.
Reinício, interrupção ou falha deixa a escuta pausada até encerramento explícito;
a captura não é retomada automaticamente como se a medição tivesse continuado.
O limite de segurança de uma hora é verificado em callbacks já existentes,
sem alarme que acorde o telefone; não substitui o término manual em 30 minutos.

Janelas abaixo de 20 minutos, carregadas, com captura insuficiente ou muita tela
ligada ficam registradas para diagnóstico, mas não entram na comparação
automática. A tabela também exige mesma versão/configuração/modelo/aparelho e
firmware. Ela conserva diferenças negativas, que podem indicar variação do
ambiente ou precisão insuficiente; não as apresenta como economia comprovada.

## Entregar o resultado

Ao terminar, tocar **Exportar relatório de bateria (sem áudio)**, salvar o JSON
no telefone e anexá-lo neste chat para análise. Esse botão exporta contadores e
contexto de energia/CPU, sem incluir as ativações de áudio guardadas anteriormente.
O ZIP tradicional de métricas e ativações continua disponível separadamente.

Na primeira rodada, são suficientes A/B/C. Se as diferenças forem pequenas ou
as condições variarem, a análise poderá pedir uma repetição em ordem inversa.

## Como os números serão interpretados

- A carga/energia e o nível de bateria pertencem ao **telefone inteiro**. O
  medidor de carga fornece mAh apenas quando o fabricante o disponibiliza.
- B−A estima o custo adicional da captura e dos serviços do sistema necessários.
  C−B estima o processamento acrescentado pela detecção. Outros processos e
  estados de suspensão também podem influenciar essas diferenças.
- CPU é tempo de trabalho medido. O thread de captura inclui o detector; essas
  linhas se sobrepõem e não devem ser somadas. CPU não é convertida em mAh.
- O tempo acordado usa os relógios monotônicos do Android. Ajuda a comparar
  suspensão entre janelas, sem atribuí-la exclusivamente a uma função.
- Os trilhos de energia de aparelhos compatíveis com Android15+ são consultas
  opcionais e representam hardware compartilhado, não energia exclusiva do app.
- Ausência de sensor, relógio inválido ou reinício não vira consumo zero.

O diagnóstico é genérico para os Android suportados pelo aplicativo. As APIs
são detectadas no próprio aparelho; a primeira investigação no S20 FE/Android 13
não introduz regras específicas para Samsung. A instalação em outro aparelho
continua exigindo as permissões e a configuração normal de assistente/modelo.

[APIs, ferramentas, limites e próximos experimentos](battery-investigation-20261116.md).
