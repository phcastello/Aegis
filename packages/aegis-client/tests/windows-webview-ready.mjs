// CDP can publish a WebView target before its initial document navigation ends.
// Retry only that known navigation race, with one deadline and a strict cap.
export async function reloadReadyWindowsPage(getPage,isAlive,{timeoutMs=45000,maxAttempts=3,now=Date.now}={}) {
  const deadline=now()+timeoutMs;
  function remaining() { const value=deadline-now();if(value<=0)throw Error('Windows WebView document readiness timed out.');return value; }
  for(let attempt=0;attempt<maxAttempts;attempt++) {
    if(!isAlive())throw Error('Installed runtime exited during document readiness.');
    try {
      const page=await getPage(remaining());
      await page.waitForFunction(()=>typeof window.__TAURI_INTERNALS__?.invoke==='function',undefined,{timeout:remaining()});
      await page.getByRole('region',{name:'Conversa com a Aegis'}).waitFor({timeout:remaining()});
      await page.reload({waitUntil:'domcontentloaded',timeout:remaining()});
      await page.getByRole('region',{name:'Conversa com a Aegis'}).waitFor({timeout:remaining()});
      return page;
    } catch(failure) {
      if(!isAlive())throw Error('Installed runtime exited during document readiness.');
      if(attempt===maxAttempts-1 || now()>=deadline || !/net::ERR_ABORTED|frame (?:was |has been )?detached/i.test(String(failure?.message??'')))throw failure;
    }
  }
  throw Error('Windows WebView document readiness timed out.');
}
