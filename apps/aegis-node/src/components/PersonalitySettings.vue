<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { invoke } from '@tauri-apps/api/core';

interface InstructionSettings { text: string; revision: string }
const emit = defineEmits<{ dirty: [value: boolean] }>();
const saved = ref<InstructionSettings | null>(null);
const draft = ref('');
const busy = ref(false);
const conflict = ref(false);
const error = ref('');
const status = ref('');
const dirty = computed(() => saved.value !== null && draft.value !== saved.value.text);
watch(dirty, value => emit('dirty', value), { flush: 'sync' });

async function load() {
  busy.value = true;
  error.value = '';
  try {
    const current = await invoke<InstructionSettings>('node_custom_instructions');
    draft.value = current.text;
    saved.value = current;
  } catch (reason) {
    error.value = typeof reason === 'string' ? reason : 'Não foi possível carregar as instruções.';
  } finally {
    busy.value = false;
  }
}

async function save() {
  if (!saved.value || busy.value || !dirty.value) return;
  busy.value = true;
  error.value = '';
  status.value = '';
  try {
    const { applied, settings: current } = await invoke<{ applied: boolean; settings: InstructionSettings }>('node_custom_instructions', {
      settings: { text: draft.value, revision: saved.value.revision }
    });
    saved.value = current;
    if (!applied && dirty.value) {
      conflict.value = true;
      return;
    }
    draft.value = current.text;
    conflict.value = false;
    status.value = current.text ? 'Instruções salvas.' : 'Instruções apagadas.';
  } catch (reason) {
    error.value = typeof reason === 'string' ? reason : 'Não foi possível salvar as instruções. Seu texto continua no campo.';
  } finally {
    busy.value = false;
  }
}

function useSaved() {
  if (saved.value) draft.value = saved.value.text;
  conflict.value = false;
}
onMounted(load);
</script>

<template>
  <section>
    <h2>Personalidade</h2>
    <p v-if="busy && !saved" role="status">Carregando instruções…</p>
    <form v-if="saved" @submit.prevent="save">
      <label for="custom-instructions">Instruções personalizadas</label>
      <p id="instruction-scope" class="settings-muted">Diga como prefere receber as respostas. Vale para o chat e o hands-free em todos os seus dispositivos.</p>
      <textarea id="custom-instructions" v-model="draft" rows="6" maxlength="2000" :disabled="busy"
        aria-describedby="instruction-scope instruction-length" @input="status = ''; error = ''"></textarea>
      <p id="instruction-length" class="settings-muted">{{ draft.length }} de 2.000 caracteres</p>
      <div v-if="conflict" role="alert">
        <p>Outro dispositivo alterou as instruções. Confira a versão salva antes de substituí-la.</p>
        <blockquote class="saved-instructions">{{ saved.text || 'Instruções apagadas' }}</blockquote>
        <button type="button" :disabled="busy" @click="useSaved">Usar versão salva</button>
      </div>
      <div class="settings-actions">
        <button type="submit" :disabled="busy || !dirty">{{ busy ? 'Salvando…' : conflict ? 'Salvar minha versão' : 'Salvar' }}</button>
        <button type="button" :disabled="busy || !draft" @click="draft = ''; status = ''; error = ''">Limpar campo</button>
      </div>
    </form>
    <p v-if="error" role="alert">{{ error }}</p>
    <button v-if="!saved && !busy && error" type="button" @click="load">Tentar novamente</button>
    <p v-if="status" role="status">{{ status }}</p>
  </section>
</template>

<style scoped>
.saved-instructions { white-space: pre-wrap; overflow-wrap: anywhere; margin-inline: 0; }
</style>
