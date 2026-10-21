<script setup lang="ts">
import { computed } from 'vue';
import AegisMark from './AegisMark.vue';
import { getReleaseIdentity } from '../services/releaseIdentity';
const props = defineProps<{ version?: string }>();
const identity = computed(() => getReleaseIdentity(props.version));
</script>

<template>
  <section class="identity-card" aria-label="Identidade da Aegis">
    <div class="identity-card__mark">
      <AegisMark />
    </div>

    <div class="identity-card__copy">
      <div class="identity-card__title">
        <h1>Aegis</h1>
        <span>{{ identity.versionLabel }}</span>
      </div>
      <p class="identity-card__codename">{{ identity.codename }}</p>
    </div>
  </section>
</template>
