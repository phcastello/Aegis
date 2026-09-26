<script setup lang="ts">
import AegisMark from './AegisMark.vue';
</script>

<template>
  <section class="identity-card" aria-label="Identidade da Aegis">
    <div class="identity-card__mark">
      <AegisMark />
    </div>

    <div class="identity-card__copy">
      <div class="identity-card__title">
        <h1>Aegis</h1>
        <span>v0.3.3</span>
      </div>
      <p class="identity-card__codename">Now We're Talking!</p>
    </div>
  </section>
</template>
