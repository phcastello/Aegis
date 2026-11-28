<script setup lang="ts">
import { ref } from 'vue';

const emit = defineEmits<{ files: [files: File[]] }>();
const picker = ref<HTMLInputElement | null>(null);

function selectFiles(event: Event): void {
  const input = event.target as HTMLInputElement;
  if (input.files?.length) emit('files', Array.from(input.files));
  input.value = '';
}
</script>

<template>
  <div class="attachment-picker">
    <input ref="picker" type="file" multiple hidden aria-label="Selecionar arquivos" @change="selectFiles" />
    <button type="button" class="attachment-button" title="Anexar arquivos" aria-label="Anexar arquivos" @click="picker?.click()">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 4v16M4 12h16" /></svg>
    </button>
  </div>
</template>

<style scoped>
.attachment-picker { position: relative; flex: 0 0 auto; }
</style>
