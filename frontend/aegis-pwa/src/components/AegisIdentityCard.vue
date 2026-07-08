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
        <span>v0.2.1</span>
      </div>
      <p class="identity-card__codename">Inbox Familiar</p>
      <p class="identity-card__note">Um espaço calmo para pensar em voz alta.</p>
    </div>
  </section>
</template>
