# Execução de scripts em Python

A Aegis pode rodar Python a partir do chat digitado, da voz manual ou do
hands-free. A ferramenta atende cálculos, sorteios, simulações e transformação
ou análise de dados. O código usa a biblioteca padrão e recebe texto UTF-8
opcional pela entrada padrão. Texto livre, listas, tabelas, CSV e JSON podem
ser fornecidos na conversa. Cada execução começa em um ambiente novo.

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

O contrato aceita até 16 KiB de código e 256 KiB de entrada por execução.
Cada execução tem até cinco segundos, 96 MiB de memória WebAssembly e
16 KiB por fluxo de saída. O executor permite até duas execuções simultâneas.
O código, a entrada e a saída bruta da ferramenta são omitidos da auditoria
das chamadas de ferramenta. O pedido e a resposta continuam no histórico.
