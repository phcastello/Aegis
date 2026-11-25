# Visualizador de entrada no chat Windows

A revisão mantém o histórico de 32 amplitudes realmente medidos e a separação entre entrada, processamento e saída definida no [estudo anterior](hands-free-waveform.md). O problema visual era a concentração desses 32 valores num cartão de até 196 px, com um SVG que esticava conjuntamente as dimensões horizontais e verticais. A revisão amplia apenas a entrada (`capturing` e `continuation`), sem criar janela, ativar ou selecionar uma conversa nem alterar a apresentação de pensamento e da voz sintetizada.

- O cartão de entrada acompanha a largura disponível no cabeçalho do chat. Em janela ampla, a indicação de estado fica à esquerda das barras e o espaço restante é preenchido pelo histórico. Em janela estreita, a indicação passa para cima e mantém o espaço horizontal das barras.
- A visualização de entrada passa de 34 para 40 px de altura; o máximo das barras passa de 30.1 unidades no SVG anterior para 36 px. Os centros são distribuídos proporcionalmente por 88% da largura, e a espessura das barras é limitada de 1 a 8 px, sem esticar horizontalmente os retângulos ao redimensionar.
- A área não captura eventos de ponteiro. Os botões do histórico e da conversa, incluindo Nova conversa, permanecem operáveis; a barra lateral aberta em desktop conserva sua própria coluna. O estilo se aplica apenas quando o slot nativo do Windows contém a indicação de entrada ativa. O layout compartilhado/PWA não é alterado.
- O contrato de 32 amplitudes, isolamento por geração, expiração e visibilidade, a animação aprovada de pensamento e as 16 bandas suavizadas de saída permanecem distintos. Nenhuma amplitude é fabricada para preencher a largura.

## Verificação

O teste Chromium `test:hands-free-settings-browser` usa amplitudes conhecidas e estados IPC simulados. Ele verifica o espaço ocupado (mais de 80% do chat na janela de 1180 px sem barra lateral), altura, cobertura horizontal, espessura máxima, foco, estado e recuperação. Com uma conversa sintética selecionada, verifica o menu e o botão Nova conversa via cliques; em largura de 320 px confirma a ausência de sobreposição com a navegação. Os testes de formato, geração, silêncio, expiração, movimento reduzido, fases de saída e processamento continuam executados. É verificação de apresentação, não prova de captura acústica no aparelho físico.
