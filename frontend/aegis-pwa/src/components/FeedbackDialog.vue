<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type {
  FeedbackRating,
  FeedbackReason,
  LocalChatMessage,
  SubmitMessageFeedbackRequest
} from '../types/chat';

interface ReasonOption {
  value: FeedbackReason;
  label: string;
}

const props = defineProps<{
  message: LocalChatMessage;
  rating: FeedbackRating;
  isSaving?: boolean;
  errorMessage?: string | null;
}>();

const emit = defineEmits<{
  cancel: [];
  submit: [request: SubmitMessageFeedbackRequest];
}>();

const goodReasons: ReasonOption[] = [
  { value: 'good_tone', label: 'Tom bom' },
  { value: 'useful', label: 'Útil' },
  { value: 'clear', label: 'Clara' },
  { value: 'concrete', label: 'Concreta' },
  { value: 'good_criticism', label: 'Boa crítica' },
  { value: 'respected_constraint', label: 'Respeitou restrição' },
  { value: 'other', label: 'Outro' }
];

const badReasons: ReasonOption[] = [
  { value: 'bad_tone', label: 'Tom estranho' },
  { value: 'not_useful', label: 'Não foi útil' },
  { value: 'too_verbose', label: 'Falou demais' },
  { value: 'too_generic', label: 'Genérica demais' },
  { value: 'ignored_constraint', label: 'Ignorou restrição' },
  { value: 'hallucinated_capability', label: 'Inventou capacidade' },
  { value: 'repeated_topic', label: 'Repetiu assunto' },
  { value: 'did_not_answer', label: 'Não respondeu' },
  { value: 'wrong_context', label: 'Contexto errado' },
  { value: 'other', label: 'Outro' }
];

const selectedReason = ref<FeedbackReason | ''>('');
const comment = ref('');
const correctedAnswer = ref('');

const title = computed(() => (props.rating === 'good' ? 'Boa resposta' : 'Resposta ruim'));
const reasons = computed(() => (props.rating === 'good' ? goodReasons : badReasons));
const shouldShowCorrection = computed(() => props.rating === 'bad');

watch(
  () => [props.message.id, props.rating],
  () => {
    selectedReason.value = '';
    comment.value = '';
    correctedAnswer.value = '';
  },
  { immediate: true }
);

function normalizeOptional(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

function handleSubmit(): void {
  emit('submit', {
    rating: props.rating,
    reason: selectedReason.value || null,
    comment: normalizeOptional(comment.value),
    correctedAnswer: shouldShowCorrection.value ? normalizeOptional(correctedAnswer.value) : null
  });
}
</script>

<template>
  <div class="feedback-overlay" role="presentation" @click.self="emit('cancel')">
    <form class="feedback-dialog" role="dialog" aria-modal="true" :aria-label="title" @submit.prevent="handleSubmit">
      <header class="feedback-dialog__header">
        <div>
          <p>{{ title }}</p>
          <span>{{ message.content }}</span>
        </div>
        <button type="button" class="icon-button" aria-label="Fechar" :disabled="isSaving" @click="emit('cancel')">
          x
        </button>
      </header>

      <label class="feedback-field">
        <span>Motivo</span>
        <select v-model="selectedReason" :disabled="isSaving">
          <option value="">Sem motivo específico</option>
          <option v-for="reason in reasons" :key="reason.value" :value="reason.value">
            {{ reason.label }}
          </option>
        </select>
      </label>

      <label class="feedback-field">
        <span>Comentário</span>
        <textarea v-model="comment" rows="3" :disabled="isSaving"></textarea>
      </label>

      <label v-if="shouldShowCorrection" class="feedback-field">
        <span>Como deveria ter respondido?</span>
        <textarea v-model="correctedAnswer" rows="4" :disabled="isSaving"></textarea>
      </label>

      <p v-if="errorMessage" class="feedback-error" role="alert">{{ errorMessage }}</p>

      <footer class="feedback-dialog__actions">
        <button type="button" class="ghost-button" :disabled="isSaving" @click="emit('cancel')">
          Cancelar
        </button>
        <button type="submit" class="save-feedback-button" :disabled="isSaving">
          {{ isSaving ? 'Salvando...' : 'Salvar feedback' }}
        </button>
      </footer>
    </form>
  </div>
</template>
