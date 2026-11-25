# Aegis — baseline de bateria anterior ao hands-free

Período: **25 a 30 de outubro de 2026**, seis dias de uso real do proprietário, sem a feature de wake word. O ano decorre do contexto; as imagens exibem dia/mês. Fuso de referência: America/Sao_Paulo.

**31/10/2026 é excluído** como dia de transição. O pós-teste deve começar no primeiro dia completo com o build e a escuta estáveis. Se a instalação ocorrer depois, excluir o dia efetivo de transição também.

## Valores transcritos das telas

| Data | Dia | Uso diário | Tela ligada | Tela desligada | Soma dos tempos | Diferença para 24h, sem causa atribuída |
|---|---|---:|---:|---:|---:|---:|
| 2026-10-25 | domingo | 60% | 02:34 | 18:59 | 21:33 | 02:27 |
| 2026-10-26 | segunda-feira | 90% | 05:01 | 18:20 | 23:21 | 00:39 |
| 2026-10-27 | terça-feira | 82% | 05:51 | 15:45 | 21:36 | 02:24 |
| 2026-10-28 | quarta-feira | 113% | 06:38 | 16:25 | 23:03 | 00:57 |
| 2026-10-29 | quinta-feira | 87% | 04:11 | 14:34 | 18:45 | 05:15 |
| 2026-10-30 | sexta-feira | 97% | 03:26 | 18:25 | 21:51 | 02:09 |

## Estatísticas calculadas, não medições adicionais

- Uso diário médio: **88,1667%**, apresentado como **88,17%**. Mediana: **88,5%**. Intervalo: **60–113%**. Desvio-padrão amostral: **17,50 pontos percentuais**.
- Soma dos percentuais diários: **529 pontos percentuais**; não é número de recargas físicas.
- Tela ligada: total **27h41min**, média **4h36min50s**, intervalo **2h34–6h38**.
- Tela desligada: total **102h28min**, média **17h04min40s**, intervalo **14h34–18h59**.
- Soma dos tempos exibidos: **130h09min** nos seis dias, média **21h41min30s**. Não completar artificialmente para 144 horas.
- Referência semanal exibida em todas as imagens: **85% / “Médio”**. Preserve separadamente da média acima.

## Gráficos: estimativas visuais

Os níveis abaixo são faixas aproximadas lidas das barras, não valores numéricos fornecidos pelo sistema. Os horários de carregamento têm resolução visual limitada e não definem conexões físicas exatas. As imagens preservadas são a evidência primária para as curvas.

| Data | Primeiro nível | Último nível | Menor nível | Maior nível |
|---|---:|---:|---:|---:|
| 2026-10-25 | ~45–50% | ~65–70% | ~5–10% | ~80–85% |
| 2026-10-26 | ~65–70% | ~35–40% | ~0–5% | ~65–70% |
| 2026-10-27 | ~35–40% | ~65–75% | ~10–15% | ~80–85% |
| 2026-10-28 | ~65–75% | ~15–25% | ~15–25% | ~80–85% |
| 2026-10-29 | ~15–25% | ~80–85% | ~0–5% | ~80–85% |
| 2026-10-30 | ~80–85% | ~50–55% | ~15–20% | ~80–85% |

**2026-10-25:** Duas faixas verdes, aproximadamente 15h30–16h30 e 17h30–19h30. Queda ao longo da madrugada/manhã; recargas na tarde/início da noite; queda após a última recarga.

**2026-10-26:** Uma faixa verde, aproximadamente 18h–19h. Descarga até perto da base do gráfico antes da recarga; pico pós-recarga por volta de 55–60%; depois queda.

**2026-10-27:** Duas faixas verdes, aproximadamente 3h30–4h30 e 20h30–22h30. Primeira elevação até cerca de 65–70%, descarga durante o dia e segunda elevação até cerca de 85%.

**2026-10-28:** Uma faixa verde, aproximadamente 14h30–15h30. Descarga até cerca de 20%, recarga até cerca de 85% e nova descarga até cerca de 20% no fim do gráfico.

**2026-10-29:** Duas faixas verdes, aproximadamente 0h30–4h e 21h–23h. Patamar próximo de 85% durante parte da primeira faixa; descarga até perto da base no fim da tarde/noite; nova recarga até cerca de 85%.

**2026-10-30:** Uma faixa verde, aproximadamente 13h–15h30. Descarga da madrugada até cerca de 15–20%; recarga até cerca de 85%; depois queda até aproximadamente 50–55%.

## Metadados comuns visíveis

Tela “Atividade da bateria”, interface em português, tema escuro. Todas as capturas mostram **09:54 e 30%** na barra de status. Estes valores são do momento da captura. A janela semanal visível vai de 25 a 31, com as letras D/S/T/Q/Q/S/S. O dia 31 aparece graficamente, mas não é incorporado ao baseline e não tem percentual numérico transcrito.

No gráfico superior: escala de nível 0/85/100%, eixo de tempo com 0 e 12; legendas “Nível da bateria”, “Nível estimado da bateria”, “Carregando”. No inferior: “Uso da bateria”, “Uso diário”, “Tempo de tela ligada”, “Tempo de tela desligada”, escala 0/85/200%, referência “Médio” e legenda “Esta semana”. Não se deduz previsão de autonomia só da existência da legenda de nível estimado.

Ícones de Wi-Fi, rede móvel/VoLTE e chave compatível com VPN são visíveis. Também aparecem alarme, som silenciado e um pequeno ícone de bateria; confirmar configurações efetivas no aparelho, sem atribuir esses estados retroativamente aos seis dias.

## Limites de interpretação

- Valores diários e tempos foram transcritos das imagens fornecidas pelo proprietário; não foram medidos por adb.
- O ano 2026 e a data de captura presumida 2026-10-31 vêm do contexto da conversa; as imagens mostram dia e mês, não o ano nem a data completa da captura.
- 09:54 e 30% na barra de status descrevem o momento das capturas, não os níveis dos dias históricos selecionados.
- O valor 85% do gráfico semanal é uma métrica exibida pelo sistema; não é a média calculada dos seis dias. A janela visível inclui o dia 31, excluído deste baseline.
- Não se conhece a fórmula exata nem o período completo usado para a média semanal da interface.
- Os valores >100% de uso diário não devem ser truncados; descrevem consumo acumulado, não nível de carga restante.
- A soma de tela ligada e desligada não é 24h. A diferença é apenas aritmética; não equivale automaticamente a tempo carregando, desligado ou não utilizado.
- Os gráficos indicam barras verdes de carregamento e níveis aproximados; não medem energia em mAh/Wh, quantidade precisa de cargas, potência, temperatura ou saúde da bateria.
- Os patamares perto de 85% são compatíveis com um limite de carga, mas as capturas não comprovam essa configuração.
- Tela desligada não comprova suspensão da CPU, repouso real nem ausência de apps em segundo plano.
- Não estão visíveis consumo individual da Aegis, lista completa de apps, modelo do celular, versão Android/One UI, brilho, estado de economia de energia confirmado ou capacidade real da bateria.
- Ícones de Wi-Fi, sinal móvel/VoLTE e chave compatível com VPN são visíveis no momento das capturas, não como histórico de seis dias. Há também alarme/som silenciado e um pequeno símbolo de bateria cuja configuração não foi confirmada.
- Os 6 dias são uso naturalístico relatado pelo proprietário, sem wake word. A janela posterior de cerca de 3 dias será exploratória, não teste causal controlado.
- Dia 2026-10-31 excluído por solicitação do proprietário como transição; excluir também qualquer outro dia parcial de instalação/ativação efetiva.
- Conservar as imagens originais na área privada, sem substituí-las por estimativas ou recortes. Dados estruturados e relatório podem ser versionados conforme solicitado.

## Comparação posterior

Usar cerca de três dias completos de uso normal com a feature funcionando, sem uma sessão artificial de demonstração contínua. Manter e anotar build, modelo e configurações. Capturar telas equivalentes dos dias concluídos e exportar telemetria agregada da Aegis. Não prometer que três dias isolam causalmente um pequeno efeito de bateria.

Registrar tempo real de escuta em tela bloqueada/desligada, indisponibilidades, reinícios do processo/serviço, estado carregando, número de ativações e sessões, tempo de STT/TTS/rede e pausas. Uma feature que morre silenciosamente não pode ser classificada como eficiente.

Comparar média de uso diário com 88,17%, também os tempos de tela e dias da semana. Apresentar diferença em pontos percentuais e diferença relativa separadamente. Não dividir todo consumo pelo tempo de tela ligada como estimador do gasto do detector. Comparação por UID é estimativa de software, não medição isolada de hardware.

Se o uso estável começar em 31/10, a janela sugerida será **01–03/11/2026** (domingo–terça). Comparar secundariamente com **25–27/10**, cujo uso médio foi **77,33%**, além do baseline completo. Não presumir essas datas se a implementação atrasar.

Não resetar as estatísticas do Android, simular bateria, mudar globalmente Doze ou deixar depuração contínua durante a coleta naturalística. Medições técnicas adicionais podem ocorrer em janela separada. Dumps completos e bugreports ficam privados.

## Versionamento

Versionar `baseline.csv`, `baseline.json`, `manifest.json` e este relatório em `docs/v0.8.0/battery/baseline-2026-10-25_30/`. Preservar os seis JPGs, com hashes conferidos, em armazenamento privado fora do Git. O ZIP de transporte também deve ser ignorado. Nenhum dado foi enviado ao repositório por este pacote; a IA codificadora deve executar esse versionamento.
