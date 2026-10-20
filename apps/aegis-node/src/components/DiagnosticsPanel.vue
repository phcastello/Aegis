<script setup lang="ts">
import { onMounted } from 'vue';
import { useDiagnostics } from '../composables/useDiagnostics';
const { runtime, checking, message, platformLabel, backendLabel, retry } = useDiagnostics();
onMounted(() => { void retry(); });
</script>
<template>
  <section class="diagnostics" :aria-busy="checking" aria-label="Diagnóstico técnico">
    <dl aria-live="polite"><div><dt>Plataforma</dt><dd>{{ platformLabel === 'Unsupported' ? 'Não suportada' : platformLabel === 'Unknown' ? 'Não identificada' : platformLabel }}</dd></div><div><dt>Servidor</dt><dd>{{ checking ? 'Verificando…' : backendLabel === 'Connected' ? 'Disponível' : 'Indisponível' }}</dd></div></dl>
    <small v-if="runtime?.backendUrl">{{ runtime.backendUrl }}</small>
    <p v-if="message" role="status">{{ message }}</p>
    <div class="settings-actions"><button type="button" :disabled="checking" @click="retry">{{ checking ? 'Verificando…' : 'Verificar conexão' }}</button></div>
  </section>
</template>
