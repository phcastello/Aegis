# Eval de intenção de tools — v0.3.2

Execução real contra `gpt-5.6-luna`, com `reasoning.effort=medium` e as 13 tools de Gmail exportadas do registro de produção. Os resultados das tools foram simulados; nenhuma operação Gmail real foi executada. Resultado: **18/18**.

| Mensagem | Esperado | Tools observadas | Resultado |
|---|---|---|---|
| Meu professor falou que a prova vai ser difícil. | none | `none` | PASS |
| Preciso responder uma pergunta da faculdade. | none | `none` | PASS |
| O GitHub é uma bagunça às vezes. | none | `none` | PASS |
| Tenho um prazo amanhã. | none | `none` | PASS |
| O que você acha dessa mensagem que eu escrevi? | none | `none` | PASS |
| Isso é importante para a apresentação. | none | `none` | PASS |
| Marcar pontos no texto ajuda a estudar? | none | `none` | PASS |
| Responda essa pergunta de matemática: quanto é 2 + 2? | none | `none` | PASS |
| Confirma que 2 + 2 = 4? | none | `none` | PASS |
| Veja se meu professor mandou algum email sobre a prova. | email | `email_search` | PASS |
| Tem algum email não lido importante? | email | `email_get_status,email_search` | PASS |
| Procura emails do GitHub sobre segurança. | email | `email_get_status,email_search,email_search` | PASS |
| Leia o último email da Unicentro. | email | `email_search,email_read` | PASS |
| Confere se chegou o convite por email. | email | `email_get_status,email_search` | PASS |
| Vê se chegou. | clarify | `none` | PASS |
| Confere aquela mensagem para mim. | clarify | `none` | PASS |
| Pode resolver isso? | clarify | `none` | PASS |
| confirmo | pending | `email_confirm_pending_action` | PASS |

`none` significa resposta sem tool; `clarify` exige pergunta sem tool; `email` exige a tool necessária de busca/leitura; `pending` exige confirmação da ação simulada. Houve uma falha transitória de TLS na primeira tentativa; esta tabela é da repetição completa bem-sucedida.

Em uma sondagem adicional de dois turnos com o mesmo catálogo, a API reportou `cached_tokens=1633` e `cache_write_tokens=15` no primeiro turno, seguidos de `cached_tokens=1648` e `cache_write_tokens=52` no segundo. O aumento de 15 tokens em cache é compatível com o reaproveitamento do limite da mensagem anterior; os números dependem do estado do cache no momento da execução.
