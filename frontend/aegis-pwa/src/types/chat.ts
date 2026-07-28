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
  titleSource?: string | null;
  messages: ChatMessage[];
}

export interface ConversationSummary {
  id: string;
  title?: string | null;
  createdAt: string;
  updatedAt: string;
  titleSource?: string | null;
  messageCount: number;
  lastMessagePreview?: string | null;
}

export interface ConversationPage {
  items: ConversationSummary[];
  nextCursor?: string | null;
  hasMore: boolean;
}

export interface SendMessageRequest {
  turnId?: string;
  conversationId?: string | null;
  content: string;
}

export interface SendMessageResponse {
  conversationId: string;
  conversationTitle?: string | null;
  titleSource?: string | null;
  message: ChatMessage;
}

export interface LocalChatMessage extends ChatMessage {
  serverId?: string;
  pending?: boolean;
  streaming?: boolean;
  interrupted?: boolean;
}

export interface ChatStreamHandlers {
  onConversation: (turnId: string, conversationId: string) => void;
  onToken: (turnId: string, content: string) => void;
  onDone: (event: {
    turnId: string;
    conversationId: string;
    messageId: string;
    conversationTitle?: string | null;
    titleSource?: string | null;
  }) => void;
  onError: (message: string) => void;
}

export interface StartSpeechRequest {
  turnId: string;
  speechRequestId: string;
  assistantMessageId: string;
}

export interface VoiceStatus {
  enabled: boolean;
  available: boolean;
  profile: string;
  sampleRate: number;
  channels: number;
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
