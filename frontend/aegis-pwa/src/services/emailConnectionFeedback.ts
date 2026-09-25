export function emailConnectionFailureMessage(code: string | null): string | null {
  if (!code) return null;
  switch (code) {
    case 'authorization_cancelled': return 'Autorização do Gmail cancelada.';
    case 'google_rejected': return 'O Google rejeitou a autorização do Gmail.';
    case 'oauth_configuration_invalid': return 'A configuração OAuth do Gmail está inválida.';
    case 'oauth_state_invalid': return 'A conexão Gmail expirou ou não pôde ser validada. Tente novamente.';
    case 'connection_unconfirmed': return 'O Gmail não confirmou a conta ou a autorização. Tente conectar novamente.';
    case 'oauth_temporary_error': return 'Erro temporário ao concluir a conexão Gmail. Tente novamente.';
    default: return 'Falha ao conectar o Gmail.';
  }
}

export function emailConnectionSuccessMessage(address: string | null): string {
  return address ? `Gmail conectado como ${address}.` : 'Gmail conectado.';
}
