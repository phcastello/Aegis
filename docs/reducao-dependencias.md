# Dependências de renderização — A13

Os imports de `dompurify` e `markdown-it` estão em `MarkdownMessage.vue` e
`mapAttribution.ts`, no pacote compartilhado. O host PWA não possui import próprio;
o host nativo já não declarava essas bibliotecas. As dependências e os tipos
continuam no dono, `packages/aegis-client`, com sanitização Markdown preservada.

Foram removidas da PWA, uma a uma, as declarações de `dompurify`, `markdown-it` e
`@types/markdown-it`. Cada passo regenerou apenas o lockfile da PWA e executou
`npm ci` seguido do build/typecheck. Nenhuma versão dos pacotes restantes mudou.
Os dois hosts compilaram com a resolução a partir do pacote compartilhado;
os testes de Markdown seguro permaneceram ativos. Um checkout novo com `npm ci`
nos três pacotes é usado também na regressão integrada final.

`vue` permanece nos hosts e no pacote compartilhado: os entrypoints o importam e
Vite usa `resolve.dedupe` para uma única instância por aplicação. Os tipos Markdown
continuam como dependência de desenvolvimento do pacote que contém o renderer.
O Dockerfile da PWA já instala esse pacote separadamente e seu arquivo
`Dockerfile.dockerignore` específico inclui as fontes necessárias; foi preservado.

## Avisos de dependências observados

O `npm ci` inicial informou 18 avisos na árvore PWA e 8 na nativa; o pacote
compartilhado informou zero. `npm audit --json` registrou, entre outros, avisos em
DOMPurify, PostCSS, Vue/server-renderer e ferramentas Vite/Tailwind transitivas.
Isso é evidência do inventário de versões, sem demonstração de exploração na Aegis.

A declaração redundante da PWA mantinha DOMPurify 3.4.8, inclusive com os avisos
[GHSA-cmwh-pvxp-8882](https://github.com/advisories/GHSA-cmwh-pvxp-8882) e
[GHSA-6688-9rhm-gjv2](https://github.com/advisories/GHSA-6688-9rhm-gjv2).
Ela foi retirada; a implementação compartilhada continua usando o lockfile com
DOMPurify 3.4.16. Os caminhos citados pelos avisos envolvem configurações/hooks
específicos; esta manutenção não demonstrou XSS pelo renderer atual.

Os avisos residuais de desenvolvimento, parsing de sourcemaps, servidor de
desenvolvimento/editor e SSR precisam de avaliação e correção próprias, mantendo
testes e atualização focalizada. Os clientes atuais são aplicações de browser,
sem servidor Vue SSR; o build processa CSS do repositório. A retirada de duplicatas
não representa aprovação irrestrita dessas outras dependências. Não foi executado
`npm audit fix` nem atualização geral de versões nesta tarefa.
