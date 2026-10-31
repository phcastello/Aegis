<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { getPreferredVoiceInput, listVoiceInputs, setPreferredVoiceInput } from '../services/voiceInput';
const inputs = ref<MediaDeviceInfo[]>([]);
const selected = ref(getPreferredVoiceInput());
const message = ref('');
const supported = !!navigator.mediaDevices?.enumerateDevices;
let disposed = false;
let refreshSequence = 0;
async function refresh() {
  const sequence = ++refreshSequence;
  try {
    const devices = await listVoiceInputs();
    if (disposed || sequence !== refreshSequence) return;
    inputs.value = devices;
    selected.value = getPreferredVoiceInput();
    // Hidden labels/IDs before permission do not prove that a saved input vanished.
    const namedDevices = devices.some(device => device.label);
    message.value = selected.value && namedDevices && !devices.some(device => device.deviceId === selected.value)
      ? 'A entrada salva não foi encontrada. A próxima gravação tentará a entrada padrão se ela continuar indisponível.'
      : devices.length ? 'Os nomes podem aparecer após você permitir uma gravação. A seleção vale para a próxima captura.'
        : 'Nenhuma entrada visível. Confira as permissões e os dispositivos do sistema.';
  } catch { if (!disposed && sequence === refreshSequence) message.value = 'Não foi possível listar as entradas. A gravação ainda pode usar o microfone padrão.'; }
}
function select(event: Event) {
  selected.value = (event.target as HTMLSelectElement).value;
  setPreferredVoiceInput(selected.value);
  message.value = 'Preferência salva nesta instalação. Será usada na próxima gravação.';
}
onMounted(() => { void refresh(); navigator.mediaDevices?.addEventListener?.('devicechange', refresh); });
onBeforeUnmount(() => { disposed = true; navigator.mediaDevices?.removeEventListener?.('devicechange', refresh); });
</script>
<template>
  <details class="voice-input-settings">
    <summary>Microfone</summary>
    <p>Esta seleção vale para gravações pelo chat e fica nesta instalação. O hands-free Android usa a entrada nativa do sistema.</p>
    <template v-if="supported">
      <label for="voice-input-device">Entrada de áudio</label>
      <select id="voice-input-device" :value="selected" @change="select">
        <option value="">Padrão do sistema</option>
        <option v-if="selected && !inputs.some(input => input.deviceId === selected)" :value="selected">Entrada salva (indisponível ou sem permissão)</option>
        <option v-for="(input, index) in inputs.filter(input => input.deviceId && input.deviceId !== 'default')" :key="input.deviceId" :value="input.deviceId">{{ input.label || `Microfone ${index + 1}` }}</option>
      </select>
      <div class="settings-actions"><button type="button" @click="refresh">Atualizar entradas</button></div>
      <p class="settings-muted" role="status">{{ message }}</p>
    </template>
    <p v-else>A plataforma não permite listar entradas. Será usado o padrão do sistema.</p>
    <p class="settings-muted">Sem sinal no Windows? Confira a entrada escolhida, o acesso ao microfone nas configurações de privacidade, o dispositivo de gravação e a sessão remota. Esses pontos são verificações, não uma causa diagnosticada.</p>
  </details>
</template>
<style scoped>
.voice-input-settings { margin-top: 20px; }
select { display: block; width: 100%; max-width: 100%; margin-top: 8px; padding: 10px; color: inherit; background: var(--bg-surface, #18181b); border: 1px solid var(--border-color, #444); border-radius: 8px; }
</style>
