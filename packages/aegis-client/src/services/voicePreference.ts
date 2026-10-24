import { ref } from 'vue';
const key = 'aegis.voice.autoSpeak';
export const autoSpeak = ref(localStorage.getItem(key) !== 'false');
export function setAutoSpeakPreference(enabled: boolean): void {
  localStorage.setItem(key, String(enabled)); autoSpeak.value = enabled;
}
window.addEventListener('storage', event => { if (event.key === key) autoSpeak.value = event.newValue !== 'false'; });
