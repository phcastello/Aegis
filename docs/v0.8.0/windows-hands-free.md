# Hands-free no Windows e escolha do nó que responde

## Uso

Atualize o servidor privado e todos os nós que escutam “Aegis”. No Windows, abra **Configurações → Áudio e voz → Hands-free**, importe o ZIP privado original `aegis-microwakeword-primario-v2.zip` e marque **Escutar “Aegis”**. O PC precisa estar pareado e habilitado. A escuta usa o microfone padrão e continua enquanto a Aegis estiver na bandeja. A resposta sai pela saída padrão. O sinal sonoro marca o início da captura. Com o chat aberto, um indicador no cabeçalho mostra quando a Aegis escuta o pedido, prepara a resposta, fala ou aguarda a continuação. Ele desaparece quando o PC volta a esperar pela palavra de ativação. Com o chat na bandeja, o estado pode ser consultado ao passar o mouse sobre o ícone da Aegis. Durante um jogo, vídeo ou apresentação em tela cheia, a interação segue por voz e o aplicativo em primeiro plano mantém o foco.

O modo diagnóstico detecta a palavra localmente. A configuração de início com o Windows permite retomar a escuta depois do login. O usuário controla o acesso a microfones nas configurações de privacidade do Windows. O detector usa o Open v2 do pacote importado, com limiar 0,60 e inferência local pelo Windows ML. Os pesos ficam no diretório privado de dados do aplicativo. O ouvinte Android continua usando o perfil instalado naquele aparelho.

## Sistemas estudados

- A Apple descreve a troca de informações por Bluetooth entre aparelhos que ouviram a Siri. A qualidade da escuta e o uso recente do aparelho participam da decisão. [Apple Support](https://support.apple.com/en-sg/105077)
- O Google descreve vários aparelhos detectando a chamada e um único respondendo. Proximidade, capacidade do aparelho e tipo de pedido influenciam sua seleção. [Google Assistant Help](https://support.google.com/assistant/answer/7257763?hl=en)
- A Amazon descreve coordenação centralizada no Echo Spatial Perception. Um estudo posterior da Amazon avaliou extrair características em cada aparelho e combiná-las para estimar proximidade. O artigo relata um método experimental e resultados de simulação. [Alexa ESP](https://developer.amazon.com/en-US/blogs/alexa/post/042be85c-5a62-4c55-a18d-d7a82cf394df/esp-moves-to-the-cloud-for-alexa-enabled-device) · [Barber, Fan e Zhang, ICASSP 2022](https://www.amazon.science/publications/end-to-end-alexa-device-arbitration)

A Aegis pode aplicar a ideia de uma decisão comum antes do sinal de escuta, usando a identidade de nó já pareada ao servidor. O tipo de pedido só fica disponível depois de selecionar o microfone e transcrever a fala. Para esta etapa a escolha usa a qualidade local da chamada e a prioridade configurada no servidor. A qualidade é uma estimativa acústica, sensível ao ganho, à reverberação e à posição do microfone.

A Microsoft usa a área de notificação para expor o estado de programas em segundo plano e recomenda a dica de texto padrão do ícone. A Aegis atualiza o ícone que já está na bandeja e usa os mesmos eventos de voz para mostrar o estado dentro do chat. As transições de escuta dispensam a criação de uma janela e a detecção de cada tipo de aplicativo em tela cheia. [Área de notificação do Windows](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area) · [Atualização de dicas de texto no Tauri](https://docs.rs/tauri/latest/tauri/tray/struct.TrayIcon.html#method.set_tooltip)

## Protocolo

Cada candidato autenticado envia um identificador aleatório por detecção, a relação sinal/ruído quando há histórico suficiente e a confiança quando disponível. Até a decisão, o áudio do começo do pedido permanece num buffer local limitado. O servidor abre uma janela de 650 ms pelo seu próprio relógio, compara a relação sinal/ruído quando todos os candidatos têm essa medida e mantém os nós a até 3 dB do melhor. Entre os restantes, compara a confiança se todos a informaram, com margem de 0,05. A prioridade pareada e o identificador estável resolvem o empate final.

O vencedor emite o sinal, solicita a pausa da mídia compatível e envia o pedido. A continuação fica vinculada a esse nó e à conversa. Os demais descartam os buffers da chamada. Novas candidaturas atrasadas são suprimidas durante dois segundos após a decisão. O cliente exige uma resposta correlacionada dentro de dois segundos da detecção e permanece em silêncio se perder contato com o coordenador.

A API privada atual usa uma instância por proprietário. Dois comandos independentes em cômodos diferentes dentro da mesma janela competem entre si. Outra instância simultânea de servidor exigiria estado de eleição compartilhado. Clientes anteriores à implementação podem iniciar uma conversa por conta própria, por isso a atualização de todos os ouvintes faz parte da instalação.

## Evidência de bancada

- Os detectores Android e Windows têm ritmos diferentes. Com 19 gravações positivas e dez alinhamentos de bloco, ambos detectaram 172 de 190 reproduções. Essa comparação de tempos usou o classificador Open v2 pela implementação de referência em ONNX na CPU. O caminho Android ficou pronto de 120 a 440 ms depois da detecção do Open v2, antes do custo de inferência final e de rede. A mediana foi 320 ms e o percentil 95, 400 ms. Em 91 das 172 detecções conjuntas, a diferença excedeu 300 ms. Isso motivou a janela de 650 ms. As dez repetições usam as mesmas gravações.
- No Windows real, o detector da aplicação processou 615,87 segundos de PCM com os 19 positivos e 128 negativos de teste, separados por dois segundos de silêncio, sem reiniciar o detector. Reconheceu 17 de 19 chamadas e produziu quatro disparos adicionais. Trata-se de um replay distinto do teste histórico de 15 min 22 s.
- Um teste de API com credenciais Android e Windows pareadas alternou o nó com maior relação sinal/ruído. As quatro eleições deram um único vencedor. A suíte também cobre 32 participantes, empates, desistência, repetição e candidaturas tardias.
- No PC, o teste de hardware abriu o microfone e a saída padrão, descartou os blocos capturados e reproduziu PCM silencioso. O teste HTTP do cliente usou áudio sintético para verificar a sequência da eleição à transcrição, chat e reprodução.
- No Windows, um teste em uma área de trabalho isolada manteve uma janela sem bordas de 2560 × 1440 ativa durante oito mudanças de estado sintéticas da escuta. Em 203 verificações, a Aegis preservou o foco e a janela em tela cheia, sem abrir outra janela visível. O teste detectou um popup criado como controle. Jogos em modo de tela cheia exclusiva ainda precisam de avaliação.
- Em uma chamada com PC e S20 FE lado a lado na prévia .33, o servidor recebeu uma candidatura de cada aparelho no mesmo segundo e o usuário ouviu apenas o celular responder. Outras distâncias e o uso de headset continuam pendentes de avaliação. No Windows desta prévia, a continuação por voz abre após a reprodução. A interrupção durante a própria resposta foi avaliada no Android.

## Teste manual

1. Com os dois clientes atualizados, pareados e com hands-free ativo, minimize a Aegis no PC. Fique perto do PC, diga “Aegis”, espere o sinal e faça uma pergunta curta. Observe qual nó sinalizou, onde a resposta tocou e quantas mensagens apareceram na conversa.
2. Repita perto do celular e depois entre os dois. Em um empate acústico a prioridade do nó pode decidir. A continuação deve sair no aparelho vencedor.
3. Repita com o headset definido como dispositivo padrão, primeiro com o chat visível e depois na bandeja. Verifique captura, reprodução e retomada da escuta ao encerrar a conversa.
4. Com a Aegis na bandeja, abra um vídeo, jogo ou apresentação em tela cheia. Faça um pedido de voz e uma continuação. Confira que o conteúdo permanece em tela cheia e que o teclado continua no aplicativo em uso. Ao voltar para o chat durante uma resposta, confira o estado mostrado no cabeçalho.
