namespace Cockpit.Plugin.Browser.Host;

internal static class BrowserPageHooks
{
    internal const string MarkerPrefix = "__cockpit:";

    internal const string Script = """
        (()=>{if(window.__cockpitHooks)return;window.__cockpitHooks=1;
        const say=m=>console.log('__cockpit:'+m);
        const audible=()=>{let a=false;document.querySelectorAll('video,audio').forEach(e=>{if(!e.paused&&!e.muted&&e.volume>0)a=true;});say('audible:'+(a?1:0));};
        ['play','pause','ended','volumechange','emptied'].forEach(t=>window.addEventListener(t,audible,true));
        window.addEventListener('error',e=>{const t=e.target;const m=t&&t.tagName==='SOURCE'?t.parentElement:t;if(m&&(m.tagName==='VIDEO'||m.tagName==='AUDIO')&&m.error&&m.error.code===4)say('codec');},true);
        const r=navigator.requestMediaKeySystemAccess;if(r){navigator.requestMediaKeySystemAccess=function(){return r.apply(this,arguments).catch(x=>{say('codec');throw x;});};}
        })();
        """;
}
