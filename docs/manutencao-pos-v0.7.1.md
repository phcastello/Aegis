# Manutenção pós-v0.7.1

Base: `origin/main` em `5552de452c8465ec2eeacdef32308f45d5aa981d`, conferida por
fetch em 21/10/2026. Branch `chore/post-v0.7-maintenance`, worktree separado em
`/home/phc/Aegis-maintenance`. O checkout original e suas alterações foram preservados.
Não há autorização para merge, deploy, release, tags ou alteração de feeds públicos.

## Checkpoints

1. **Base/documentação — `bf453d3`, validado.** Reproduzido timeout de 30 s na paridade
   procurando `0.7.1` em Sobre; expectativa corrigida no badge instalado para
   `v${appVersion}`, independente da função de produção. O comando email antigo
   retornava sucesso com zero testes no Node 22; agora o pacote dono nomeia os três
   arquivos explicitamente e a PWA delega a ele. README anterior arquivado com links
   ajustados; previews retiradas apontam para fontes preservadas, nunca outro binário.
   Builds PWA/nativo e email (9 testes) aprovados; paridade completa web/nativo
   aprovada após corrigir também a corrida da fixture de paginação (resposta
   controlada, sem aumentar timeout nem mudar o produto).
2. **Captura de áudio — validada em automação.** Recursos por sessão, descarte
   imediato/abort, escolha local de entrada, nível real e silêncio digital.
   16 regressões e 58 testes compartilhados passaram. Chromium com MediaRecorder/
   WebAudio reais e sinal sintético passou nos hosts web e fixtures Android/Windows.
   Não houve teste físico; ver [evidências de áudio](audio-maintenance-validation.md).
3. Web Push — implementação em andamento; nenhuma consulta ao banco do proprietário.
4. Distribuição/operação/CI — implementação em andamento; sem operação em produção.
5. Redução seletiva — consumidores a confirmar. A16 permanece fora do escopo.

Próximo passo: completar paridade/aviso de atualização/Lugares/Lembretes e registrar
os commits e resultados de cada checkpoint. A matriz A01–A16 será fechada com evidência.

## Fontes e limites

Pacote fornecido extraído na raiz do checkout original e deste worktree. Reprodução
histórica isolada em `/tmp/aegis-audio-historical`; blob de proveniência
`6e33517b9e8f030cb81a8738e41d1b6fa1a28958`, sem substituição pela implementação nova.
As fontes de manutenção recebidas continuam locais e não integram o produto.

`gh release list` e `git ls-remote --tags origin` confirmaram somente distribuições
nativas estáveis v0.7.0/v0.7.1 e feed `node-preview`; tags de previews antigas só
persistiam no clone local. O baseline Windows estável do PR #17 já estava reparado;
o baseline Android por SHA preservado não depende de release removida.

Não houve captura de áudio real, acesso a STT pago ou inventário do banco de produção.
Geofencing, memória, IDs, chaves e migrations históricas permanecem no escopo de preservação.
