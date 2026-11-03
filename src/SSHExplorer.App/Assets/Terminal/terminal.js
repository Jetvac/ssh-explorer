'use strict';
(() => {
  const post = data => window.chrome.webview.postMessage(data);
  const term = new Terminal({
    fontFamily:'Cascadia Mono, Consolas, monospace', fontSize:14,
    cursorBlink:true, scrollback:8000, allowProposedApi:false,
    theme:{background:'#111820',foreground:'#e4eaf1',cursor:'#60cdff',selectionBackground:'#264f78'},
    linkHandler:{activate:()=>{},hover:()=>{},leave:()=>{}}
  });
  const fit = new FitAddon.FitAddon();
  const search = new SearchAddon.SearchAddon();
  term.loadAddon(fit); term.loadAddon(search);
  // A remote program cannot read or write the Windows clipboard through OSC 52.
  term.parser.registerOscHandler(52, () => true);
  term.open(document.getElementById('terminal'));
  term.onData(data => post({type:'input',data}));
  term.onResize(({cols,rows}) => post({type:'resize',cols,rows}));
  term.attachCustomKeyEventHandler(e => {
    if (e.type !== 'keydown') return true;
    if (e.ctrlKey && e.shiftKey && e.code === 'KeyC') { post({type:'copy',data:term.getSelection()}); return false; }
    if (e.ctrlKey && e.shiftKey && e.code === 'KeyV') { post({type:'paste'}); return false; }
    if (e.ctrlKey && e.shiftKey && e.code === 'KeyF') { post({type:'search'}); return false; }
    if (e.ctrlKey && (e.code==='Equal' || e.code==='NumpadAdd')) { post({type:'font',delta:1}); return false; }
    if (e.ctrlKey && (e.code==='Minus' || e.code==='NumpadSubtract')) { post({type:'font',delta:-1}); return false; }
    return true;
  });
  // All paste goes through the native confirmation and terminal.paste (bracketed paste).
  document.addEventListener('paste', e => { e.preventDefault(); e.stopImmediatePropagation(); post({type:'paste'}); },true);
  let timer;
  new ResizeObserver(() => { clearTimeout(timer); timer=setTimeout(()=>{try{fit.fit();}catch{}},80); }).observe(document.body);
  window.sshExplorer = Object.freeze({
    write(base64,id) { const raw=atob(base64); const b=new Uint8Array(raw.length); for(let i=0;i<raw.length;i++)b[i]=raw.charCodeAt(i); term.write(b,()=>post({type:'outputAck',id})); },
    paste(text) { term.paste(text); },
    search(text,back=false) { return back?search.findPrevious(text):search.findNext(text); },
    font(size) { term.options.fontSize=size; fit.fit(); },
    focus() { term.focus(); },
    fit() { fit.fit(); },
    size() { return {cols:term.cols,rows:term.rows}; }
    ,probeStatus() {
      let text='',red=false;
      const b=term.buffer.active;
      for(let y=0;y<b.length;y++) {
        const line=b.getLine(y); if(!line)continue;
        const value=line.translateToString(true); text+=value+'\n';
        const x=value.indexOf('SSHX_PROBE');
        if(x>=0) { const cell=line.getCell(x); red=red||!!(cell&&cell.isFgPalette()&&cell.getFgColor()===1); }
      }
      return {cols:term.cols,rows:term.rows,marker:text.includes('SSHX_PROBE'),utf8:text.includes('Привет')&&text.includes('😀'),red};
    }
  });
  fit.fit(); term.focus(); post({type:'ready',cols:term.cols,rows:term.rows});
})();
