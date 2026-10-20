// Process.MainWindowHandle can select Tauri's technically visible, layered IPC
// HWND after the product window hides. Enumerate the exact owned product window.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows
export function ownedWindowInspectionScript(processId, operation='inspect') {
  if(!Number.isSafeInteger(processId) || processId<1 || processId>0xffffffff)throw Error('Invalid owned Windows process identifier.');
  if(!['inspect','requestClose'].includes(operation))throw Error('Invalid owned Windows window operation.');
  return String.raw`
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class AegisFixtureWindow {
  public delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
  public static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder title, int capacity);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr window);
  [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
  public static IntPtr[] Find(uint processId) {
    var matches=new List<IntPtr>();
    if(!EnumWindows((window, parameter)=>{
      uint owner; GetWindowThreadProcessId(window,out owner);
      if(owner==processId) {
        var title=new StringBuilder(512); GetWindowText(window,title,title.Capacity);
        if(String.Equals(title.ToString(),"Aegis",StringComparison.Ordinal)) matches.Add(window);
      }
      return true;
    },IntPtr.Zero)) throw new InvalidOperationException("Window enumeration failed.");
    return matches.ToArray();
  }
}
'@
$windows=@([AegisFixtureWindow]::Find([uint32]${processId}))
if($windows.Count -ne 1) { [pscustomobject]@{windowCount=$windows.Count} | ConvertTo-Json -Compress; exit 0 }
${operation==='requestClose'?"if(-not [AegisFixtureWindow]::PostMessage($windows[0],0x0010,[IntPtr]::Zero,[IntPtr]::Zero)) { throw 'Owned window close request failed.' }":''}
[pscustomobject]@{windowCount=1;visible=[AegisFixtureWindow]::IsWindowVisible($windows[0]);mainWindowTitle='Aegis'} | ConvertTo-Json -Compress
`;
}

export function parseOwnedWindowState(stdout) {
  const invalid=()=>{throw Error('Invalid owned Windows window observation.');};
  let value;try{value=JSON.parse(stdout);}catch{return invalid();}
  if(!value || typeof value!=='object' || !Number.isInteger(value.windowCount) || value.windowCount<0 || value.windowCount>16)return invalid();
  if(value.windowCount!==1)return {windowCount:value.windowCount};
  if(typeof value.visible!=='boolean' || value.mainWindowTitle!=='Aegis')return invalid();
  return {windowCount:1,visible:value.visible,mainWindowTitle:'Aegis'};
}
