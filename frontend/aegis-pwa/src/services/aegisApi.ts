import type { Conversation, SendMessageRequest, SendMessageResponse } from '../types/chat';

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
    let message = 'Nao foi possivel conectar com a Aegis.';

    try {
      const errorBody = (await response.json()) as { error?: string };
      message = errorBody.error ?? message;
    } catch {
      message = response.statusText || message;
    }

    throw new Error(message);
  }

  return (await response.json()) as T;
}

export async function sendMessage(request: SendMessageRequest): Promise<SendMessageResponse> {
  return requestJson<SendMessageResponse>('/api/chat/messages', {
    method: 'POST',
    body: JSON.stringify(request)
  });
}

export async function getConversation(conversationId: string): Promise<Conversation> {
  return requestJson<Conversation>(`/api/chat/conversations/${conversationId}`);
}
