export type UpdatePhase = 'discovery' | 'download' | 'verification' | 'install_permission' | 'installer_launch';

const messages: Record<string, [UpdatePhase, string]> = {
  update_feed_missing: ['discovery', 'O manifesto de atualizações não está disponível. Tente novamente mais tarde.'],
  update_feed_unavailable: ['discovery', 'Não foi possível consultar as atualizações. Confira a conexão e tente novamente.'],
  update_timeout: ['discovery', 'A consulta de atualizações demorou demais. Tente novamente.'],
  update_metadata_invalid: ['discovery', 'O manifesto retornou informações inválidas. Nenhum arquivo foi instalado.'],
  update_asset_mismatch: ['discovery', 'Os dados do instalador não correspondem à versão anunciada.'],
  update_download_failed: ['download', 'Não foi possível baixar o instalador. Confira a conexão e tente novamente.'],
  update_archive_invalid: ['verification', 'O arquivo baixado não pôde ser validado. Tente baixar novamente.'],
  update_package_mismatch: ['verification', 'O instalador não corresponde a este aplicativo.'],
  update_version_mismatch: ['verification', 'O instalador não corresponde à versão anunciada.'],
  update_signature_mismatch: ['verification', 'A assinatura do instalador não corresponde à Aegis instalada.'],
  update_permission_failed: ['install_permission', 'Não foi possível abrir a permissão de instalação do Android.'],
  update_installer_failed: ['installer_launch', 'Não foi possível abrir o instalador do sistema.'],
};

export function updateFailure(error: unknown, fallback: UpdatePhase) {
  const candidate = typeof error === 'string' ? error : error instanceof Error ? error.message : '';
  const known = Object.prototype.hasOwnProperty.call(messages, candidate) ? messages[candidate] : undefined;
  if (known) return { code: candidate, phase: known[0], message: known[1] };
  return { code: fallback === 'discovery' ? 'update_check_failed' : 'update_install_failed', phase: fallback,
    message: fallback === 'discovery' ? 'Não foi possível verificar atualizações. Confira a conexão e tente novamente.' : 'Não foi possível atualizar. Tente novamente.' };
}
