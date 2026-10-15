// Windows PowerShell 5 projects the real WinRT history API. Inspect only Aegis's
// generic outbox card; never return reminder text, XML, tokens or other apps.
// https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotificationhistory.gethistory
export const pendingToastHistoryScript = String.raw`
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
$history=[Windows.UI.Notifications.ToastNotificationManager]::History.GetHistory('com.aegis.node')
$cards=@($history | Where-Object { $_.Tag -eq 'reminder-actions-pending' -and $_.Group -eq 'aegis-system' })
if($cards.Count -ne 1) { [pscustomobject]@{count=$cards.Count} | ConvertTo-Json -Compress; exit 0 }
$card=$cards[0]
$document=[xml]$card.Content.GetXml()
$texts=@($document.SelectNodes('/toast/visual/binding/text') | ForEach-Object { $_.InnerText })
$expiration=([DateTimeOffset]$card.ExpirationTime).UtcDateTime.Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
[pscustomobject]@{
  count=1
  expirationTicks=$expiration
  genericTextMatches=($texts.Count -eq 2 -and $texts[0] -ceq 'Aegis' -and $texts[1] -ceq 'Ação aguardando conexão. Adiamentos serão confirmados ao sincronizar.')
  silent=($document.toast.audio.silent -eq 'true')
  opensAppOnly=($document.toast.activationType -eq 'protocol' -and $document.toast.launch -eq 'aegis-reminder://open')
  hasNoActions=($document.SelectNodes('/toast/actions/action').Count -eq 0)
} | ConvertTo-Json -Compress
`;

export function parsePendingToastHistory(stdout) {
  const invalid = () => { throw Error('Invalid bounded Windows pending-notification observation.'); };
  let value;try { value=JSON.parse(stdout); } catch { return invalid(); }
  if(!value || typeof value!=='object' || !Number.isInteger(value.count) || value.count<0 || value.count>1)return invalid();
  if(value.count===0)return {count:0};
  if(typeof value.expirationTicks!=='string' || !/^[1-9][0-9]{15,19}$/.test(value.expirationTicks) ||
    !['genericTextMatches','silent','opensAppOnly','hasNoActions'].every(key=>value[key]===true))return invalid();
  return {count:1,expirationTicks:value.expirationTicks,genericTextMatches:true,silent:true,opensAppOnly:true,hasNoActions:true};
}
