export type ChatRole = 'user' | 'assistant' | 'system' | string;

export interface ChatMessage {
  id: string;
  conversationId: string;
  role: ChatRole;
  content: string;
  createdAt: string;
  model?: string | null;
}

export interface Conversation {
  id: string;
  title?: string | null;
  createdAt: string;
  updatedAt: string;
  messages: ChatMessage[];
}

export interface SendMessageRequest {
  conversationId?: string | null;
  content: string;
}

export interface SendMessageResponse {
  conversationId: string;
  message: ChatMessage;
}

export interface LocalChatMessage extends ChatMessage {
  pending?: boolean;
}
