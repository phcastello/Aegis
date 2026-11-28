# Execução de scripts em Python

A Aegis pode rodar Python a partir do chat digitado, da voz manual ou do
hands-free. A ferramenta atende cálculos, sorteios, simulações e transformação
ou análise de dados. O código usa a biblioteca padrão, pode receber texto UTF-8
pela entrada padrão e pode abrir os anexos que o usuário envia à conversa.

## Arquivos por conversa

Se você enviar um ZIP com planilhas e pedir um CSV consolidado, a Aegis pode
abrir os originais e entregar o CSV na própria resposta. Em seguida, você pode
pedir “agora refaz sem março”. Os arquivos de trabalho ficam disponíveis por
72 horas desde a última mensagem dessa conversa. Uma nova mensagem renova o
prazo. Abrir o histórico ou baixar um arquivo mantém o prazo anterior.
Quando o prazo termina, o chat conserva os originais e cada versão entregue.

O processo Python termina após cada chamada. A próxima execução recebe os arquivos
intermediários salvos em `/work`. Isso permite retomar o trabalho sem manter um
interpretador ocioso. A limpeza também ocorre quando a conversa é excluída.
[Ciclo de vida de um Codespace](https://docs.github.com/en/codespaces/about-codespaces/understanding-the-codespace-lifecycle)

`/inputs/index.json` lista nomes, identificadores, tamanhos e caminhos dos
anexos dessa conversa. O executor monta os originais selecionados para leitura.
Arquivos regulares escritos em `/outputs` ficam disponíveis para download na
resposta concluída. Um trabalho delegado usa as mesmas três pastas. Os arquivos
de conversas diferentes ficam isolados. [Entrega de arquivos no chat](anexos.md)

O runtime inclui zlib para que `zipfile` possa ler e gravar arquivos ZIP com
compressão DEFLATE, inclusive planilhas e outros documentos que a utilizam.
[ZIP no Python](https://docs.python.org/3/library/zipfile.html)

## Decisão e entrega

A Aegis executa tarefas curtas no mesmo turno com `run_python`. Para um
trabalho que deve causar uma espera perceptível, ela usa `delegate_python`
antes da primeira execução. O aviso de intenção é salvo no chat e falado no
hands-free. A fila persistente guarda o vínculo com o pedido e o trabalhador
recebe o contexto necessário. Ele pode usar apenas a ferramenta Python.
O usuário pode continuar a conversa, cancelar pelo estado do trabalho ou
reabrir o aplicativo para ver o resultado salvo.

Enquanto o hands-free estiver ativo, o Node que fez o pedido lê a resposta
completa a partir da conclusão quando o áudio estiver livre. As decisões de
retomar a locução, parar de ler ou aceitar um novo pedido seguem o mecanismo
que já existe para pesquisa em segundo plano.

## Sorteio

Para sortear um inteiro de `a` a `b`, com os dois extremos incluídos, o
script usa `a + secrets.randbelow(b - a + 1)`. Um intervalo com extremos
iguais devolve aquele inteiro. A Aegis devolve o valor produzido na execução.
O módulo usa a fonte de aleatoriedade do sistema. [Documentação de secrets](https://docs.python.org/3/library/secrets.html)

## Isolamento e implantação

O backend empacota CPython para WASI e executa cada script no Wasmtime. A
instância recebe a biblioteca padrão como somente leitura e a entrada do
pedido. O executor restringe rede, acesso ao sistema de arquivos do servidor,
variáveis de ambiente e recursos por execução. Se o executor empacotado
estiver indisponível, a ferramenta fica fora do catálogo. As versões e
somas de verificação das dependências estão em
[`backend/script-runtime/build.sh`](../../backend/script-runtime/build.sh).
A distribuição ocorre na imagem do backend, sem instalar Python nos Nodes.
[Isolamento do Wasmtime](https://docs.wasmtime.dev/security.html)
· [Limitações do Python em WASI](https://docs.python.org/3/library/intro.html#webassembly-platforms)

O contrato aceita até 16 KiB de código e 256 KiB na entrada textual por execução.
Anexos usam leitura de arquivo e seguem os limites do chat. Cada script tem até
30 segundos, 96 MiB de memória WebAssembly e 16 KiB por fluxo de saída textual.
O servidor executa até dois scripts simultaneamente e serializa os do mesmo chat.
O espaço intermediário admite até 768 MiB e 4.096 itens por conversa. Um volume
guarda os intermediários entre chamadas, e as duas execuções compartilham um
`tmpfs` de 1 GiB para intermediários abertos e novos resultados. Os originais
selecionados são preparados em disco e liberados no fim da execução.
[Volumes Docker](https://docs.docker.com/engine/storage/volumes/) · [Armazenamento temporário no Docker](https://docs.docker.com/engine/storage/tmpfs/)
O código, a entrada e a saída bruta da ferramenta são omitidos da auditoria
das chamadas de ferramenta. O pedido e a resposta continuam no histórico.
