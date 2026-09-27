export function emailConnectionFailureMessage(code: string | null): string | null {
  if (!code) return null;
  switch (code) {
    case 'authorization_cancelled': return 'Autorização Google cancelada.';
    case 'google_account_mismatch': return 'Autorize com a mesma conta Google já conectada.';
    case 'google_rejected': return 'O Google rejeitou a autorização solicitada.';
    case 'oauth_configuration_invalid': return 'A configuração OAuth Google está inválida.';
    case 'oauth_state_invalid': return 'A conexão Google expirou ou não pôde ser validada. Tente novamente.';
    case 'connection_unconfirmed': return 'O Google não confirmou a conta ou a autorização. Tente conectar novamente.';
    case 'oauth_temporary_error': return 'Erro temporário ao concluir a conexão Google. Tente novamente.';
    default: return 'Falha ao conectar a conta Google.';
  }
}

export function emailConnectionSuccessMessage(address: string | null): string {
  return address ? `Conta Google conectada como ${address}.` : 'Conta Google conectada.';
}
