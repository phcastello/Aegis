# Latência do aviso: medição no S20 FE (25/11/2026)

## Observação na prévia .38

O proprietário relatou cerca de três segundos até ouvir o aviso. A exportação
obtida por USB contém três ativações concedidas da `0.8.0-unstable.38`, com
`aegis-microwakeword-primario-v2-2026-11-24`, modo normal. A análise utilizou
somente `metrics.json`; nenhuma gravação foi lida ou transferida para esta
investigação. O tempo da tela foi restaurado depois da extração.

| Estágio registrado no aparelho (ms) | Média | Mín.–máx. |
| --- | ---: | ---: |
| Observação da decisão primária (estimativa por amostra) | 78,3 | 40–114 |
| Lookahead restante | 92,0 | 77–120 |
| Verificador local | 138,3 | 62–198 |
| Espera do coordenador (requisição completa) | 1.181,0 | 1.016–1.266 |
| Observação do grant pelo capturador | 48,7 | 39–58 |
| Preparação após observar o grant | 58,0 | 21–126 |
| Decisão primária estimada → solicitação do aviso | 1.596,3 | 1.567–1.642 |
| Enfileiramento → retorno de `AudioTrack.play()` | 38,3 | 28–57 |

A espera do coordenador corresponde a aproximadamente 74% do tempo entre a
decisão primária estimada e a solicitação do aviso. Isso inclui conexão, rede,
autenticação, inventário e eleição; não mede isoladamente cada parcela. A API
registrava dois nós pareados habilitados (Android e Windows), e a identidade
atual apresentou cinco candidatos, dos quais três foram concedidos e dois
suprimidos. A janela máxima de 650 ms só era eliminada quando todos os
participantes tinham respondido. Não há, nessa versão, evidência de qual tempo
foi efetivamente gasto dentro do árbitro ou se uma conexão TLS precisou ser
aberta para cada chamada.

O marco da decisão primária não é o início/fim acústico de “Aegis”. A chamada
`AudioTrack.play()` também não mede o som físico no alto-falante. Portanto, não
se contradiz o relato de aproximadamente três segundos contando toda a chamada.
A amostra é pequena e não é estimativa de confiabilidade geral do wakeword.

## Mudanças em avaliação

O foco é reduzir a espera redundante depois de uma confirmação local, sem tocar
no detector, seus limiares, no cancelamento de eco ou no volume do aviso. O
protocolo mantém a exclusividade de um vencedor e o descarte do PCM dos demais
nós. Uma idade local estimada (relógio monotônico) permite aproveitar parte do
tempo já transcorrido desde a detecção; a janela continua limitada e conserva a
espera anterior para clientes sem a informação. As métricas adicionais separam
a espera medida no árbitro do restante da requisição. A conexão HTTP ociosa pode
ser reaproveitada por mais tempo sem enviar sondas ou manter heartbeats novos.

## Aceitação física

Comparar a mesma sequência de chamadas com um nó audível e com ambos em alcance.
Registrar mediana, pior caso, tempo da fronteira estimada até o aviso, espera no
árbitro, restante da requisição e ocorrências de respostas duplicadas. Separar
conexão fria de chamadas consecutivas. Não extrapolar o ganho de uma bancada
para o tempo ouvido pelo usuário ou para o consumo de bateria. Preservar a
capacidade de falar imediatamente após o sinal, sem perder o início do pedido.
