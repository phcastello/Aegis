<script setup lang="ts">
import { onMounted } from 'vue';
import { useDiagnostics } from '../composables/useDiagnostics';

const { runtime, checking, message, platformLabel, backendLabel, retry } = useDiagnostics();
onMounted(() => { void retry(); });
</script>

<template>
  <main class="diagnostics" :aria-busy="checking">
    <h1>Aegis</h1>
    <p class="subtitle">Stage 01 · Tauri Foundation</p>
    <dl aria-live="polite">
      <div><dt>Platform:</dt><dd>{{ platformLabel }}</dd></div>
      <div><dt>Backend:</dt><dd>{{ backendLabel }}</dd></div>
    </dl>
    <p v-if="runtime?.backendUrl" class="backend-url">{{ runtime.backendUrl }}</p>
    <p v-if="message" role="status" class="message">{{ message }}</p>
    <button type="button" :disabled="checking" @click="retry">
      {{ checking ? 'Checking…' : 'Retry' }}
    </button>
  </main>
</template>
