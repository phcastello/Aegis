import type {
  ChatStreamHandlers,
  Conversation,
  ConversationPage,
  ConversationSummary,
  MessageFeedbackResponse,
  SendMessageRequest,
  SendMessageResponse,
  StartSpeechRequest,
  SubmitMessageFeedbackRequest,
  VoiceStatus
} from '../types/chat';

const configuredBaseUrl = import.meta.env.VITE_AEGIS_API_BASE_URL ?? '';

function getApiBaseUrl(): string {
  return configuredBaseUrl.trim().replace(/\/+$/, '');
}

async function requestJson<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${getApiBaseUrl()}${path}`, {
    ...options,
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...options?.headers
    }
  });

  if (!response.ok) {
    let message = 'Não foi possível conectar com a Aegis.';

    try {
      const errorBody = (await response.json()) as { error?: string };
      message = errorBody.error ?? message;
    } catch {
      message = response.statusText || message;
    }

    throw new Error(message);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export async function sendMessage(request: SendMessageRequest): Promise<SendMessageResponse> {
  return requestJson<SendMessageResponse>('/api/chat/messages', {
    method: 'POST',
    body: JSON.stringify(request)
  });
}

export async function sendMessageStream(
  request: SendMessageRequest,
  handlers: ChatStreamHandlers,
  signal?: AbortSignal
): Promise<void> {
  const response = await fetch(`${getApiBaseUrl()}/api/chat/messages/stream`, {
    method: 'POST',
    headers: {
      Accept: 'application/x-ndjson',
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(request),
    signal
  });

  if (!response.ok || !response.body) {
    let message = 'Não foi possível iniciar a resposta da Aegis.';

    try {
      const errorBody = (await response.json()) as { error?: string; message?: string };
      message = errorBody.error ?? errorBody.message ?? message;
    } catch {
      message = response.statusText || message;
    }

    throw new Error(message);
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let receivedDone = false;

  const processLine = (line: string): void => {
    if (!line.trim()) {
      return;
    }

    const event = JSON.parse(line) as {
      type?: string;
      conversationId?: string;
      turnId?: string;
      content?: string;
      messageId?: string;
      assistantMessageId?: string;
      message?: string;
      conversationTitle?: string | null;
      titleSource?: string | null;
    };

    switch (event.type) {
      case 'conversation':
        if (event.turnId && event.conversationId) {
          handlers.onConversation(event.turnId, event.conversationId);
        }
        break;
      case 'token':
        if (event.turnId && event.content) {
          handlers.onToken(event.turnId, event.content);
        }
        break;
      case 'done':
        if (event.turnId && event.conversationId && (event.assistantMessageId || event.messageId)) {
          receivedDone = true;
          handlers.onDone({
            turnId: event.turnId,
            conversationId: event.conversationId,
            messageId: event.assistantMessageId || event.messageId!,
            conversationTitle: event.conversationTitle,
            titleSource: event.titleSource
          });
        }
        break;
      case 'error':
        {
          const message = event.message || 'A resposta da Aegis foi interrompida.';
          handlers.onError(message);
          throw new Error(message);
        }
    }
  };

  while (true) {
    const { value, done } = await reader.read();
    buffer += decoder.decode(value, { stream: !done });

    const lines = buffer.split('\n');
    buffer = lines.pop() ?? '';

    for (const line of lines) {
      processLine(line);
    }

    if (done) {
      break;
    }
  }

  processLine(buffer);

  if (!receivedDone) {
    throw new Error('A resposta da Aegis terminou antes da confirmação final.');
  }
}

export async function cancelTurn(turnId: string): Promise<void> {
  await requestJson<void>(`/api/chat/turns/${encodeURIComponent(turnId)}`, { method: 'DELETE' });
}

export async function getHealth(signal?: AbortSignal): Promise<boolean> {
  const response = await fetch(`${getApiBaseUrl()}/api/health`, { cache: 'no-store', signal });
  if (!response.ok) return false;
  try { return ((await response.json()) as { status?: string }).status === 'ok'; } catch { return false; }
}

export async function completeTurnWithoutSpeech(turnId: string): Promise<void> {
  await requestJson<void>(`/api/chat/turns/${encodeURIComponent(turnId)}/complete`, { method: 'POST' });
}

export async function streamSpeech(
  request: StartSpeechRequest,
  onChunk: (chunk: Uint8Array) => Promise<void>,
  signal?: AbortSignal
): Promise<void> {
  const response = await fetch(`${getApiBaseUrl()}/api/voice/speech`, {
    method: 'POST',
    headers: { Accept: 'application/octet-stream', 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal
  });
  if (!response.ok || !response.body) throw new Error('Voice is unavailable.');
  if (response.headers.get('X-Aegis-Audio-Format') !== 'pcm_s16le' ||
      response.headers.get('X-Aegis-Sample-Rate') !== '24000' ||
      response.headers.get('X-Aegis-Channels') !== '1') throw new Error('Invalid audio format.');
  const reader = response.body.getReader();
  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      if (value) await onChunk(value);
    }
  } finally { reader.releaseLock(); }
}

export async function cancelSpeech(speechRequestId: string): Promise<void> {
  await requestJson<void>(`/api/voice/speech/${encodeURIComponent(speechRequestId)}`, { method: 'DELETE' });
}

export function getVoiceStatus(): Promise<VoiceStatus> {
  return requestJson<VoiceStatus>('/api/voice/status');
}

export async function getConversation(conversationId: string): Promise<Conversation> {
  return requestJson<Conversation>(`/api/chat/conversations/${conversationId}`);
}

export async function getConversations(limit = 30, cursor?: string | null): Promise<ConversationPage> {
  const params = new URLSearchParams({ limit: String(limit) });
  if (cursor) {
    params.set('cursor', cursor);
  }

  return requestJson<ConversationPage>(`/api/chat/conversations?${params.toString()}`);
}

export async function renameConversation(
  conversationId: string,
  title: string
): Promise<ConversationSummary> {
  return requestJson<ConversationSummary>(`/api/chat/conversations/${conversationId}/title`, {
    method: 'PATCH',
    body: JSON.stringify({ title })
  });
}

export async function deleteConversation(conversationId: string): Promise<void> {
  await requestJson<void>(`/api/chat/conversations/${conversationId}`, {
    method: 'DELETE'
  });
}

export async function submitMessageFeedback(
  messageId: string,
  request: SubmitMessageFeedbackRequest
): Promise<MessageFeedbackResponse> {
  return requestJson<MessageFeedbackResponse>(`/api/chat/messages/${messageId}/feedback`, {
    method: 'POST',
    body: JSON.stringify(request)
  });
}
