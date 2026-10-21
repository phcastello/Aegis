import { computed, onBeforeUnmount, ref } from 'vue';
import { getTranscriptionStatus, transcribeAudio } from '../services/aegisApi';
import { DIGITAL_ZERO_FLOOR, classifyPcmSignal, getPreferredVoiceInput, setPreferredVoiceInput } from '../services/voiceInput';

export type TranscriptionState = 'idle' | 'requesting_permission' | 'recording' | 'transcribing' | 'unavailable' | 'error';
export type CaptureDiagnostic = 'permission_denied' | 'no_device' | 'device_unavailable' | 'track_ended' | 'recording_failed' | 'empty_audio' | 'digital_silence' | 'upload_failed' | 'transcription_failed' | null;

const DEFAULT_MAX_RECORDING_MILLISECONDS = 90_000;
const MIME_CANDIDATES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4;codecs=mp4a.40.2', 'audio/mp4'];

type CaptureSession = {
  valid: boolean;
  stream: MediaStream | null;
  recorder: MediaRecorder | null;
  chunks: BlobPart[];
  startedAt: number;
  durationTimer: number | null;
  meterTimer: number | null;
  stopTimer: number | null;
  controller: AbortController | null;
  context: AudioContext | null;
  source: MediaStreamAudioSourceNode | null;
  analyser: AnalyserNode | null;
  removeTrackListeners: (() => void)[];
  finishing: boolean;
  sawSignal: boolean;
};

export function useAegisTranscription(onTranscript: (text: string) => void) {
  const state = ref<TranscriptionState>('idle');
  const available = ref(false);
  const errorMessage = ref<string | null>(null);
  const notice = ref<string | null>(null);
  const diagnostic = ref<CaptureDiagnostic>(null);
  const inputLevel = ref(0);
  const inputStatus = ref('');
  const maxRecordingMilliseconds = ref(DEFAULT_MAX_RECORDING_MILLISECONDS);
  let current: CaptureSession | null = null;
  let disposed = false;
  const isRecording = computed(() => state.value === 'recording');
  const isTranscribing = computed(() => state.value === 'transcribing');
  const isBusy = computed(() => isRecording.value || isTranscribing.value || state.value === 'requesting_permission');
  const owns = (session: CaptureSession): boolean => !disposed && session.valid && current === session;

  async function refreshStatus(): Promise<void> {
    try {
      const status = await getTranscriptionStatus();
      if (disposed) return;
      if (Number.isFinite(status.maxRecordingSeconds) && status.maxRecordingSeconds > 0) {
        maxRecordingMilliseconds.value = Math.floor(status.maxRecordingSeconds * 1000);
      }
      available.value = status.enabled && status.configured;
      if (!isBusy.value) state.value = available.value ? 'idle' : 'unavailable';
    } catch {
      if (disposed) return;
      available.value = false;
      if (!isBusy.value) state.value = 'unavailable';
    }
  }

  function clearTimer(session: CaptureSession, key: 'durationTimer' | 'meterTimer' | 'stopTimer'): void {
    if (session[key] !== null) window.clearTimeout(session[key]!);
    session[key] = null;
  }

  function releaseCapture(session: CaptureSession, preserveStopTimer = false): void {
    clearTimer(session, 'durationTimer');
    clearTimer(session, 'meterTimer');
    if (!preserveStopTimer) clearTimer(session, 'stopTimer');
    session.removeTrackListeners.splice(0).forEach(remove => remove());
    session.source?.disconnect();
    session.analyser?.disconnect();
    session.source = null;
    session.analyser = null;
    const context = session.context;
    session.context = null;
    if (context) void context.close().catch(() => undefined);
    session.stream?.getTracks().forEach(track => track.stop());
    session.stream = null;
    if (current === session) { inputLevel.value = 0; inputStatus.value = ''; }
  }

  function invalidate(session: CaptureSession): void {
    session.valid = false; // Before stop(): even a synchronous/repeated callback must be harmless.
    session.controller?.abort();
    session.controller = null;
    const recorder = session.recorder;
    session.recorder = null;
    if (recorder) {
      recorder.ondataavailable = null;
      recorder.onstop = null;
      recorder.onerror = null;
      try { if (recorder.state !== 'inactive') recorder.stop(); } catch { /* Release tracks below even when stop fails. */ }
    }
    session.chunks = [];
    releaseCapture(session);
    if (current === session) current = null;
  }

  function fail(session: CaptureSession, code: CaptureDiagnostic, message: string): void {
    if (!owns(session)) { invalidate(session); return; }
    invalidate(session);
    state.value = 'error';
    diagnostic.value = code;
    errorMessage.value = message;
  }

  function monitor(session: CaptureSession): void {
    try {
      const context = new AudioContext();
      session.context = context;
      const source = context.createMediaStreamSource(session.stream!);
      const analyser = context.createAnalyser();
      session.source = source;
      session.analyser = analyser;
      analyser.fftSize = 2048;
      source.connect(analyser); // Never connect the microphone to the speakers.
      const samples = new Float32Array(analyser.fftSize);
      const tick = (): void => {
        if (!owns(session) || state.value !== 'recording') return;
        if (context.state === 'running') {
          analyser.getFloatTimeDomainData(samples);
          let squares = 0;
          let peak = 0;
          for (const sample of samples) { squares += sample * sample; peak = Math.max(peak, Math.abs(sample)); }
          if (peak > DIGITAL_ZERO_FLOOR) session.sawSignal = true;
          inputLevel.value = Math.min(1, Math.sqrt(squares / samples.length) * 8);
          const muted = session.stream?.getAudioTracks().some(track => track.muted);
          inputStatus.value = muted ? 'A entrada está temporariamente sem dados. Verifique o dispositivo e o sistema.'
            : peak > DIGITAL_ZERO_FLOOR ? 'Sinal recebido do microfone.'
              : Date.now() - session.startedAt >= 2000 ? 'Sem sinal detectado. Confira a entrada em Configurações → Sobre → Microfone e as permissões do sistema.' : 'Aguardando sinal do microfone…';
        } else {
          inputLevel.value = 0;
          inputStatus.value = 'Medição de entrada indisponível; confira as permissões do sistema.';
        }
        session.meterTimer = window.setTimeout(tick, 80);
      };
      void context.resume().catch(() => undefined);
      tick();
    } catch {
      inputStatus.value = 'Medição de entrada indisponível nesta plataforma.';
    }
  }

  async function start(): Promise<void> {
    if (disposed || isBusy.value) return;
    if (!available.value) { state.value = 'unavailable'; return; }
    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') {
      available.value = false;
      state.value = 'unavailable';
      errorMessage.value = 'Este navegador não oferece gravação de voz.';
      return;
    }
    const session: CaptureSession = {
      valid: true, stream: null, recorder: null, chunks: [], startedAt: 0,
      durationTimer: null, meterTimer: null, stopTimer: null, controller: null,
      context: null, source: null, analyser: null, removeTrackListeners: [], finishing: false, sawSignal: false
    };
    current = session;
    state.value = 'requesting_permission';
    errorMessage.value = null;
    notice.value = null;
    diagnostic.value = null;
    try {
      const preferred = getPreferredVoiceInput();
      let stream: MediaStream;
      try {
        stream = await navigator.mediaDevices.getUserMedia({ audio: preferred ? { deviceId: { exact: preferred } } : true });
      } catch (error) {
        if (!owns(session)) return;
        const name = (error as DOMException).name;
        if (!preferred || (name !== 'NotFoundError' && name !== 'OverconstrainedError')) throw error;
        setPreferredVoiceInput('');
        notice.value = 'O microfone salvo não está disponível. Usando a entrada padrão; confira a seleção em Configurações → Sobre → Microfone.';
        diagnostic.value = 'device_unavailable';
        stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      }
      session.stream = stream;
      if (!owns(session)) { invalidate(session); return; }
      const tracks = stream.getAudioTracks();
      if (!tracks.length || tracks.some(track => track.readyState === 'ended')) {
        fail(session, 'no_device', 'Nenhum microfone ativo foi encontrado. Confira a entrada e as permissões do sistema.');
        return;
      }
      for (const track of tracks) {
        const ended = (): void => fail(session, 'track_ended', 'O microfone foi desconectado ou encerrado pelo sistema. Selecione uma entrada e grave novamente.');
        track.addEventListener('ended', ended);
        session.removeTrackListeners.push(() => track.removeEventListener('ended', ended));
      }
      const mimeType = MIME_CANDIDATES.find(candidate => MediaRecorder.isTypeSupported(candidate));
      const recorder = mimeType ? new MediaRecorder(stream, { mimeType }) : new MediaRecorder(stream);
      session.recorder = recorder;
      recorder.ondataavailable = event => {
        if (owns(session) && !session.finishing && event.data.size > 0) session.chunks.push(event.data);
      };
      recorder.onstop = () => { void finishRecording(session, recorder); };
      recorder.onerror = () => fail(session, 'recording_failed', 'Falha ao gravar o áudio. Confira a entrada e tente novamente.');
      recorder.start();
      session.startedAt = Date.now();
      state.value = 'recording';
      monitor(session);
      session.durationTimer = window.setTimeout(() => {
        if (!owns(session) || state.value !== 'recording') return;
        notice.value = `Limite de ${Math.ceil(maxRecordingMilliseconds.value / 1000)} segundos atingido.`;
        stopRecording();
      }, maxRecordingMilliseconds.value);
    } catch (error) {
      if (!owns(session)) { invalidate(session); return; }
      const name = (error as DOMException).name;
      fail(session, name === 'NotAllowedError' || name === 'SecurityError' ? 'permission_denied' : name === 'NotFoundError' ? 'no_device' : 'recording_failed',
        name === 'NotAllowedError' || name === 'SecurityError' ? 'Permita o uso do microfone no aplicativo/navegador e nas configurações de privacidade do sistema.'
          : name === 'NotFoundError' ? 'Nenhum microfone foi encontrado. Conecte uma entrada e tente novamente.'
            : 'Não foi possível iniciar a gravação. Confira a entrada, se outro aplicativo a está usando e as permissões do sistema.');
    }
  }

  function stopRecording(): void {
    const session = current;
    if (!session || !owns(session) || state.value !== 'recording' || !session.recorder) return;
    clearTimer(session, 'durationTimer');
    clearTimer(session, 'meterTimer');
    state.value = 'transcribing';
    try {
      // A missing onstop must not leave a live microphone or a busy composer forever.
      session.stopTimer = window.setTimeout(() => fail(session, 'recording_failed', 'A gravação não foi encerrada corretamente. Tente novamente.'), 5000);
      session.recorder.stop();
      releaseCapture(session, true);
    } catch { fail(session, 'recording_failed', 'Não foi possível encerrar a gravação. Tente novamente.'); }
  }

  function discard(): void {
    if (current) invalidate(current);
    if (disposed) return;
    errorMessage.value = null;
    notice.value = null;
    diagnostic.value = null;
    state.value = available.value ? 'idle' : 'unavailable';
  }

  async function finishRecording(session: CaptureSession, recorder: MediaRecorder): Promise<void> {
    if (!owns(session) || session.finishing) return;
    session.finishing = true;
    state.value = 'transcribing';
    const duration = Math.min(maxRecordingMilliseconds.value, Math.max(1, Date.now() - session.startedAt));
    const blob = new Blob(session.chunks, { type: recorder.mimeType || 'audio/webm' });
    session.chunks = [];
    releaseCapture(session);
    if (blob.size === 0) { fail(session, 'empty_audio', 'A gravação não contém áudio. Confira a entrada e grave novamente.'); return; }

    // Inspect the entire decoded recording. Sparse meter samples alone cannot
    // prove silence: a short/quiet word may occur between two meter ticks.
    let context: AudioContext | null = null;
    try {
      context = new AudioContext();
      session.context = context;
      const decoded = await context.decodeAudioData(await blob.arrayBuffer());
      if (!owns(session)) return;
      const channels = Array.from({ length: decoded.numberOfChannels }, (_, index) => decoded.getChannelData(index));
      if (classifyPcmSignal(channels) === 'silent' && !session.sawSignal) {
        fail(session, 'digital_silence', 'A gravação contém apenas silêncio digital. Confira a entrada em Configurações → Sobre → Microfone, as permissões e o dispositivo.');
        return;
      }
    } catch {
      if (owns(session)) notice.value = 'Não foi possível verificar todo o sinal nesta plataforma. A ausência de fala não pode ser confirmada.';
    } finally {
      if (session.context === context) session.context = null;
      if (context && context.state !== 'closed') void context.close().catch(() => undefined);
    }
    if (!owns(session)) return;
    const controller = new AbortController();
    session.controller = controller;
    try {
      const result = await transcribeAudio(blob, crypto.randomUUID(), duration, fileNameFor(blob.type), controller.signal);
      if (!owns(session) || controller.signal.aborted) return;
      invalidate(session);
      state.value = available.value ? 'idle' : 'unavailable';
      onTranscript(result.text);
    } catch (error) {
      if (!owns(session) || controller.signal.aborted) return;
      fail(session, error instanceof TypeError ? 'upload_failed' : 'transcription_failed',
        error instanceof TypeError ? 'Servidor de transcrição indisponível. Confira sua conexão.'
          : error instanceof Error ? error.message : 'Não foi possível transcrever a gravação.');
    } finally {
      if (session.controller === controller) session.controller = null;
    }
  }

  const onVisibilityChange = (): void => {
    if (document.visibilityState === 'hidden' && isBusy.value) discard();
  };
  document.addEventListener('visibilitychange', onVisibilityChange);
  function dispose(): void {
    disposed = true;
    document.removeEventListener('visibilitychange', onVisibilityChange);
    if (current) invalidate(current);
  }
  onBeforeUnmount(dispose);
  return { state, available, errorMessage, notice, diagnostic, inputLevel, inputStatus, isRecording, isTranscribing, isBusy, refreshStatus, start, stopRecording, discard, dispose };
}

function fileNameFor(mimeType: string): string {
  const type = mimeType.split(';', 1)[0].toLowerCase();
  const extension = type.includes('mp4') ? 'm4a' : type.includes('ogg') ? 'ogg' : type.includes('mpeg') || type.includes('mp3') ? 'mp3' : 'webm';
  return `aegis-voice.${extension}`;
}
