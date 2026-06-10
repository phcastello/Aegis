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
  serverId?: string;
  pending?: boolean;
  streaming?: boolean;
}

export interface ChatStreamHandlers {
  onConversation: (conversationId: string) => void;
  onToken: (content: string) => void;
  onDone: (event: { conversationId: string; messageId: string }) => void;
  onError: (message: string) => void;
}

export type FeedbackRating = 'good' | 'bad';

export type FeedbackReason =
  | 'good_tone'
  | 'useful'
  | 'clear'
  | 'concrete'
  | 'good_criticism'
  | 'respected_constraint'
  | 'bad_tone'
  | 'not_useful'
  | 'too_verbose'
  | 'too_generic'
  | 'ignored_constraint'
  | 'hallucinated_capability'
  | 'repeated_topic'
  | 'did_not_answer'
  | 'wrong_context'
  | 'other';

export interface SubmitMessageFeedbackRequest {
  rating: FeedbackRating;
  reason?: FeedbackReason | null;
  comment?: string | null;
  correctedAnswer?: string | null;
}

export interface MessageFeedbackResponse {
  id: string;
  conversationId: string;
  messageId: string;
  rating: FeedbackRating;
  reason?: FeedbackReason | null;
  comment?: string | null;
  correctedAnswer?: string | null;
  createdAt: string;
}
