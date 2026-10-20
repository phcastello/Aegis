// API 26 Settings.Secure maps mode changes to provider-list updates in its Java
// wrapper. The settings shell command bypasses that wrapper.
const setting = (run, name) => run(['shell','settings','get','secure',name]).replace(/\r?\n$/, '');
const quoted = value => `'${value.replaceAll("'", "'\\''")}'`;
const put = (run, name, value) => run(['shell','settings','put','secure',name,quoted(value)]);
export function captureLegacyLocation(run) {
  return {mode:setting(run,'location_mode'),providers:setting(run,'location_providers_allowed')};
}
export function enableLegacyLocation(run, state=captureLegacyLocation(run)) {
  // Pre-P's provider API cannot restore a missing key: deletion and plain puts
  // are ignored. Refuse before changing anything instead of claiming cleanup.
  if(state.providers==='null')throw Error('legacy_provider_setting_missing');
  put(run,'location_mode','3');
  // Match AOSP's NLP-first ordering, preserving the Google NLP consent setting.
  put(run,'location_providers_allowed','+network');
  put(run,'location_providers_allowed','+gps');
}
export function restoreLegacyLocation(run, state) {
  if(state.mode==='null')run(['shell','settings','delete','secure','location_mode']);
  else put(run,'location_mode',state.mode);
  if(state.providers!=='null') {
    // Literal provider-list puts are ignored by SettingsProvider26. Undo only
    // our GPS/NLP toggles, retaining all original providers and list ordering.
    const original=new Set(state.providers.split(','));
    if(original.has('network'))put(run,'location_providers_allowed','+network');
    put(run,'location_providers_allowed',original.has('gps')?'+gps':'-gps');
    if(!original.has('network'))put(run,'location_providers_allowed','-network');
  }
  if(setting(run,'location_providers_allowed')!==state.providers)throw Error('legacy_provider_restore_failed');
}
export function safeLegacyLocation(run) {
  const state=captureLegacyLocation(run),allowed=new Set(state.providers.split(','));
  return {mode:/^[0-3]$/.test(state.mode)?Number(state.mode):'unknown',gpsAllowed:allowed.has('gps'),networkAllowed:allowed.has('network')};
}
