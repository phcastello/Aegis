export type ChatRole = 'user' | 'assistant' | 'system' | string;

export type MemoryActivityKind = 'used' | 'consulted' | 'created' | 'updated' | 'deleted';
export interface MemoryActivitySection {
  kind: MemoryActivityKind;
  items: string[];
  totalCount: number;
}
export interface MemoryActivitySnapshot {
  pending: boolean;
  sections: MemoryActivitySection[];
}

export interface WebResearchSource {
  url: string;
  title?: string | null;
  kind: 'consulted' | 'opened' | 'citation';
  cited?: boolean;
}

export interface WebResearchCitation {
  url: string;
  title?: string | null;
  startIndex: number;
  endIndex: number;
}

export interface WebResearch {
  sources: WebResearchSource[];
  citations: WebResearchCitation[];
}

export interface WebResearchSourceCount {
  url: string;
  count: number;
}

export interface ChatAttachment {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
}

export interface ChatMessage {
  id: string;
  conversationId: string;
  role: ChatRole;
  content: string;
  attachments?: ChatAttachment[];
  createdAt: string;
  model?: string | null;
  webResearch?: WebResearch | null;
  memoryActivity?: MemoryActivitySnapshot | null;
  isProactive?: boolean;
  proactiveSequence?: number | null;
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
  hasUnreadProactive?: boolean;
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
  attachmentUploadIds?: string[];
}

export interface SendMessageResponse {
  conversationId: string;
  conversationTitle?: string | null;
  titleSource?: string | null;
  message: ChatMessage;
}

export interface LocalChatMessage extends ChatMessage {
  serverId?: string;
  memoryActivity?: MemoryActivitySnapshot | null;
  isProactive?: boolean;
  proactiveSequence?: number | null;
  pending?: boolean;
  streaming?: boolean;
  interrupted?: boolean;
}

export interface ChatStreamHandlers {
  onConversation: (turnId: string, conversationId: string) => void;
  onToken: (turnId: string, content: string) => void;
  onToolStatus?: (turnId: string, status: { category: string; state: 'started' | 'completed' | 'failed'; message: string }) => void;
  onWebResearchSources?: (turnId: string, sources: WebResearchSourceCount[]) => void;
  onDone: (event: {
    turnId: string;
    conversationId: string;
    messageId: string;
    conversationTitle?: string | null;
    titleSource?: string | null;
    memoryActivity?: MemoryActivitySnapshot | null;
    webResearch?: WebResearch | null;
  isProactive?: boolean;
  proactiveSequence?: number | null;
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

export interface TranscriptionStatus {
  enabled: boolean;
  configured: boolean;
  maxRecordingSeconds: number;
}

export interface TranscriptionResponse {
  transcriptionRequestId: string;
  text: string;
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
