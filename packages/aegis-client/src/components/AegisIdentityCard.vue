<script setup lang="ts">
import AegisMark from './AegisMark.vue';
defineProps<{ version?: string }>();
</script>

<template>
  <section class="identity-card" aria-label="Identidade da Aegis">
    <div class="identity-card__mark">
      <AegisMark />
    </div>

    <div class="identity-card__copy">
      <div class="identity-card__title">
        <h1>Aegis</h1>
        <span>{{ version ? `v${version}` : 'Assistente pessoal' }}</span>
      </div>

    </div>
  </section>
</template>
