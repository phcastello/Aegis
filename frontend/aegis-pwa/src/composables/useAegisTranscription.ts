import { computed, onBeforeUnmount, ref } from 'vue';
import { getTranscriptionStatus, transcribeAudio } from '../services/aegisApi';

export type TranscriptionState =
  | 'idle'
  | 'requesting_permission'
  | 'recording'
  | 'transcribing'
  | 'unavailable'
  | 'error';

const DEFAULT_MAX_RECORDING_MILLISECONDS = 90_000;
const MIME_CANDIDATES = [
  'audio/webm;codecs=opus',
  'audio/webm',
  'audio/mp4;codecs=mp4a.40.2',
  'audio/mp4'
];

export function useAegisTranscription(onTranscript: (text: string) => void) {
  const state = ref<TranscriptionState>('idle');
  const available = ref(false);
  const errorMessage = ref<string | null>(null);
  const notice = ref<string | null>(null);
  const maxRecordingMilliseconds = ref(DEFAULT_MAX_RECORDING_MILLISECONDS);
  let recorder: MediaRecorder | null = null;
  let mediaStream: MediaStream | null = null;
  let chunks: BlobPart[] = [];
  let recordingStartedAt = 0;
  let durationTimer: number | null = null;
  let transcriptionController: AbortController | null = null;
  let discardOnStop = false;
  let disposed = false;
  let captureSequence = 0;

  const isRecording = computed(() => state.value === 'recording');
  const isTranscribing = computed(() => state.value === 'transcribing');
  const isBusy = computed(() => isRecording.value || isTranscribing.value || state.value === 'requesting_permission');

  async function refreshStatus(): Promise<void> {
    try {
      const status = await getTranscriptionStatus();
      if (Number.isFinite(status.maxRecordingSeconds) && status.maxRecordingSeconds > 0) {
        maxRecordingMilliseconds.value = Math.floor(status.maxRecordingSeconds * 1000);
      }
      available.value = status.enabled && status.configured;
      if (!available.value && state.value !== 'recording' && state.value !== 'transcribing') {
        state.value = 'unavailable';
      } else if (available.value && state.value === 'unavailable') {
        state.value = 'idle';
      }
    } catch {
      available.value = false;
      if (!isBusy.value) state.value = 'unavailable';
    }
  }

  async function start(): Promise<void> {
    if (disposed || isBusy.value || !available.value) {
      if (!available.value) state.value = 'unavailable';
      return;
    }

    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') {
      available.value = false;
      state.value = 'unavailable';
      errorMessage.value = 'Este navegador não oferece gravação de voz.';
      return;
    }

    state.value = 'requesting_permission';
    errorMessage.value = null;
    notice.value = null;
    discardOnStop = false;
    const sequence = ++captureSequence;
    try {
      mediaStream = await navigator.mediaDevices.getUserMedia({ audio: true });
      if (disposed || sequence !== captureSequence) {
        releaseMicrophone();
        return;
      }

      const mimeType = MIME_CANDIDATES.find((candidate) => MediaRecorder.isTypeSupported(candidate));
      recorder = mimeType ? new MediaRecorder(mediaStream, { mimeType }) : new MediaRecorder(mediaStream);
      chunks = [];
      recorder.ondataavailable = (event) => {
        if (event.data.size > 0 && !discardOnStop) chunks.push(event.data);
      };
      recorder.onstop = () => { void finishRecording(); };
      recorder.start();
      recordingStartedAt = Date.now();
      state.value = 'recording';
      durationTimer = window.setTimeout(() => {
        notice.value = `Limite de ${Math.ceil(maxRecordingMilliseconds.value / 1000)} segundos atingido.`;
        stopRecording();
      }, maxRecordingMilliseconds.value);
    } catch (error) {
      releaseMicrophone();
      recorder = null;
      state.value = 'error';
      errorMessage.value = (error as DOMException).name === 'NotAllowedError'
        ? 'Permita o uso do microfone para gravar.'
        : 'Não foi possível iniciar a gravação.';
    }
  }

  function stopRecording(): void {
    if (state.value !== 'recording' || !recorder) return;
    clearDurationTimer();
    state.value = 'transcribing';
    recorder.stop();
  }

  function discard(): void {
    captureSequence += 1;
    errorMessage.value = null;
    notice.value = null;
    if (state.value === 'transcribing') {
      transcriptionController?.abort();
      transcriptionController = null;
      state.value = available.value ? 'idle' : 'unavailable';
      return;
    }

    if (recorder && (state.value === 'recording' || state.value === 'requesting_permission')) {
      discardOnStop = true;
      clearDurationTimer();
      if (recorder.state !== 'inactive') {
        state.value = 'requesting_permission';
        recorder.stop();
        return;
      }
      releaseMicrophone();
    }
    chunks = [];
    state.value = available.value ? 'idle' : 'unavailable';
  }

  async function finishRecording(): Promise<void> {
    const activeRecorder = recorder;
    recorder = null;
    releaseMicrophone();
    if (!activeRecorder || discardOnStop || disposed) {
      chunks = [];
      if (!disposed) state.value = available.value ? 'idle' : 'unavailable';
      return;
    }

    const duration = Math.min(maxRecordingMilliseconds.value, Math.max(1, Date.now() - recordingStartedAt));
    const blob = new Blob(chunks, { type: activeRecorder.mimeType || 'audio/webm' });
    chunks = [];
    if (blob.size === 0) {
      state.value = 'error';
      errorMessage.value = 'A gravação não contém áudio.';
      return;
    }

    const requestId = crypto.randomUUID();
    const controller = new AbortController();
    transcriptionController = controller;
    try {
      const result = await transcribeAudio(
        blob,
        requestId,
        duration,
        fileNameFor(blob.type),
        controller.signal
      );
      if (!controller.signal.aborted && !disposed) {
        onTranscript(result.text);
        state.value = 'idle';
      }
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError') && !controller.signal.aborted && !disposed) {
        state.value = 'error';
        errorMessage.value = 'Não foi possível transcrever a gravação.';
      }
    } finally {
      if (transcriptionController === controller) transcriptionController = null;
    }
  }

  function releaseMicrophone(): void {
    clearDurationTimer();
    mediaStream?.getTracks().forEach((track) => track.stop());
    mediaStream = null;
  }

  function clearDurationTimer(): void {
    if (durationTimer !== null) {
      window.clearTimeout(durationTimer);
      durationTimer = null;
    }
  }

  function dispose(): void {
    disposed = true;
    discard();
    transcriptionController?.abort();
    transcriptionController = null;
    releaseMicrophone();
  }

  onBeforeUnmount(dispose);
  return { state, available, errorMessage, notice, isRecording, isTranscribing, isBusy, refreshStatus, start, stopRecording, discard, dispose };
}

function fileNameFor(mimeType: string): string {
  const type = mimeType.split(';', 1)[0].toLowerCase();
  const extension = type.includes('mp4') ? 'm4a' : type.includes('ogg') ? 'ogg' : type.includes('mpeg') || type.includes('mp3') ? 'mp3' : 'webm';
  return `aegis-voice.${extension}`;
}
