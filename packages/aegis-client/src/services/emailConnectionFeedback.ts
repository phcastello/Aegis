export function emailConnectionSuccessMessage(address: string | null): string {
  return address ? `Conta Google conectada como ${address}.` : 'Conta Google conectada.';
}
