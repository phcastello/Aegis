<!-- Copyright (C) 2026 Pedro Hasson Castello.
     GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md. -->
<script setup lang="ts">
import { ref } from 'vue';
import { clientEnvironment, openClientLink } from '../services/clientEnvironment';
import license from '../../../../LICENSE?raw';
import attributionTerms from '../../../../ADDITIONAL_TERMS.md?raw';
import authors from '../../../../AUTHORS.md?raw';
import notices from '../../../../NOTICE.md?raw';

const linkError = ref<string | null>(null);
async function followSource(event: MouseEvent): Promise<void> {
  if (clientEnvironment().externalLinks !== 'system' || (event.type === 'auxclick' && event.button !== 1)) return;
  event.preventDefault();
  try {
    await openClientLink('https://github.com/phcastello/Aegis');
    linkError.value = null;
  } catch { linkError.value = 'Não foi possível abrir o código-fonte no navegador. Tente novamente.'; }
}
</script>

<template>
  <section class="legal-notices" aria-labelledby="legal-notices-title">
    <h3 id="legal-notices-title">Autoria e informações legais</h3>
    <p>Aegis — criada originalmente por <strong>Pedro Hasson Castello</strong>.</p>
    <p>Copyright (C) 2026 Pedro Hasson Castello, quanto às suas contribuições originais.
      Outros autores conservam os direitos sobre suas contribuições.</p>
    <p>Software livre sob a GNU AGPL versão 3 exclusivamente (AGPL-3.0-only),
      com termos adicionais de atribuição razoável. Você pode usar, modificar e
      redistribuir este software, inclusive comercialmente, conforme esses termos.</p>
    <p>Fornecido SEM GARANTIA, na medida permitida pela lei, salvo garantias
      expressamente oferecidas. Consulte as seções 15 a 17 da licença.</p>
    <p><a href="https://github.com/phcastello/Aegis" target="_blank" rel="noopener noreferrer"
      @click="followSource" @auxclick="followSource">Código-fonte do projeto</a></p>
    <p v-if="linkError" role="alert">{{ linkError }}</p>
    <details><summary>Ler a licença GNU AGPLv3 integral</summary><pre tabindex="0">{{ license }}</pre></details>
    <details><summary>Ler os termos adicionais de atribuição</summary><pre tabindex="0">{{ attributionTerms }}</pre></details>
    <details><summary>Autores</summary><pre tabindex="0">{{ authors }}</pre></details>
    <details><summary>Avisos e material de terceiros</summary><pre tabindex="0">{{ notices }}</pre></details>
  </section>
</template>

<style scoped>
.legal-notices { margin-top: 24px; padding-top: 20px; border-top: 1px solid var(--line); }
.legal-notices a { color: var(--accent-strong); text-decoration: underline; }
.legal-notices pre { white-space: pre-wrap; overflow-wrap: anywhere; max-height: 55vh; overflow: auto; font: inherit; line-height: 1.55; }
.legal-notices a:focus-visible, .legal-notices pre:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>
