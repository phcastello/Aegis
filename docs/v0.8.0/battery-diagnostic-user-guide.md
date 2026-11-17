# Como comparar o consumo da escuta no Android

[Entrega oficial: 0.8.0-unstable.17](battery-diagnostic-publication-unstable17.md).

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
5. Escolher **Iniciar comparação automática** e apagar a tela. O aplicativo
   aguarda a tela apagar para começar A e avança sozinho para B e C.

Não é preciso deixar o computador conectado para o ensaio. USB/depuração são
recursos opcionais para análise avançada, não requisitos do coletor no app.

## Três testes de aproximadamente 30 minutos

| Botão | O que funciona na janela |
| --- | --- |
| A · Escuta desligada | Observação de bateria; captura e detectores desligados. |
| B · Somente microfone | Mesmo AudioRecord MIC, PCM16 mono a 16 kHz, leituras de 200 ms; sem carregar Strong/openWakeWord ou executar ativação. |
| C · Detecção local | Captura e detecção local completas, incluindo o verificador quando instalado; sem beep, painel, coleta de áudio, transcrição ou resposta. |

## Início único e tela apagada

1. Tocar **Iniciar comparação automática** e confirmar na própria tela que está
   aguardando a tela apagar.
2. Apagar a tela e deixar o telefone sem uso por cerca de **1h30**. Não é preciso
   destravar ou tocar no app entre os cenários.
3. Ao voltar, consultar as três janelas e exportar o relatório. A conclusão
   restaura a preferência de escuta anterior automaticamente.
4. Se precisar usar o aparelho, voltar à tela e tocar **Cancelar comparação**.
   O trecho incompleto fica registrado e não completa uma comparação válida.

O Android pode atrasar uma transição. O relatório registra as durações reais;
o próximo cenário recebe seus próprios 30 minutos completos. A previsão de
1h30 é aproximada, principalmente sem acesso a alarmes exatos. O app usa a
autorização já disponível, sem mudar a otimização de bateria nem pedir uma
permissão adicional para este ensaio.

**Opções avançadas** conserva os testes individuais e oferece **Verificar
automação (sem medir consumo)**: três etapas de um minuto para conferir se o
telefone permite as transições com tela apagada. Esse teste também pode sofrer
atraso do Android e nunca entra nos resultados de consumo.

Reinício, interrupção, falha de armazenamento ou recusa do microfone pausa a
sequência. O app não continua uma janela cujo consumo não observou. Encerre a
comparação e inicie outra após resolver a causa. Alterações explícitas da
preferência de escuta feitas durante o teste são respeitadas.

Nos controles individuais, começar cada cenário, apagar a tela e encerrá-lo
manualmente após cerca de 30 minutos. O limite de segurança de uma hora dos
controles individuais é observado em callbacks já existentes; não substitui o
encerramento manual.

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
