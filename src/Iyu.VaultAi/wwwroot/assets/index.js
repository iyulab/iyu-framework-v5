const __vite__mapDeps=(i,m=__vite__mapDeps,d=(m.f||(m.f=["assets/SidebarLayout.js","assets/lit.js","assets/highlight.js","assets/marked.js","assets/katex.js"])))=>i.map(i=>d[i]);
import{f as $r,l as xe,b as y,i as S,n as h,D as xo,t as E,m as Br,A as M,o as rt,r as z,p as Qo,j as Nr,d as Oe,e as it,g as ko,k as Yt,s as We,h as $o,c as Y,a as Zo,q as es,u as _o}from"./lit.js";import{H as Ur}from"./highlight.js";import{D as ts,b as zr}from"./marked.js";import{m as rs}from"./katex.js";(function(){const e=document.createElement("link").relList;if(e&&e.supports&&e.supports("modulepreload"))return;for(const s of document.querySelectorAll('link[rel="modulepreload"]'))o(s);new MutationObserver(s=>{for(const n of s)if(n.type==="childList")for(const i of n.addedNodes)i.tagName==="LINK"&&i.rel==="modulepreload"&&o(i)}).observe(document,{childList:!0,subtree:!0});function r(s){const n={};return s.integrity&&(n.integrity=s.integrity),s.referrerPolicy&&(n.referrerPolicy=s.referrerPolicy),s.crossOrigin==="use-credentials"?n.credentials="include":s.crossOrigin==="anonymous"?n.credentials="omit":n.credentials="same-origin",n}function o(s){if(s.ep)return;s.ep=!0;const n=r(s);fetch(s.href,n)}})();const os="modulepreload",ss=function(t){return"/vault-ai-reports/"+t},Ir={},_r=function(e,r,o){let s=Promise.resolve();if(r&&r.length>0){document.getElementsByTagName("link");const i=document.querySelector("meta[property=csp-nonce]"),a=i?.nonce||i?.getAttribute("nonce");s=Promise.allSettled(r.map(l=>{if(l=ss(l),l in Ir)return;Ir[l]=!0;const c=l.endsWith(".css"),u=c?'[rel="stylesheet"]':"";if(document.querySelector(`link[href="${l}"]${u}`))return;const d=document.createElement("link");if(d.rel=c?"stylesheet":os,c||(d.as="script"),d.crossOrigin="",d.href=l,a&&d.setAttribute("nonce",a),document.head.appendChild(d),c)return new Promise((f,g)=>{d.addEventListener("load",f),d.addEventListener("error",()=>g(new Error(`Unable to preload CSS for ${l}`)))})}))}function n(i){const a=new Event("vite:preloadError",{cancelable:!0});if(a.payload=i,window.dispatchEvent(a),!a.defaultPrevented)throw i}return s.then(i=>{for(const a of i||[])a.status==="rejected"&&n(a.reason);return e().catch(n)})},$=t=>typeof t=="string",wt=()=>{let t,e;const r=new Promise((o,s)=>{t=o,e=s});return r.resolve=t,r.reject=e,r},Mr=t=>t==null?"":String(t),ns=(t,e,r)=>{t.forEach(o=>{e[o]&&(r[o]=e[o])})},is=/###/g,Vr=t=>t&&t.includes("###")?t.replace(is,"."):t,Hr=t=>!t||$(t),$t=(t,e,r)=>{const o=$(e)?e.split("."):e;let s=0;for(;s<o.length-1;){if(Hr(t))return{};const n=Vr(o[s]);!t[n]&&r&&(t[n]=new r),Object.prototype.hasOwnProperty.call(t,n)?t=t[n]:t={},++s}return Hr(t)?{}:{obj:t,k:Vr(o[s])}},Wr=(t,e,r)=>{const{obj:o,k:s}=$t(t,e,Object);if(o!==void 0||e.length===1){o[s]=r;return}let n=e[e.length-1],i=e.slice(0,e.length-1),a=$t(t,i,Object);for(;a.obj===void 0&&i.length;)n=`${i[i.length-1]}.${n}`,i=i.slice(0,i.length-1),a=$t(t,i,Object),a?.obj&&typeof a.obj[`${a.k}.${n}`]<"u"&&(a.obj=void 0);a.obj[`${a.k}.${n}`]=r},as=(t,e,r,o)=>{const{obj:s,k:n}=$t(t,e,Object);s[n]=s[n]||[],s[n].push(r)},zt=(t,e)=>{const{obj:r,k:o}=$t(t,e);if(r&&Object.prototype.hasOwnProperty.call(r,o))return r[o]},ls=(t,e,r)=>{const o=zt(t,r);return o!==void 0?o:zt(e,r)},Co=(t,e,r)=>{for(const o in e)o!=="__proto__"&&o!=="constructor"&&(o in t?$(t[o])||t[o]instanceof String||$(e[o])||e[o]instanceof String?r&&(t[o]=e[o]):Co(t[o],e[o],r):t[o]=e[o]);return t},we=t=>t.replace(/[\-\[\]\/\{\}\(\)\*\+\?\.\\\^\$\|]/g,"\\$&"),cs={"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;","/":"&#x2F;"},us=t=>$(t)?t.replace(/[&<>"'\/]/g,e=>cs[e]):t;class ds{constructor(e){this.capacity=e,this.regExpMap=new Map,this.regExpQueue=[]}getRegExp(e){const r=this.regExpMap.get(e);if(r!==void 0)return r;const o=new RegExp(e);return this.regExpQueue.length===this.capacity&&this.regExpMap.delete(this.regExpQueue.shift()),this.regExpMap.set(e,o),this.regExpQueue.push(e),o}}const hs=[" ",",","?","!",";"],ps=new ds(20),fs=(t,e,r)=>{e=e||"",r=r||"";const o=hs.filter(i=>!e.includes(i)&&!r.includes(i));if(o.length===0)return!0;const s=ps.getRegExp(`(${o.map(i=>i==="?"?"\\?":i).join("|")})`);let n=!s.test(t);if(!n){const i=t.indexOf(r);i>0&&!s.test(t.substring(0,i))&&(n=!0)}return n},yr=(t,e,r=".")=>{if(!t)return;if(t[e])return Object.prototype.hasOwnProperty.call(t,e)?t[e]:void 0;const o=e.split(r);let s=t;for(let n=0;n<o.length;){if(!s||typeof s!="object")return;let i,a="";for(let l=n;l<o.length;++l)if(l!==n&&(a+=r),a+=o[l],i=s[a],i!==void 0){if(["string","number","boolean"].includes(typeof i)&&l<o.length-1)continue;n+=l-n+1;break}s=i}return s},St=t=>t?.replace(/_/g,"-"),gs={type:"logger",log(t){this.output("log",t)},warn(t){this.output("warn",t)},error(t){this.output("error",t)},output(t,e){console?.[t]?.apply?.(console,e)}};class It{constructor(e,r={}){this.init(e,r)}init(e,r={}){this.prefix=r.prefix||"i18next:",this.logger=e||gs,this.options=r,this.debug=r.debug}log(...e){return this.forward(e,"log","",!0)}warn(...e){return this.forward(e,"warn","",!0)}error(...e){return this.forward(e,"error","")}deprecate(...e){return this.forward(e,"warn","WARNING DEPRECATED: ",!0)}forward(e,r,o,s){return s&&!this.debug?null:(e=e.map(n=>$(n)?n.replace(/[\r\n\x00-\x1F\x7F]/g," "):n),$(e[0])&&(e[0]=`${o}${this.prefix} ${e[0]}`),this.logger[r](e))}create(e){return new It(this.logger,{prefix:`${this.prefix}:${e}:`,...this.options})}clone(e){return e=e||this.options,e.prefix=e.prefix||this.prefix,new It(this.logger,e)}}var de=new It;class Qt{constructor(){this.observers={}}on(e,r){return e.split(" ").forEach(o=>{this.observers[o]||(this.observers[o]=new Map);const s=this.observers[o].get(r)||0;this.observers[o].set(r,s+1)}),this}off(e,r){if(this.observers[e]){if(!r){delete this.observers[e];return}this.observers[e].delete(r)}}once(e,r){const o=(...s)=>{r(...s),this.off(e,o)};return this.on(e,o),this}emit(e,...r){this.observers[e]&&Array.from(this.observers[e].entries()).forEach(([s,n])=>{for(let i=0;i<n;i++)s(...r)}),this.observers["*"]&&Array.from(this.observers["*"].entries()).forEach(([s,n])=>{for(let i=0;i<n;i++)s(e,...r)})}}class Kr extends Qt{constructor(e,r={ns:["translation"],defaultNS:"translation"}){super(),this.data=e||{},this.options=r,this.options.keySeparator===void 0&&(this.options.keySeparator="."),this.options.ignoreJSONStructure===void 0&&(this.options.ignoreJSONStructure=!0)}addNamespaces(e){this.options.ns.includes(e)||this.options.ns.push(e)}removeNamespaces(e){const r=this.options.ns.indexOf(e);r>-1&&this.options.ns.splice(r,1)}getResource(e,r,o,s={}){const n=s.keySeparator!==void 0?s.keySeparator:this.options.keySeparator,i=s.ignoreJSONStructure!==void 0?s.ignoreJSONStructure:this.options.ignoreJSONStructure;let a;e.includes(".")?a=e.split("."):(a=[e,r],o&&(Array.isArray(o)?a.push(...o):$(o)&&n?a.push(...o.split(n)):a.push(o)));const l=zt(this.data,a);return!l&&!r&&!o&&e.includes(".")&&(e=a[0],r=a[1],o=a.slice(2).join(".")),l||!i||!$(o)?l:yr(this.data?.[e]?.[r],o,n)}addResource(e,r,o,s,n={silent:!1}){const i=n.keySeparator!==void 0?n.keySeparator:this.options.keySeparator;let a=[e,r];o&&(a=a.concat(i?o.split(i):o)),e.includes(".")&&(a=e.split("."),s=r,r=a[1]),this.addNamespaces(r),Wr(this.data,a,s),n.silent||this.emit("added",e,r,o,s)}addResources(e,r,o,s={silent:!1}){for(const n in o)($(o[n])||Array.isArray(o[n]))&&this.addResource(e,r,n,o[n],{silent:!0});s.silent||this.emit("added",e,r,o)}addResourceBundle(e,r,o,s,n,i={silent:!1,skipCopy:!1}){let a=[e,r];e.includes(".")&&(a=e.split("."),s=o,o=r,r=a[1]),this.addNamespaces(r);let l=zt(this.data,a)||{};i.skipCopy||(o=JSON.parse(JSON.stringify(o))),s?Co(l,o,n):l={...l,...o},Wr(this.data,a,l),i.silent||this.emit("added",e,r,o)}removeResourceBundle(e,r){this.hasResourceBundle(e,r)&&delete this.data[e][r],this.removeNamespaces(r),this.emit("removed",e,r)}hasResourceBundle(e,r){return this.getResource(e,r)!==void 0}getResourceBundle(e,r){return r||(r=this.options.defaultNS),this.getResource(e,r)}getDataByLanguage(e){return this.data[e]}hasLanguageSomeTranslations(e){const r=this.getDataByLanguage(e);return!!(r&&Object.keys(r)||[]).find(s=>r[s]&&Object.keys(r[s]).length>0)}toJSON(){return this.data}}var So={processors:{},addPostProcessor(t){this.processors[t.name]=t},handle(t,e,r,o,s){return t.forEach(n=>{e=this.processors[n]?.process(e,r,o,s)??e}),e}};const Eo=Symbol("i18next/PATH_KEY");function ms(){const t=[],e=Object.create(null);let r;return e.get=(o,s)=>(r?.revoke?.(),s===Eo?t:(t.push(s),r=Proxy.revocable(o,e),r.proxy)),Proxy.revocable(Object.create(null),e).proxy}function Qe(t,e){const{[Eo]:r}=t(ms()),o=e?.keySeparator??".",s=e?.nsSeparator??":",n=e?.enableSelector==="strict";if(r.length>1&&s){const i=e?.ns,a=n?Array.isArray(i)?i:i?[i]:null:Array.isArray(i)?i:null;if(a&&(n?a:a.length>1?a.slice(1):[]).includes(r[0]))return`${r[0]}${s}${r.slice(1).join(o)}`}return r.join(o)}const dr=t=>!$(t)&&typeof t!="boolean"&&typeof t!="number";class Mt extends Qt{constructor(e,r={}){super(),ns(["resourceStore","languageUtils","pluralResolver","interpolator","backendConnector","i18nFormat","utils"],e,this),this.options=r,this.options.keySeparator===void 0&&(this.options.keySeparator="."),this.logger=de.create("translator"),this.checkedLoadedFor={}}changeLanguage(e){e&&(this.language=e)}exists(e,r={interpolation:{}}){const o={...r};if(e==null)return!1;const s=this.resolve(e,o);if(s?.res===void 0)return!1;const n=dr(s.res);return!(o.returnObjects===!1&&n)}extractFromKey(e,r){let o=r.nsSeparator!==void 0?r.nsSeparator:this.options.nsSeparator;o===void 0&&(o=":");const s=r.keySeparator!==void 0?r.keySeparator:this.options.keySeparator;let n=r.ns||this.options.defaultNS||[];const i=o&&e.includes(o),a=!this.options.userDefinedKeySeparator&&!r.keySeparator&&!this.options.userDefinedNsSeparator&&!r.nsSeparator&&!fs(e,o,s);if(i&&!a){const l=e.match(this.interpolator.nestingRegexp);if(l&&l.length>0)return{key:e,namespaces:$(n)?[n]:n};const c=e.split(o);(o!==s||o===s&&this.options.ns.includes(c[0]))&&(n=c.shift()),e=c.join(s)}return{key:e,namespaces:$(n)?[n]:n}}translate(e,r,o){let s=typeof r=="object"?{...r}:r;if(typeof s!="object"&&this.options.overloadTranslationOptionHandler&&(s=this.options.overloadTranslationOptionHandler(arguments)),typeof s=="object"&&(s={...s}),s||(s={}),e==null)return"";typeof e=="function"&&(e=Qe(e,{...this.options,...s})),Array.isArray(e)||(e=[String(e)]),e=e.map(D=>typeof D=="function"?Qe(D,{...this.options,...s}):String(D));const n=s.returnDetails!==void 0?s.returnDetails:this.options.returnDetails,i=s.keySeparator!==void 0?s.keySeparator:this.options.keySeparator,{key:a,namespaces:l}=this.extractFromKey(e[e.length-1],s),c=l[l.length-1];let u=s.nsSeparator!==void 0?s.nsSeparator:this.options.nsSeparator;u===void 0&&(u=":");const d=s.lng||this.language,f=s.appendNamespaceToCIMode||this.options.appendNamespaceToCIMode;if(d?.toLowerCase()==="cimode")return f?n?{res:`${c}${u}${a}`,usedKey:a,exactUsedKey:a,usedLng:d,usedNS:c,usedParams:this.getUsedParamsDetails(s)}:`${c}${u}${a}`:n?{res:a,usedKey:a,exactUsedKey:a,usedLng:d,usedNS:c,usedParams:this.getUsedParamsDetails(s)}:a;const g=this.resolve(e,s);let p=g?.res;const m=g?.usedKey||a,k=g?.exactUsedKey||a,w=["[object Number]","[object Function]","[object RegExp]"],x=s.joinArrays!==void 0?s.joinArrays:this.options.joinArrays,_=!this.i18nFormat||this.i18nFormat.handleAsObject,C=s.count!==void 0&&!$(s.count),P=Mt.hasDefaultValue(s),R=C?this.pluralResolver.getSuffix(d,s.count,s):"",L=s.ordinal&&C?this.pluralResolver.getSuffix(d,s.count,{ordinal:!1}):"",ye=C&&!s.ordinal&&s.count===0,B=ye&&s[`defaultValue${this.options.pluralSeparator}zero`]||s[`defaultValue${R}`]||s[`defaultValue${L}`]||s.defaultValue;let O=p;_&&!p&&P&&(O=B);const W=dr(O),re=Object.prototype.toString.apply(O);if(_&&O&&W&&!w.includes(re)&&!($(x)&&Array.isArray(O))){if(!s.returnObjects&&!this.options.returnObjects){this.options.returnedObjectHandler||this.logger.warn("accessing an object - but returnObjects options is not enabled!");const D=this.options.returnedObjectHandler?this.options.returnedObjectHandler(m,O,{...s,ns:l}):`key '${a} (${this.language})' returned an object instead of string.`;return n?(g.res=D,g.usedParams=this.getUsedParamsDetails(s),g):D}if(i){const D=Array.isArray(O),A=D?[]:{},ue=D?k:m;for(const T in O)if(Object.prototype.hasOwnProperty.call(O,T)){const F=`${ue}${i}${T}`;P&&!p?A[T]=this.translate(F,{...s,defaultValue:dr(B)?B[T]:void 0,joinArrays:!1,ns:l}):A[T]=this.translate(F,{...s,joinArrays:!1,ns:l}),A[T]===F&&(A[T]=O[T])}p=A}}else if(_&&$(x)&&Array.isArray(p))p=p.join(x),p&&(p=this.extendTranslation(p,e,s,o));else{let D=!1,A=!1;!this.isValidLookup(p)&&P&&(D=!0,p=B),this.isValidLookup(p)||(A=!0,p=a);const T=(s.missingKeyNoValueFallbackToKey||this.options.missingKeyNoValueFallbackToKey)&&A?void 0:p,F=P&&B!==p&&this.options.updateMissing;if(A||D||F){if(this.logger.log(F?"updateKey":"missingKey",d,c,C&&!F?`${a}${this.pluralResolver.getSuffix(d,s.count,s)}`:a,F?B:p),i){const G=this.resolve(a,{...s,keySeparator:!1});G&&G.res&&this.logger.warn("Seems the loaded translations were in flat JSON format instead of nested. Either set keySeparator: false on init or make sure your translations are published in nested format.")}let N=[];const K=this.languageUtils.getFallbackCodes(this.options.fallbackLng,s.lng||this.language);if(this.options.saveMissingTo==="fallback"&&K&&K[0])for(let G=0;G<K.length;G++)N.push(K[G]);else this.options.saveMissingTo==="all"?N=this.languageUtils.toResolveHierarchy(s.lng||this.language):N.push(s.lng||this.language);const Dt=(G,Be,yt)=>{const Tr=P&&yt!==p?yt:T;this.options.missingKeyHandler?this.options.missingKeyHandler(G,c,Be,Tr,F,s):this.backendConnector?.saveMissing&&this.backendConnector.saveMissing(G,c,Be,Tr,F,s),this.emit("missingKey",G,c,Be,p)};this.options.saveMissing&&(this.options.saveMissingPlurals&&C?N.forEach(G=>{const Be=this.pluralResolver.getSuffixes(G,s);ye&&s[`defaultValue${this.options.pluralSeparator}zero`]&&!Be.includes(`${this.options.pluralSeparator}zero`)&&Be.push(`${this.options.pluralSeparator}zero`),Be.forEach(yt=>{Dt([G],a+yt,s[`defaultValue${yt}`]||B)})}):Dt(N,a,B))}p=this.extendTranslation(p,e,s,g,o),A&&p===a&&this.options.appendNamespaceToMissingKey&&(p=`${c}${u}${a}`),(A||D)&&this.options.parseMissingKeyHandler&&(p=this.options.parseMissingKeyHandler(this.options.appendNamespaceToMissingKey?`${c}${u}${a}`:a,D?p:void 0,s))}return n?(g.res=p,g.usedParams=this.getUsedParamsDetails(s),g):p}extendTranslation(e,r,o,s,n){if(this.i18nFormat?.parse)e=this.i18nFormat.parse(e,{...this.options.interpolation.defaultVariables,...o},o.lng||this.language||s.usedLng,s.usedNS,s.usedKey,{resolved:s});else if(!o.skipInterpolation){o.interpolation&&this.interpolator.init({...o,interpolation:{...this.options.interpolation,...o.interpolation}});const l=$(e)&&(o?.interpolation?.skipOnVariables!==void 0?o.interpolation.skipOnVariables:this.options.interpolation.skipOnVariables);let c;if(l){const d=e.match(this.interpolator.nestingRegexp);c=d&&d.length}let u=o.replace&&!$(o.replace)?o.replace:o;if(this.options.interpolation.defaultVariables&&(u={...this.options.interpolation.defaultVariables,...u}),e=this.interpolator.interpolate(e,u,o.lng||this.language||s.usedLng,o),l){const d=e.match(this.interpolator.nestingRegexp),f=d&&d.length;c<f&&(o.nest=!1)}!o.lng&&s&&s.res&&(o.lng=this.language||s.usedLng),o.nest!==!1&&(e=this.interpolator.nest(e,(...d)=>n?.[0]===d[0]&&!o.context?(this.logger.warn(`It seems you are nesting recursively key: ${d[0]} in key: ${r[0]}`),null):this.translate(...d,r),o)),o.interpolation&&this.interpolator.reset()}const i=o.postProcess||this.options.postProcess,a=$(i)?[i]:i;return e!=null&&a?.length&&o.applyPostProcessor!==!1&&(e=So.handle(a,e,r,this.options&&this.options.postProcessPassResolved?{i18nResolved:{...s,usedParams:this.getUsedParamsDetails(o)},...o}:o,this)),e}resolve(e,r={}){let o,s,n,i,a;return $(e)&&(e=[e]),Array.isArray(e)&&(e=e.map(l=>typeof l=="function"?Qe(l,{...this.options,...r}):l)),e.forEach(l=>{if(this.isValidLookup(o))return;const c=this.extractFromKey(l,r),u=c.key;s=u;let d=c.namespaces;this.options.fallbackNS&&(d=d.concat(this.options.fallbackNS));const f=r.count!==void 0&&!$(r.count),g=f&&!r.ordinal&&r.count===0,p=r.context!==void 0&&($(r.context)||typeof r.context=="number")&&r.context!=="",m=r.lngs?r.lngs:this.languageUtils.toResolveHierarchy(r.lng||this.language,r.fallbackLng);d.forEach(k=>{this.isValidLookup(o)||(a=k,!this.checkedLoadedFor[`${m[0]}-${k}`]&&this.utils?.hasLoadedNamespace&&!this.utils?.hasLoadedNamespace(a)&&(this.checkedLoadedFor[`${m[0]}-${k}`]=!0,this.logger.warn(`key "${s}" for languages "${m.join(", ")}" won't get resolved as namespace "${a}" was not yet loaded`,"This means something IS WRONG in your setup. You access the t function before i18next.init / i18next.loadNamespace / i18next.changeLanguage was done. Wait for the callback or Promise to resolve before accessing it!!!")),m.forEach(w=>{if(this.isValidLookup(o))return;i=w;const x=[u];if(this.i18nFormat?.addLookupKeys)this.i18nFormat.addLookupKeys(x,u,w,k,r);else{let C;f&&(C=this.pluralResolver.getSuffix(w,r.count,r));const P=`${this.options.pluralSeparator}zero`,R=`${this.options.pluralSeparator}ordinal${this.options.pluralSeparator}`;if(f&&(r.ordinal&&C.startsWith(R)&&x.push(u+C.replace(R,this.options.pluralSeparator)),x.push(u+C),g&&x.push(u+P)),p){const L=`${u}${this.options.contextSeparator||"_"}${r.context}`;x.push(L),f&&(r.ordinal&&C.startsWith(R)&&x.push(L+C.replace(R,this.options.pluralSeparator)),x.push(L+C),g&&x.push(L+P))}}let _;for(;_=x.pop();)this.isValidLookup(o)||(n=_,o=this.getResource(w,k,_,r))}))})}),{res:o,usedKey:s,exactUsedKey:n,usedLng:i,usedNS:a}}isValidLookup(e){return e!==void 0&&!(!this.options.returnNull&&e===null)&&!(!this.options.returnEmptyString&&e==="")}getResource(e,r,o,s={}){return this.i18nFormat?.getResource?this.i18nFormat.getResource(e,r,o,s):this.resourceStore.getResource(e,r,o,s)}getUsedParamsDetails(e={}){const r=["defaultValue","ordinal","context","replace","lng","lngs","fallbackLng","ns","keySeparator","nsSeparator","returnObjects","returnDetails","joinArrays","postProcess","interpolation"],o=e.replace&&!$(e.replace);let s=o?e.replace:e;if(o&&typeof e.count<"u"&&(s.count=e.count),this.options.interpolation.defaultVariables&&(s={...this.options.interpolation.defaultVariables,...s}),!o){s={...s};for(const n of r)delete s[n]}return s}static hasDefaultValue(e){const r="defaultValue";for(const o in e)if(Object.prototype.hasOwnProperty.call(e,o)&&o.startsWith(r)&&e[o]!==void 0)return!0;return!1}}class Gr{constructor(e){this.options=e,this.supportedLngs=this.options.supportedLngs||!1,this.logger=de.create("languageUtils")}getScriptPartFromCode(e){if(e=St(e),!e||!e.includes("-"))return null;const r=e.split("-");return r.length===2||(r.pop(),r[r.length-1].toLowerCase()==="x")?null:this.formatLanguageCode(r.join("-"))}getLanguagePartFromCode(e){if(e=St(e),!e||!e.includes("-"))return e;const r=e.split("-");return this.formatLanguageCode(r[0])}formatLanguageCode(e){if($(e)&&e.includes("-")){let r;try{r=Intl.getCanonicalLocales(e)[0]}catch{}return r&&this.options.lowerCaseLng&&(r=r.toLowerCase()),r||(this.options.lowerCaseLng?e.toLowerCase():e)}return this.options.cleanCode||this.options.lowerCaseLng?e.toLowerCase():e}isSupportedCode(e){return(this.options.load==="languageOnly"||this.options.nonExplicitSupportedLngs)&&(e=this.getLanguagePartFromCode(e)),!this.supportedLngs||!this.supportedLngs.length||this.supportedLngs.includes(e)}getBestMatchFromCodes(e){if(!e)return null;let r;return e.forEach(o=>{if(r)return;const s=this.formatLanguageCode(o);(!this.options.supportedLngs||this.isSupportedCode(s))&&(r=s)}),!r&&this.options.supportedLngs&&e.forEach(o=>{if(r)return;const s=this.getScriptPartFromCode(o);if(this.isSupportedCode(s))return r=s;const n=this.getLanguagePartFromCode(o);if(this.isSupportedCode(n))return r=n;r=this.options.supportedLngs.find(i=>i===n?!0:!i.includes("-")&&!n.includes("-")?!1:!!(i.includes("-")&&!n.includes("-")&&i.slice(0,i.indexOf("-"))===n||i.startsWith(n)&&n.length>1))}),r||(r=this.getFallbackCodes(this.options.fallbackLng)[0]),r}getFallbackCodes(e,r){if(!e)return[];if(typeof e=="function"&&(e=e(r)),$(e)&&(e=[e]),Array.isArray(e))return e;if(!r)return e.default||[];let o=e[r];return o||(o=e[this.getScriptPartFromCode(r)]),o||(o=e[this.formatLanguageCode(r)]),o||(o=e[this.getLanguagePartFromCode(r)]),o||(o=e.default),o||[]}toResolveHierarchy(e,r){const o=this.getFallbackCodes((r===!1?[]:r)||this.options.fallbackLng||[],e),s=[],n=i=>{i&&(this.isSupportedCode(i)?s.push(i):this.logger.warn(`rejecting language code not found in supportedLngs: ${i}`))};return $(e)&&(e.includes("-")||e.includes("_"))?(this.options.load!=="languageOnly"&&n(this.formatLanguageCode(e)),this.options.load!=="languageOnly"&&this.options.load!=="currentOnly"&&n(this.getScriptPartFromCode(e)),this.options.load!=="currentOnly"&&n(this.getLanguagePartFromCode(e))):$(e)&&n(this.formatLanguageCode(e)),o.forEach(i=>{s.includes(i)||n(this.formatLanguageCode(i))}),s}}const qr={zero:0,one:1,two:2,few:3,many:4,other:5},Jr={select:t=>t===1?"one":"other",resolvedOptions:()=>({pluralCategories:["one","other"]})};class bs{constructor(e,r={}){this.languageUtils=e,this.options=r,this.logger=de.create("pluralResolver"),this.pluralRulesCache={}}clearCache(){this.pluralRulesCache={}}getRule(e,r={}){const o=St(e==="dev"?"en":e),s=r.ordinal?"ordinal":"cardinal",n=JSON.stringify({cleanedCode:o,type:s});if(n in this.pluralRulesCache)return this.pluralRulesCache[n];let i;try{i=new Intl.PluralRules(o,{type:s})}catch{if(typeof Intl>"u")return this.logger.error("No Intl support, please use an Intl polyfill!"),Jr;if(!e.match(/-|_/))return Jr;const l=this.languageUtils.getLanguagePartFromCode(e);i=this.getRule(l,r)}return this.pluralRulesCache[n]=i,i}needsPlural(e,r={}){let o=this.getRule(e,r);return o||(o=this.getRule("dev",r)),o?.resolvedOptions().pluralCategories.length>1}getPluralFormsOfKey(e,r,o={}){return this.getSuffixes(e,o).map(s=>`${r}${s}`)}getSuffixes(e,r={}){let o=this.getRule(e,r);return o||(o=this.getRule("dev",r)),o?o.resolvedOptions().pluralCategories.sort((s,n)=>qr[s]-qr[n]).map(s=>`${this.options.prepend}${r.ordinal?`ordinal${this.options.prepend}`:""}${s}`):[]}getSuffix(e,r,o={}){const s=this.getRule(e,o);return s?`${this.options.prepend}${o.ordinal?`ordinal${this.options.prepend}`:""}${s.select(r)}`:(this.logger.warn(`no plural rule found for: ${e}`),this.getSuffix("dev",r,o))}}const Xr=(t,e,r,o=".",s=!0)=>{let n=ls(t,e,r);return!n&&s&&$(r)&&(n=yr(t,r,o),n===void 0&&(n=yr(e,r,o))),n},hr=t=>t.replace(/\$/g,"$$$$");class Yr{constructor(e={}){this.logger=de.create("interpolator"),this.options=e,this.format=e?.interpolation?.format||(r=>r),this.init(e)}init(e={}){e.interpolation||(e.interpolation={escapeValue:!0});const{escape:r,escapeValue:o,useRawValueToEscape:s,prefix:n,prefixEscaped:i,suffix:a,suffixEscaped:l,formatSeparator:c,unescapeSuffix:u,unescapePrefix:d,nestingPrefix:f,nestingPrefixEscaped:g,nestingSuffix:p,nestingSuffixEscaped:m,nestingOptionsSeparator:k,maxReplaces:w,alwaysFormat:x}=e.interpolation;this.escape=r!==void 0?r:us,this.escapeValue=o!==void 0?o:!0,this.useRawValueToEscape=s!==void 0?s:!1,this.prefix=n?we(n):i||"{{",this.suffix=a?we(a):l||"}}",this.formatSeparator=c||",",this.unescapePrefix=u?"":d?we(d):"-",this.unescapeSuffix=this.unescapePrefix?"":u?we(u):"",this.nestingPrefix=f?we(f):g||we("$t("),this.nestingSuffix=p?we(p):m||we(")"),this.nestingOptionsSeparator=k||",",this.maxReplaces=w||1e3,this.alwaysFormat=x!==void 0?x:!1,this.resetRegExp()}reset(){this.options&&this.init(this.options)}resetRegExp(){const e=(r,o)=>r?.source===o?(r.lastIndex=0,r):new RegExp(o,"g");this.regexp=e(this.regexp,`${this.prefix}(.+?)${this.suffix}`),this.regexpUnescape=e(this.regexpUnescape,`${this.prefix}${this.unescapePrefix}(.+?)${this.unescapeSuffix}${this.suffix}`),this.nestingRegexp=e(this.nestingRegexp,`${this.nestingPrefix}((?:[^()"']+|"[^"]*"|'[^']*'|\\((?:[^()]|"[^"]*"|'[^']*')*\\))*?)${this.nestingSuffix}`)}interpolate(e,r,o,s){let n,i,a;const l=this.options&&this.options.interpolation&&this.options.interpolation.defaultVariables||{},c=g=>{if(!g.includes(this.formatSeparator)){const w=Xr(r,l,g,this.options.keySeparator,this.options.ignoreJSONStructure);return this.alwaysFormat?this.format(w,void 0,o,{...s,...r,interpolationkey:g}):w}const p=g.split(this.formatSeparator),m=p.shift().trim(),k=p.join(this.formatSeparator).trim();return this.format(Xr(r,l,m,this.options.keySeparator,this.options.ignoreJSONStructure),k,o,{...s,...r,interpolationkey:m})};this.resetRegExp(),!this.escapeValue&&typeof e=="string"&&/\$t\([^)]*\{[^}]*\{\{/.test(e)&&this.logger.warn("nesting options string contains interpolated variables with escapeValue: false — if any of those values are attacker-controlled they can inject additional nesting options (e.g. redirect lng/ns). Sanitise untrusted input before passing it to t(), or keep escapeValue: true.");const u=s?.missingInterpolationHandler||this.options.missingInterpolationHandler,d=s?.interpolation?.skipOnVariables!==void 0?s.interpolation.skipOnVariables:this.options.interpolation.skipOnVariables;return[{regex:this.regexpUnescape,safeValue:g=>hr(g)},{regex:this.regexp,safeValue:g=>this.escapeValue?hr(this.escape(g)):hr(g)}].forEach(g=>{for(a=0;n=g.regex.exec(e);){const p=n[1].trim();if(i=c(p),i===void 0)if(typeof u=="function"){const k=u(e,n,s);i=$(k)?k:""}else if(s&&Object.prototype.hasOwnProperty.call(s,p))i="";else if(d){i=n[0];continue}else this.logger.warn(`missed to pass in variable ${p} for interpolating ${e}`),i="";else!$(i)&&!this.useRawValueToEscape&&(i=Mr(i));const m=g.safeValue(i);if(e=e.replace(n[0],m),d?(g.regex.lastIndex+=i.length,g.regex.lastIndex-=n[0].length):g.regex.lastIndex=0,a++,a>=this.maxReplaces)break}}),e}nest(e,r,o={}){let s,n,i;const a=(l,c)=>{const u=this.nestingOptionsSeparator;if(!l.includes(u))return l;const d=l.split(new RegExp(`${we(u)}[ ]*{`));let f=`{${d[1]}`;l=d[0],f=this.interpolate(f,i);const g=f.match(/'/g),p=f.match(/"/g);((g?.length??0)%2===0&&!p||(p?.length??0)%2!==0)&&(f=f.replace(/'/g,'"'));try{i=JSON.parse(f),c&&(i={...c,...i})}catch(m){return this.logger.warn(`failed parsing options string in nesting for key ${l}`,m),`${l}${u}${f}`}return i.defaultValue&&i.defaultValue.includes(this.prefix)&&delete i.defaultValue,l};for(;s=this.nestingRegexp.exec(e);){let l=[];i={...o},i=i.replace&&!$(i.replace)?i.replace:i,i.applyPostProcessor=!1,delete i.defaultValue;const c=/{.*}/.test(s[1])?s[1].lastIndexOf("}")+1:s[1].indexOf(this.formatSeparator);if(c!==-1&&(l=s[1].slice(c).split(this.formatSeparator).map(u=>u.trim()).filter(Boolean),s[1]=s[1].slice(0,c)),n=r(a.call(this,s[1].trim(),i),i),n&&s[0]===e&&!$(n))return n;$(n)||(n=Mr(n)),n||(this.logger.warn(`missed to resolve ${s[1]} for nesting ${e}`),n=""),l.length&&(n=l.reduce((u,d)=>this.format(u,d,o.lng,{...o,interpolationkey:s[1].trim()}),n.trim())),e=e.replace(s[0],n),this.regexp.lastIndex=0}return e}}const vs=t=>{let e=t.toLowerCase().trim();const r={};if(t.includes("(")){const o=t.split("(");e=o[0].toLowerCase().trim();const s=o[1].slice(0,-1);e==="currency"&&!s.includes(":")?r.currency||(r.currency=s.trim()):e==="relativetime"&&!s.includes(":")?r.range||(r.range=s.trim()):s.split(";").forEach(i=>{if(i){const[a,...l]=i.split(":"),c=l.join(":").trim().replace(/^'+|'+$/g,""),u=a.trim();r[u]||(r[u]=c),c==="false"&&(r[u]=!1),c==="true"&&(r[u]=!0),isNaN(c)||(r[u]=parseInt(c,10))}})}return{formatName:e,formatOptions:r}},Qr=t=>{const e={};return(r,o,s)=>{let n=s;s&&s.interpolationkey&&s.formatParams&&s.formatParams[s.interpolationkey]&&s[s.interpolationkey]&&(n={...n,[s.interpolationkey]:void 0});const i=o+JSON.stringify(n);let a=e[i];return a||(a=t(St(o),s),e[i]=a),a(r)}},ys=t=>(e,r,o)=>t(St(r),o)(e);class ws{constructor(e={}){this.logger=de.create("formatter"),this.options=e,this.init(e)}init(e,r={interpolation:{}}){this.formatSeparator=r.interpolation.formatSeparator||",";const o=r.cacheInBuiltFormats?Qr:ys;this.formats={number:o((s,n)=>{const i=new Intl.NumberFormat(s,{...n});return a=>i.format(a)}),currency:o((s,n)=>{const i=new Intl.NumberFormat(s,{...n,style:"currency"});return a=>i.format(a)}),datetime:o((s,n)=>{const i=new Intl.DateTimeFormat(s,{...n});return a=>i.format(a)}),relativetime:o((s,n)=>{const i=new Intl.RelativeTimeFormat(s,{...n});return a=>i.format(a,n.range||"day")}),list:o((s,n)=>{const i=new Intl.ListFormat(s,{...n});return a=>i.format(a)})}}add(e,r){this.formats[e.toLowerCase().trim()]=r}addCached(e,r){this.formats[e.toLowerCase().trim()]=Qr(r)}format(e,r,o,s={}){if(!r||e==null)return e;const n=r.split(this.formatSeparator);if(n.length>1&&n[0].indexOf("(")>1&&!n[0].includes(")")&&n.find(a=>a.includes(")"))){const a=n.findIndex(l=>l.includes(")"));n[0]=[n[0],...n.splice(1,a)].join(this.formatSeparator)}return n.reduce((a,l)=>{const{formatName:c,formatOptions:u}=vs(l);if(this.formats[c]){let d=a;try{const f=s?.formatParams?.[s.interpolationkey]||{},g=f.locale||f.lng||s.locale||s.lng||o;d=this.formats[c](a,g,{...u,...s,...f})}catch(f){this.logger.warn(f)}return d}else this.logger.warn(`there was no format function for ${c}`);return a},e)}}const xs=(t,e)=>{t.pending[e]!==void 0&&(delete t.pending[e],t.pendingCount--)};class ks extends Qt{constructor(e,r,o,s={}){super(),this.backend=e,this.store=r,this.services=o,this.languageUtils=o.languageUtils,this.options=s,this.logger=de.create("backendConnector"),this.waitingReads=[],this.maxParallelReads=s.maxParallelReads||10,this.readingCalls=0,this.maxRetries=s.maxRetries>=0?s.maxRetries:5,this.retryTimeout=s.retryTimeout>=1?s.retryTimeout:350,this.state={},this.queue=[],this.backend?.init?.(o,s.backend,s)}queueLoad(e,r,o,s){const n={},i={},a={},l={};return e.forEach(c=>{let u=!0;r.forEach(d=>{const f=`${c}|${d}`;!o.reload&&this.store.hasResourceBundle(c,d)?this.state[f]=2:this.state[f]<0||(this.state[f]===1?i[f]===void 0&&(i[f]=!0):(this.state[f]=1,u=!1,i[f]===void 0&&(i[f]=!0),n[f]===void 0&&(n[f]=!0),l[d]===void 0&&(l[d]=!0)))}),u||(a[c]=!0)}),(Object.keys(n).length||Object.keys(i).length)&&this.queue.push({pending:i,pendingCount:Object.keys(i).length,loaded:{},errors:[],callback:s}),{toLoad:Object.keys(n),pending:Object.keys(i),toLoadLanguages:Object.keys(a),toLoadNamespaces:Object.keys(l)}}loaded(e,r,o){const s=e.split("|"),n=s[0],i=s[1];r&&this.emit("failedLoading",n,i,r),!r&&o&&this.store.addResourceBundle(n,i,o,void 0,void 0,{skipCopy:!0}),this.state[e]=r?-1:2,r&&o&&(this.state[e]=0);const a={};this.queue.forEach(l=>{as(l.loaded,[n],i),xs(l,e),r&&l.errors.push(r),l.pendingCount===0&&!l.done&&(Object.keys(l.loaded).forEach(c=>{a[c]||(a[c]={});const u=l.loaded[c];u.length&&u.forEach(d=>{a[c][d]===void 0&&(a[c][d]=!0)})}),l.done=!0,l.errors.length?l.callback(l.errors):l.callback())}),this.emit("loaded",a),this.queue=this.queue.filter(l=>!l.done)}read(e,r,o,s=0,n=this.retryTimeout,i){if(!e.length)return i(null,{});if(this.readingCalls>=this.maxParallelReads){this.waitingReads.push({lng:e,ns:r,fcName:o,tried:s,wait:n,callback:i});return}this.readingCalls++;const a=(c,u)=>{if(this.readingCalls--,this.waitingReads.length>0){const d=this.waitingReads.shift();this.read(d.lng,d.ns,d.fcName,d.tried,d.wait,d.callback)}if(c&&u&&s<this.maxRetries){setTimeout(()=>{this.read(e,r,o,s+1,n*2,i)},n);return}i(c,u)},l=this.backend[o].bind(this.backend);if(l.length===2){try{const c=l(e,r);c&&typeof c.then=="function"?c.then(u=>a(null,u)).catch(a):a(null,c)}catch(c){a(c)}return}return l(e,r,a)}prepareLoading(e,r,o={},s){if(!this.backend)return this.logger.warn("No backend was added via i18next.use. Will not load resources."),s&&s();$(e)&&(e=this.languageUtils.toResolveHierarchy(e)),$(r)&&(r=[r]);const n=this.queueLoad(e,r,o,s);if(!n.toLoad.length)return n.pending.length||s(),null;n.toLoad.forEach(i=>{this.loadOne(i)})}load(e,r,o){this.prepareLoading(e,r,{},o)}reload(e,r,o){this.prepareLoading(e,r,{reload:!0},o)}loadOne(e,r=""){const o=e.split("|"),s=o[0],n=o[1];this.read(s,n,"read",void 0,void 0,(i,a)=>{i&&this.logger.warn(`${r}loading namespace ${n} for language ${s} failed`,i),!i&&a&&this.logger.log(`${r}loaded namespace ${n} for language ${s}`,a),this.loaded(e,i,a)})}saveMissing(e,r,o,s,n,i={},a=()=>{}){if(this.services?.utils?.hasLoadedNamespace&&!this.services?.utils?.hasLoadedNamespace(r)){this.logger.warn(`did not save key "${o}" as the namespace "${r}" was not yet loaded`,"This means something IS WRONG in your setup. You access the t function before i18next.init / i18next.loadNamespace / i18next.changeLanguage was done. Wait for the callback or Promise to resolve before accessing it!!!");return}if(!(o==null||o==="")){if(this.backend?.create){const l={...i,isUpdate:n},c=this.backend.create.bind(this.backend);if(c.length<6)try{let u;c.length===5?u=c(e,r,o,s,l):u=c(e,r,o,s),u&&typeof u.then=="function"?u.then(d=>a(null,d)).catch(a):a(null,u)}catch(u){a(u)}else c(e,r,o,s,a,l)}!e||!e[0]||this.store.addResource(e[0],r,o,s)}}}const pr=()=>({debug:!1,initAsync:!0,ns:["translation"],defaultNS:["translation"],fallbackLng:["dev"],fallbackNS:!1,supportedLngs:!1,nonExplicitSupportedLngs:!1,load:"all",preload:!1,keySeparator:".",nsSeparator:":",pluralSeparator:"_",contextSeparator:"_",enableSelector:!1,partialBundledLanguages:!1,saveMissing:!1,updateMissing:!1,saveMissingTo:"fallback",saveMissingPlurals:!0,missingKeyHandler:!1,missingInterpolationHandler:!1,postProcess:!1,postProcessPassResolved:!1,returnNull:!1,returnEmptyString:!0,returnObjects:!1,joinArrays:!1,returnedObjectHandler:!1,parseMissingKeyHandler:!1,appendNamespaceToMissingKey:!1,appendNamespaceToCIMode:!1,overloadTranslationOptionHandler:t=>{let e={};if(typeof t[1]=="object"&&(e=t[1]),$(t[1])&&(e.defaultValue=t[1]),$(t[2])&&(e.tDescription=t[2]),typeof t[2]=="object"||typeof t[3]=="object"){const r=t[3]||t[2];Object.keys(r).forEach(o=>{e[o]=r[o]})}return e},interpolation:{escapeValue:!0,prefix:"{{",suffix:"}}",formatSeparator:",",unescapePrefix:"-",nestingPrefix:"$t(",nestingSuffix:")",nestingOptionsSeparator:",",maxReplaces:1e3,skipOnVariables:!0},cacheInBuiltFormats:!0}),Zr=t=>($(t.ns)&&(t.ns=[t.ns]),$(t.fallbackLng)&&(t.fallbackLng=[t.fallbackLng]),$(t.fallbackNS)&&(t.fallbackNS=[t.fallbackNS]),t.supportedLngs&&!t.supportedLngs.includes("cimode")&&(t.supportedLngs=t.supportedLngs.concat(["cimode"])),t),Ft=()=>{},$s=t=>{Object.getOwnPropertyNames(Object.getPrototypeOf(t)).forEach(r=>{typeof t[r]=="function"&&(t[r]=t[r].bind(t))})};class _t extends Qt{constructor(e={},r){if(super(),this.options=Zr(e),this.services={},this.logger=de,this.modules={external:[]},$s(this),r&&!this.isInitialized&&!e.isClone){if(!this.options.initAsync)return this.init(e,r),this;setTimeout(()=>{this.init(e,r)},0)}}init(e={},r){this.isInitializing=!0,typeof e=="function"&&(r=e,e={}),e.defaultNS==null&&e.ns&&($(e.ns)?e.defaultNS=e.ns:e.ns.includes("translation")||(e.defaultNS=e.ns[0]));const o=pr();this.options={...o,...this.options,...Zr(e)},this.options.interpolation={...o.interpolation,...this.options.interpolation},e.keySeparator!==void 0&&(this.options.userDefinedKeySeparator=e.keySeparator),e.nsSeparator!==void 0&&(this.options.userDefinedNsSeparator=e.nsSeparator),typeof this.options.overloadTranslationOptionHandler!="function"&&(this.options.overloadTranslationOptionHandler=o.overloadTranslationOptionHandler);const s=c=>c?typeof c=="function"?new c:c:null;if(!this.options.isClone){this.modules.logger?de.init(s(this.modules.logger),this.options):de.init(null,this.options);let c;this.modules.formatter?c=this.modules.formatter:c=ws;const u=new Gr(this.options);this.store=new Kr(this.options.resources,this.options);const d=this.services;d.logger=de,d.resourceStore=this.store,d.languageUtils=u,d.pluralResolver=new bs(u,{prepend:this.options.pluralSeparator}),c&&(d.formatter=s(c),d.formatter.init&&d.formatter.init(d,this.options),this.options.interpolation.format=d.formatter.format.bind(d.formatter)),d.interpolator=new Yr(this.options),d.utils={hasLoadedNamespace:this.hasLoadedNamespace.bind(this)},d.backendConnector=new ks(s(this.modules.backend),d.resourceStore,d,this.options),d.backendConnector.on("*",(f,...g)=>{this.emit(f,...g)}),this.modules.languageDetector&&(d.languageDetector=s(this.modules.languageDetector),d.languageDetector.init&&d.languageDetector.init(d,this.options.detection,this.options)),this.modules.i18nFormat&&(d.i18nFormat=s(this.modules.i18nFormat),d.i18nFormat.init&&d.i18nFormat.init(this)),this.translator=new Mt(this.services,this.options),this.translator.on("*",(f,...g)=>{this.emit(f,...g)}),this.modules.external.forEach(f=>{f.init&&f.init(this)})}if(this.format=this.options.interpolation.format,r||(r=Ft),this.options.fallbackLng&&!this.services.languageDetector&&!this.options.lng){const c=this.services.languageUtils.getFallbackCodes(this.options.fallbackLng);c.length>0&&c[0]!=="dev"&&(this.options.lng=c[0])}!this.services.languageDetector&&!this.options.lng&&this.logger.warn("init: no languageDetector is used and no lng is defined"),["getResource","hasResourceBundle","getResourceBundle","getDataByLanguage"].forEach(c=>{this[c]=(...u)=>this.store[c](...u)}),["addResource","addResources","addResourceBundle","removeResourceBundle"].forEach(c=>{this[c]=(...u)=>(this.store[c](...u),this)});const a=wt(),l=()=>{const c=(u,d)=>{this.isInitializing=!1,this.isInitialized&&!this.initializedStoreOnce&&this.logger.warn("init: i18next is already initialized. You should call init just once!"),this.isInitialized=!0,this.options.isClone||this.logger.log("initialized",this.options),this.emit("initialized",this.options),a.resolve(d),r(u,d)};if((this.languages||this.isLanguageChangingTo)&&!this.isInitialized)return c(null,this.t.bind(this));this.changeLanguage(this.options.lng,c)};return this.options.resources||!this.options.initAsync?l():setTimeout(l,0),a}loadResources(e,r=Ft){let o=r;const s=$(e)?e:this.language;if(typeof e=="function"&&(o=e),!this.options.resources||this.options.partialBundledLanguages){if(s?.toLowerCase()==="cimode"&&(!this.options.preload||this.options.preload.length===0))return o();const n=[],i=a=>{if(!a||a==="cimode")return;this.services.languageUtils.toResolveHierarchy(a).forEach(c=>{c!=="cimode"&&(n.includes(c)||n.push(c))})};s?i(s):this.services.languageUtils.getFallbackCodes(this.options.fallbackLng).forEach(l=>i(l)),this.options.preload?.forEach?.(a=>i(a)),this.services.backendConnector.load(n,this.options.ns,a=>{!a&&!this.resolvedLanguage&&this.language&&this.setResolvedLanguage(this.language),o(a)})}else o(null)}reloadResources(e,r,o){const s=wt();return typeof e=="function"&&(o=e,e=void 0),typeof r=="function"&&(o=r,r=void 0),e||(e=this.languages),r||(r=this.options.ns),o||(o=Ft),this.services.backendConnector.reload(e,r,n=>{s.resolve(),o(n)}),s}use(e){if(!e)throw new Error("You are passing an undefined module! Please check the object you are passing to i18next.use()");if(!e.type)throw new Error("You are passing a wrong module! Please check the object you are passing to i18next.use()");return e.type==="backend"&&(this.modules.backend=e),(e.type==="logger"||e.log&&e.warn&&e.error)&&(this.modules.logger=e),e.type==="languageDetector"&&(this.modules.languageDetector=e),e.type==="i18nFormat"&&(this.modules.i18nFormat=e),e.type==="postProcessor"&&So.addPostProcessor(e),e.type==="formatter"&&(this.modules.formatter=e),e.type==="3rdParty"&&this.modules.external.push(e),this}setResolvedLanguage(e){if(!(!e||!this.languages)&&!["cimode","dev"].includes(e)){for(let r=0;r<this.languages.length;r++){const o=this.languages[r];if(!["cimode","dev"].includes(o)&&this.store.hasLanguageSomeTranslations(o)){this.resolvedLanguage=o;break}}!this.resolvedLanguage&&!this.languages.includes(e)&&this.store.hasLanguageSomeTranslations(e)&&(this.resolvedLanguage=e,this.languages.unshift(e))}}changeLanguage(e,r){this.isLanguageChangingTo=e;const o=wt();this.emit("languageChanging",e);const s=a=>{this.language=a,this.languages=this.services.languageUtils.toResolveHierarchy(a),this.resolvedLanguage=void 0,this.setResolvedLanguage(a)},n=(a,l)=>{l?this.isLanguageChangingTo===e&&(s(l),this.translator.changeLanguage(l),this.isLanguageChangingTo=void 0,this.emit("languageChanged",l),this.logger.log("languageChanged",l)):this.isLanguageChangingTo=void 0,o.resolve((...c)=>this.t(...c)),r&&r(a,(...c)=>this.t(...c))},i=a=>{!e&&!a&&this.services.languageDetector&&(a=[]);const l=$(a)?a:a&&a[0],c=this.store.hasLanguageSomeTranslations(l)?l:this.services.languageUtils.getBestMatchFromCodes($(a)?[a]:a);c&&(this.language||s(c),this.translator.language||this.translator.changeLanguage(c),this.services.languageDetector?.cacheUserLanguage?.(c)),this.loadResources(c,u=>{n(u,c)})};return!e&&this.services.languageDetector&&!this.services.languageDetector.async?i(this.services.languageDetector.detect()):!e&&this.services.languageDetector&&this.services.languageDetector.async?this.services.languageDetector.detect.length===0?this.services.languageDetector.detect().then(i):this.services.languageDetector.detect(i):i(e),o}getFixedT(e,r,o,s){const n=s?.scopeNs,i=(a,l,...c)=>{let u;typeof l!="object"?u=this.options.overloadTranslationOptionHandler([a,l].concat(c)):u={...l},u.lng=u.lng||i.lng,u.lngs=u.lngs||i.lngs;const d=u.ns!==void 0&&u.ns!==null;u.ns=u.ns||i.ns,u.keyPrefix!==""&&(u.keyPrefix=u.keyPrefix||o||i.keyPrefix);const f={...this.options,...u};Array.isArray(n)&&!d&&(f.ns=n),typeof u.keyPrefix=="function"&&(u.keyPrefix=Qe(u.keyPrefix,f));const g=this.options.keySeparator||".";let p;return u.keyPrefix&&Array.isArray(a)?p=a.map(m=>(typeof m=="function"&&(m=Qe(m,f)),`${u.keyPrefix}${g}${m}`)):(typeof a=="function"&&(a=Qe(a,f)),p=u.keyPrefix?`${u.keyPrefix}${g}${a}`:a),this.t(p,u)};return $(e)?i.lng=e:i.lngs=e,i.ns=r,i.keyPrefix=o,i}t(...e){return this.translator?.translate(...e)}exists(...e){return this.translator?.exists(...e)}setDefaultNamespace(e){this.options.defaultNS=e}hasLoadedNamespace(e,r={}){if(!this.isInitialized)return this.logger.warn("hasLoadedNamespace: i18next was not initialized",this.languages),!1;if(!this.languages||!this.languages.length)return this.logger.warn("hasLoadedNamespace: i18n.languages were undefined or empty",this.languages),!1;const o=r.lng||this.resolvedLanguage||this.languages[0],s=this.options?this.options.fallbackLng:!1,n=this.languages[this.languages.length-1];if(o.toLowerCase()==="cimode")return!0;const i=(a,l)=>{const c=this.services.backendConnector.state[`${a}|${l}`];return c===-1||c===0||c===2};if(r.precheck){const a=r.precheck(this,i);if(a!==void 0)return a}return!!(this.hasResourceBundle(o,e)||!this.services.backendConnector.backend||this.options.resources&&!this.options.partialBundledLanguages||i(o,e)&&(!s||i(n,e)))}loadNamespaces(e,r){const o=wt();return this.options.ns?($(e)&&(e=[e]),e.forEach(s=>{this.options.ns.includes(s)||this.options.ns.push(s)}),this.loadResources(s=>{o.resolve(),r&&r(s)}),o):(r&&r(),Promise.resolve())}loadLanguages(e,r){const o=wt();$(e)&&(e=[e]);const s=this.options.preload||[],n=e.filter(i=>!s.includes(i)&&this.services.languageUtils.isSupportedCode(i));return n.length?(this.options.preload=s.concat(n),this.loadResources(i=>{o.resolve(),r&&r(i)}),o):(r&&r(),Promise.resolve())}dir(e){if(e||(e=this.resolvedLanguage||(this.languages?.length>0?this.languages[0]:this.language)),!e)return"rtl";try{const s=new Intl.Locale(e);if(s&&s.getTextInfo){const n=s.getTextInfo();if(n&&n.direction)return n.direction}}catch{}const r=["ar","shu","sqr","ssh","xaa","yhd","yud","aao","abh","abv","acm","acq","acw","acx","acy","adf","ads","aeb","aec","afb","ajp","apc","apd","arb","arq","ars","ary","arz","auz","avl","ayh","ayl","ayn","ayp","bbz","pga","he","iw","ps","pbt","pbu","pst","prp","prd","ug","ur","ydd","yds","yih","ji","yi","hbo","men","xmn","fa","jpr","peo","pes","prs","dv","sam","ckb"],o=this.services?.languageUtils||new Gr(pr());return e.toLowerCase().indexOf("-latn")>1?"ltr":r.includes(o.getLanguagePartFromCode(e))||e.toLowerCase().indexOf("-arab")>1?"rtl":"ltr"}static createInstance(e={},r){const o=new _t(e,r);return o.createInstance=_t.createInstance,o}cloneInstance(e={},r=Ft){const o=e.forkResourceStore;o&&delete e.forkResourceStore;const s={...this.options,...e,isClone:!0},n=new _t(s);if((e.debug!==void 0||e.prefix!==void 0)&&(n.logger=n.logger.clone(e)),["store","services","language"].forEach(a=>{n[a]=this[a]}),n.services={...this.services},n.services.utils={hasLoadedNamespace:n.hasLoadedNamespace.bind(n)},o){const a=Object.keys(this.store.data).reduce((l,c)=>(l[c]={...this.store.data[c]},l[c]=Object.keys(l[c]).reduce((u,d)=>(u[d]={...l[c][d]},u),l[c]),l),{});n.store=new Kr(a,s),n.services.resourceStore=n.store}if(e.interpolation){const l={...pr().interpolation,...this.options.interpolation,...e.interpolation},c={...s,interpolation:l};n.services.interpolator=new Yr(c)}return n.translator=new Mt(n.services,s),n.translator.on("*",(a,...l)=>{n.emit(a,...l)}),n.init(s,r),n.translator.options=s,n.translator.backendConnector.services.utils={hasLoadedNamespace:n.hasLoadedNamespace.bind(n)},n}toJSON(){return{options:this.options,store:this.store,language:this.language,languages:this.languages,resolvedLanguage:this.resolvedLanguage}}}const U=_t.createInstance();U.createInstance;U.dir;U.init;U.loadResources;U.reloadResources;U.use;U.changeLanguage;U.getFixedT;U.t;U.exists;U.setDefaultNamespace;U.hasLoadedNamespace;U.loadNamespaces;U.loadLanguages;var Oo=class extends HTMLElement{async render(t,e){if(!(this.routeId===e?.id&&e?.force===!1)){if(this.routeId=e?.id,this.reset(),t===null)throw new Error("Content is null and cannot be rendered.");if(typeof t!="object")throw new Error("Content is not a valid renderable object.");if(t instanceof HTMLElement)this.replaceChildren(t),this.root=void 0;else if("_$litType$"in t)this.root=xo(t,this);else if("$$typeof"in t){const{createRoot:r}=await _r(async()=>{const{createRoot:o}=await Promise.resolve().then(()=>Ac);return{createRoot:o}},void 0);this.root=r(this),this.root.render(t)}else throw new Error("not supported content type for Outlet rendering.")}}reset(){this.root&&"_$litPart$"in this&&delete this._$litPart$,this.root&&"unmount"in this.root&&this.root.unmount(),this.root=void 0,this.innerHTML=""}};customElements.define("u-outlet",Oo);function jo(t){if(!t)return!1;if(t=t.trim(),/^(?:mailto:|tel:|javascript:)/i.test(t)||t.startsWith("//"))return!0;try{const e=typeof window<"u"?window.location.origin:"http://localhost",r=new URL(t,e);return/^(?:ftp:|ftps:|ws:|wss:)/i.test(r.protocol)?!0:r.origin!==new URL(e).origin}catch{return!1}}function _s(t,e){let r;return e=Cs(e),t.startsWith("http")?r=new URL(t):t.startsWith("/")?r=new URL(t,window.location.origin):t.startsWith("?")?r=new URL(window.location.pathname+t,window.location.origin):t.startsWith("#")?r=new URL(window.location.pathname+window.location.search+t,window.location.origin):r=new URL(Et(e,t),window.location.origin),{href:r.href,origin:r.origin,basepath:e,path:r.href.replace(r.origin,""),pathname:r.pathname,query:new URLSearchParams(r.search),hash:r.hash,params:{},progress:()=>{},metadata:{}}}function Et(...t){return t=t.map(e=>e.replace(/^\/|\/$/g,"")).filter(e=>e.length>0),t.length===0?"/":"/"+t.join("/")}function Cs(t){if(t==="/")return t;let e=new URLPattern({pathname:t+"/*"}),r=e.exec({pathname:window.location.pathname});if(r){const o=r.pathname.input,s=r.pathname.groups?.["0"];return s!==void 0&&s!==""?o.replace("/"+s,""):o.replace(/\/$/,"")}return e=new URLPattern({pathname:`${t}{/}?`}),r=e.exec({pathname:window.location.pathname}),r?r.pathname.input:t}function At(t,e){if(typeof Reflect=="object"&&typeof Reflect.metadata=="function")return Reflect.metadata(t,e)}function at(t,e,r,o){var s=arguments.length,n=s<3?e:o,i;if(typeof Reflect=="object"&&typeof Reflect.decorate=="function")n=Reflect.decorate(t,e,r,o);else for(var a=t.length-1;a>=0;a--)(i=t[a])&&(n=(s<3?i(n):s>3?i(e,r,n):i(e,r))||n);return s>3&&n&&Object.defineProperty(e,r,n),n}var Ot=class extends $r{constructor(...e){super(...e),this.isExternal=!1,this.handleClick=r=>{if(r.defaultPrevented||r.button!==0||r.metaKey||r.ctrlKey||r.shiftKey||r.altKey||this.target&&this.target.toLowerCase()!=="_self")return;const o=this.getBasepath();if(!this.href){r.preventDefault(),this.dispatchPopstate(o,o);return}if(this.isExternal||this.href.startsWith("#"))return;if(r.preventDefault(),this.href.startsWith("?")){const n=window.location.pathname+this.href;this.dispatchPopstate(o,n);return}if(this.href.startsWith("/")){if(!this.href.startsWith(o)){window.location.assign(this.href);return}this.dispatchPopstate(o,this.href);return}const s=Et(o,this.href);this.dispatchPopstate(o,s)}}connectedCallback(){super.connectedCallback(),this.addEventListener("click",this.handleClick)}disconnectedCallback(){this.removeEventListener("click",this.handleClick),super.disconnectedCallback()}willUpdate(e){super.willUpdate(e),e.has("href")&&(this.isExternal=jo(this.href||""))}render(){return y`
      <a
        href=${this.compute(this.href)}
        target=${xe(this.target)}
        rel=${xe(this.rel)}
      >
        <slot></slot>
      </a>
    `}compute(e){const r=this.getBasepath();return e?this.isExternal||e.startsWith("/")||e.startsWith("#")||e.startsWith("?")?e:Et(r,e):window.location.origin+r}dispatchPopstate(e,r){window.history.pushState({basepath:e},"",r),window.dispatchEvent(new PopStateEvent("popstate"))}getBasepath(){return window.history.state?.basepath||"/"}static{this.styles=S`
    :host {
      cursor: pointer;
    }

    a {
      text-decoration: none;

      font-size: inherit;
      font-weight: inherit;
      font-family: inherit;
      color: inherit;
      cursor: inherit;
    }
  `}};at([h({type:String}),At("design:type",String)],Ot.prototype,"target",void 0);at([h({type:String}),At("design:type",String)],Ot.prototype,"rel",void 0);at([h({type:String}),At("design:type",String)],Ot.prototype,"href",void 0);Ot=at([E("u-link")],Ot);var ee=class Po extends Error{constructor(e,r,o){super(r),this.name="RouteError",this.code=e,this.original=o,this.timestamp=new Date().toISOString(),Error.captureStackTrace&&Error.captureStackTrace(this,Po)}},Ss=class extends ee{constructor(t,e){super(404,`Page not found: ${t}`,e)}},eo=class extends ee{constructor(t){super(403,`Access denied: ${t}`)}},Es=class extends ee{constructor(){super("OUTLET_MISSING","Router outlet element not found. Add <u-outlet> to your template.")}},Os=class extends ee{constructor(t){super("CONTENT_LOAD_FAILED","Failed to load route content. Check browser console for details.",t)}},js=class extends ee{constructor(t){super("CONTENT_RENDER_FAILED","Failed to render route component. Check browser console for details.",t)}},Zt=class extends Event{constructor(t,e,r=!1){super(t,{bubbles:!0,composed:!0,cancelable:r}),this.context=e,this.timestamp=new Date().toISOString()}get cancelled(){return this.defaultPrevented}cancel(){this.cancelable&&this.preventDefault()}},Ps=class extends Zt{constructor(t){super("route-begin",t,!1)}},Rs=class extends Zt{constructor(t,e){super("route-progress",t,!1),this.progress=e}},As=class extends Zt{constructor(t){super("route-done",t,!1)}},Ls=class extends Zt{constructor(t,e){super("route-error",t,!1),this.error=e}},to,ro,jt=class extends $r{constructor(e){super(),this.error=e}render(){const e=this.error||this.getDefaultError();return y`
      <div class="icon">${this.getErrorIcon(e.code)}</div>
      <div class="code">${e.code}</div>
      <div class="message">${e.message}</div>
    `}getDefaultError(){return new ee(500,"Something went wrong. Please try again or contact support if the problem persists.")}getErrorIcon(e){switch(String(e)){case"OUTLET_MISSING":return"📦";case"CONTENT_LOAD_FAILED":return"📡";case"CONTENT_RENDER_FAILED":return"🎨";case"ACCESS_DENIED":return"🚫"}switch(typeof e=="string"?parseInt(e):e){case 404:return"🔍";case 403:return"🚫";case 401:return"🔐";case 429:return"⏱️";case 503:return"🛠️";default:return"⚠️"}}static{this.styles=S`
    :host {
      --error-icon-color: #4a5568;
      --error-code-color: #1a202c;
      --error-message-color: #718096;
    }
    :host-context([theme="dark"]) {
      --error-icon-color: #a0aec0;
      --error-code-color: #f7fafc;
      --error-message-color: #cbd5e0;
    }

    @media (prefers-color-scheme: dark) {
      :host {
        --error-icon-color: #a0aec0;
        --error-code-color: #f7fafc;
        --error-message-color: #cbd5e0;
      }
    }

    :host {
      display: flex;
      flex-direction: column;
      justify-content: center;
      align-items: center;
      width: 100%;
      height: 100%;
      text-align: center;
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      overflow: auto;
      user-select: none;
    }

    .icon {
      color: var(--error-icon-color);
      font-size: 6rem;
      opacity: 0.85;
    }

    .code {
      color: var(--error-code-color);
      font-size: 2rem;
      font-weight: 700;
      letter-spacing: -0.5px;
      margin: 1rem 0;
    }

    .message {
      color: var(--error-message-color);
      font-size: 1rem;
      line-height: 1.6;
      max-width: 600px;
    }
  `}};at([h({type:Object}),At("design:type",typeof(ro=typeof ee<"u"&&ee)=="function"?ro:Object)],jt.prototype,"error",void 0);jt=at([E("u-error-page"),At("design:paramtypes",[typeof(to=typeof ee<"u"&&ee)=="function"?to:Object])],jt);function wr(){return window.isSecureContext?window.crypto.randomUUID():window.crypto.getRandomValues(new Uint32Array(1))[0].toString(16)}function er(t,e=!1){if(!e&&t instanceof Oo)return t;const r=t.shadowRoot?[t.shadowRoot,t]:[t];for(const o of r)for(const s of Array.from(o.children)){const n=er(s);if(n)return n}}function Ds(t,e=!1){const r=er(t,e);if(!r)throw new Es;return r}async function oo(t,e=1e4,r=!1){const o=performance.now();for(;performance.now()-o<e;){const s=er(t,r);if(s)return s;t.localName.includes("-")&&await customElements.whenDefined(t.localName),"updateComplete"in t&&await t.updateComplete,await new Promise(n=>requestAnimationFrame(()=>n()))}throw new Error(`Timed out waiting for <u-outlet> inside <${t.tagName.toLowerCase()}>. Ensure that the router root element contains a <u-outlet> child.`)}function Fs(t){const e=t.composedPath()||[];if(e&&e.length){for(const o of e)if(o instanceof Element&&o.tagName==="A")return o}const r=t.target;return r?r.closest("a"):null}function Ro(t,e){for(const r of t)if(r.id||=wr(),r.ignoreCase||=!1,r.index===!0)r.path=new URLPattern({pathname:`${e}{/}?`},{ignoreCase:r.ignoreCase}),r.force||=!0;else{if(typeof r.path=="string"){const o=Et(e,r.path);r.path=new URLPattern({pathname:`${o}{/}?`},{ignoreCase:r.ignoreCase})}else r.path instanceof URLPattern||(r.path=new URLPattern({pathname:`${e}{/}?`},{ignoreCase:r.ignoreCase}));if(r.children&&r.children.length>0){const o=r.path.pathname.replace("{/}?","");r.children=Ro(r.children,o),r.force||=!1}else r.force||=!0}return t}function Ao(t,e){for(const r of t){if(r.index!==!0&&r.children&&r.children.length>0){const o=Ao(r.children,e);if(o.length>0)return[r,...o]}if(r.path instanceof URLPattern){if(r.path.test({pathname:e}))return[r]}else throw new Error("Route path must be an instance of URLPattern, Something wrong in setRoutes function.")}return[]}var Ts=class{constructor(){this._history=new Set,this._processed=new Set}reset(){this._history.clear(),this._processed.clear()}visit(t){return this._history.has(t)?(console.error("Router: Redirect cycle detected:",[...this._history,t].join(" → ")),!0):(this._history.add(t),!1)}enter(t){const e=t.id??(t.path instanceof URLPattern?t.path.pathname:String(t.path));return this._processed.has(e)?!1:(this._processed.add(e),!0)}},Bs=class{constructor(t){this._tracker=new Ts,this.handleWindowPopstate=async e=>{await this.go(window.location.href)},this.handleRootElementClick=async e=>{try{if(e.defaultPrevented||e.button!==0||e.metaKey||e.ctrlKey||e.shiftKey)return;const r=Fs(e);if(!r)return;const o=r.getAttribute("href")||r.href;if(!o||jo(o)||r.hasAttribute("download")||r.getAttribute("rel")==="external"||r.target&&r.target!==""||this._basepath!=="/"&&!new URL(r.href).pathname.startsWith(this._basepath))return;e.preventDefault(),await this.go(r.href)}catch{}},this.destroy(),this._rootElement=t.root,this._basepath=Et(t.basepath||"/"),this._routes=Ro(t.routes||[],this._basepath),this._fallback=t.fallback,this._enter=t.enter,window.addEventListener("popstate",this.handleWindowPopstate),t.useIntercept!==!1&&this._rootElement.addEventListener("click",this.handleRootElementClick),t.initialLoad!==!1&&oo(this._rootElement).then(()=>{this.go(window.location.href)})}destroy(){window.removeEventListener("popstate",this.handleWindowPopstate),this._rootElement?.removeEventListener("click",this.handleRootElementClick),this._requestID=void 0,this._context=void 0}get basepath(){return this._basepath}get routes(){return this._routes}get context(){return this._context}async go(t,e){e?.isRedirect||this._tracker.reset();const r=wr();this._requestID=r;const o=_s(t,this._basepath);if(e?.isRedirect&&this._tracker.visit(o.href))return;const s=e?.isRedirect||e?.replace||o.href===window.location.href,n={basepath:o.basepath,...e?.state};s?window.history.replaceState(n,"",o.href):window.history.pushState(n,"",o.href);let i,a;try{i=Ds(this._rootElement);const l=Ao(this._routes,o.pathname);if(l.length===0)throw new Ss(o.href);const c=l[l.length-1];if(c.path instanceof URLPattern&&(o.params=c.path.exec({pathname:o.pathname})?.pathname.groups||{}),o.progress=u=>{if(this._requestID!==r)return;const d=Math.max(0,Math.min(100,Math.round(u)));window.dispatchEvent(new Rs(o,d))},this._enter&&!e?.isRedirect){const u=await this._enter(o);if(this._requestID!==r)return;if(u===!1)throw new eo(o.pathname);if(typeof u=="string")return void this.go(u,{isRedirect:!0})}this._context=o,window.dispatchEvent(new Ps(o));for(const u of l){if(this._requestID!==r)return;if(o.metadata={...o.metadata,...u.metadata},u.enter&&this._tracker.enter(u)){const f=await u.enter(o);if(this._requestID!==r)return;if(f===!1)throw new eo(o.pathname);if(typeof f=="string")return void this.go(f,{isRedirect:!0})}if(!u.render)continue;let d;try{if(d=await u.render(o),d===!1||d===void 0||d===null)throw new Error("Failed to load content for the route.")}catch(f){throw new Os(f)}try{await i.render(d,{id:u.id,force:u.force})}catch(f){throw new js(f)}"children"in u&&u.children&&u.children.length>0?i=await oo(i,2e3,!0):i=er(i,!0)||i,a=u.title||a}window.dispatchEvent(new As(o))}catch(l){const c=l instanceof ee?l:new ee(l?.status||l?.code||"UNKNOWN_ERROR",l?.message||"An unexpected error occurred",l);window.dispatchEvent(new Ls(o,c)),console.error("Routing error:",c.original||c);try{const u=this._fallback?.render?await this._fallback.render({...o,error:c}):new jt(c);i?i.render(u,{id:wr(),force:!0}):(document.body.innerHTML="",document.body.appendChild(u instanceof Node?u:new jt(c))),a=this._fallback?.title||c.message||"Error"}catch(u){console.error("Failed to render error component:",u),console.error("Original error:",c.original||c)}}finally{document.title=a||document.title}}},Ns=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path d="M8 16a2 2 0 0 0 2-2H6a2 2 0 0 0 2 2m.995-14.901a1 1 0 1 0-1.99 0A5 5 0 0 0 3 6c0 1.098-.5 6-2 7h14c-1.5-1-2-5.902-2-7 0-2.42-1.72-4.44-4.005-4.901"/>\r
</svg>`,Us=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
    <path d="M16 8A8 8 0 1 1 0 8a8 8 0 0 1 16 0m-3.97-3.03a.75.75 0 0 0-1.08.022L7.477 9.417 5.384 7.323a.75.75 0 0 0-1.06 1.06L6.97 11.03a.75.75 0 0 0 1.079-.02l3.992-4.99a.75.75 0 0 0-.01-1.05z"/>\r
</svg>`,zs=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path d="M12.736 3.97a.733.733 0 0 1 1.047 0c.286.289.29.756.01 1.05L7.88 12.01a.733.733 0 0 1-1.065.02L3.217 8.384a.757.757 0 0 1 0-1.06.733.733 0 0 1 1.047 0l3.052 3.093 5.4-6.425z"/>\r
</svg>`,Is=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path fill-rule="evenodd" d="M1.646 4.646a.5.5 0 0 1 .708 0L8 10.293l5.646-5.647a.5.5 0 0 1 .708.708l-6 6a.5.5 0 0 1-.708 0l-6-6a.5.5 0 0 1 0-.708"></path>\r
</svg>`,Ms=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path fill-rule="evenodd" d="M11.354 1.646a.5.5 0 0 1 0 .708L5.707 8l5.647 5.646a.5.5 0 0 1-.708.708l-6-6a.5.5 0 0 1 0-.708l6-6a.5.5 0 0 1 .708 0"/>\r
</svg>`,Vs=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path fill-rule="evenodd" d="M4.646 1.646a.5.5 0 0 1 .708 0l6 6a.5.5 0 0 1 0 .708l-6 6a.5.5 0 0 1-.708-.708L10.293 8 4.646 2.354a.5.5 0 0 1 0-.708"/>\r
</svg>`,Hs=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path fill-rule="evenodd" d="M7.646 4.646a.5.5 0 0 1 .708 0l6 6a.5.5 0 0 1-.708.708L8 5.707l-5.646 5.647a.5.5 0 0 1-.708-.708z"></path>\r
</svg>`,Ws=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
    <path fill-rule="evenodd" d="M2 8a.5.5 0 0 1 .5-.5h11a.5.5 0 0 1 0 1h-11A.5.5 0 0 1 2 8"/>\r
</svg>`,Ks=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
    <path d="M16 8A8 8 0 1 1 0 8a8 8 0 0 1 16 0M8 4a.905.905 0 0 0-.9.995l.35 3.507a.552.552 0 0 0 1.1 0l.35-3.507A.905.905 0 0 0 8 4m.002 6a1 1 0 1 0 0 2 1 1 0 0 0 0-2"/>\r
</svg>`,Gs=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
    <path d="M8.982 1.566a1.13 1.13 0 0 0-1.96 0L.165 13.233c-.457.778.091 1.767.98 1.767h13.713c.889 0 1.438-.99.98-1.767zM8 5c.535 0 .954.462.9.995l-.35 3.507a.552.552 0 0 1-1.1 0L7.1 5.995A.905.905 0 0 1 8 5m.002 6a1 1 0 1 1 0 2 1 1 0 0 1 0-2"/>\r
</svg>`,qs=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path d="M13.359 11.238C15.06 9.72 16 8 16 8s-3-5.5-8-5.5a7 7 0 0 0-2.79.588l.77.771A6 6 0 0 1 8 3.5c2.12 0 3.879 1.168 5.168 2.457A13 13 0 0 1 14.828 8q-.086.13-.195.288c-.335.48-.83 1.12-1.465 1.755q-.247.248-.517.486z"/>\r
  <path d="M11.297 9.176a3.5 3.5 0 0 0-4.474-4.474l.823.823a2.5 2.5 0 0 1 2.829 2.829zm-2.943 1.299.822.822a3.5 3.5 0 0 1-4.474-4.474l.823.823a2.5 2.5 0 0 0 2.829 2.829"/>\r
  <path d="M3.35 5.47q-.27.24-.518.487A13 13 0 0 0 1.172 8l.195.288c.335.48.83 1.12 1.465 1.755C4.121 11.332 5.881 12.5 8 12.5c.716 0 1.39-.133 2.02-.36l.77.772A7 7 0 0 1 8 13.5C3 13.5 0 8 0 8s.939-1.721 2.641-3.238l.708.709zm10.296 8.884-12-12 .708-.708 12 12z"/>\r
</svg>`,Js=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path d="M16 8s-3-5.5-8-5.5S0 8 0 8s3 5.5 8 5.5S16 8 16 8M1.173 8a13 13 0 0 1 1.66-2.043C4.12 4.668 5.88 3.5 8 3.5s3.879 1.168 5.168 2.457A13 13 0 0 1 14.828 8q-.086.13-.195.288c-.335.48-.83 1.12-1.465 1.755C11.879 11.332 10.119 12.5 8 12.5s-3.879-1.168-5.168-2.457A13 13 0 0 1 1.172 8z"/>\r
  <path d="M8 5.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5M4.5 8a3.5 3.5 0 1 1 7 0 3.5 3.5 0 0 1-7 0"/>\r
</svg>`,Xs=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
    <path d="M8 16A8 8 0 1 0 8 0a8 8 0 0 0 0 16m.93-9.412-1 4.705c-.07.34.029.533.304.533.194 0 .487-.07.686-.246l-.088.416c-.287.346-.92.598-1.465.598-.703 0-1.002-.422-.808-1.319l.738-3.468c.064-.293.006-.399-.287-.47l-.451-.081.082-.381 2.29-.287zM8 5.5a1 1 0 1 1 0-2 1 1 0 0 1 0 2"/>\r
</svg>`,Ys=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path fill-rule="evenodd" d="M8 2a.5.5 0 0 1 .5.5v5h5a.5.5 0 0 1 0 1h-5v5a.5.5 0 0 1-1 0v-5h-5a.5.5 0 0 1 0-1h5v-5A.5.5 0 0 1 8 2"/>\r
</svg>`,Qs=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path d="M11.742 10.344a6.5 6.5 0 1 0-1.397 1.398h-.001q.044.06.098.115l3.85 3.85a1 1 0 0 0 1.415-1.414l-3.85-3.85a1 1 0 0 0-.115-.1zM12 6.5a5.5 5.5 0 1 1-11 0 5.5 5.5 0 0 1 11 0"/>\r
</svg>`,Zs=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path d="M3.612 15.443c-.386.198-.824-.149-.746-.592l.83-4.73L.173 6.765c-.329-.314-.158-.888.283-.95l4.898-.696L7.538.792c.197-.39.73-.39.927 0l2.184 4.327 4.898.696c.441.062.612.636.282.95l-3.522 3.356.83 4.73c.078.443-.36.79-.746.592L8 13.187l-4.389 2.256z"/>\r
</svg>`,en=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path d="M2.866 14.85c-.078.444.36.791.746.593l4.39-2.256 4.389 2.256c.386.198.824-.149.746-.592l-.83-4.73 3.522-3.356c.33-.314.16-.888-.282-.95l-4.898-.696L8.465.792a.513.513 0 0 0-.927 0L5.354 5.12l-4.898.696c-.441.062-.612.636-.283.95l3.523 3.356-.83 4.73zm4.905-2.767-3.686 1.894.694-3.957a.56.56 0 0 0-.163-.505L1.71 6.745l4.052-.576a.53.53 0 0 0 .393-.288L8 2.223l1.847 3.658a.53.53 0 0 0 .393.288l4.052.575-2.906 2.77a.56.56 0 0 0-.163.506l.694 3.957-3.686-1.894a.5.5 0 0 0-.461 0z"/>\r
</svg>`,tn=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path d="M9.5 13a1.5 1.5 0 1 1-3 0 1.5 1.5 0 0 1 3 0m0-5a1.5 1.5 0 1 1-3 0 1.5 1.5 0 0 1 3 0m0-5a1.5 1.5 0 1 1-3 0 1.5 1.5 0 0 1 3 0"/>\r
</svg>`,rn=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path d="M3 9.5a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3m5 0a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3m5 0a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3"/>\r
</svg>`,on=`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">\r
  <path d="M2.146 2.854a.5.5 0 1 1 .708-.708L8 7.293l5.146-5.147a.5.5 0 0 1 .708.708L8.707 8l5.147 5.146a.5.5 0 0 1-.708.708L8 8.707l-5.146 5.147a.5.5 0 0 1-.708-.708L7.293 8z"/>\r
</svg>`,Lo="/assets/icons/";function sn(){return Lo}function nn(t){Lo=t}var an=new Map(Object.entries(Object.assign({"../assets/icons/bell-fill.svg":Ns,"../assets/icons/check-circle-fill.svg":Us,"../assets/icons/check-lg.svg":zs,"../assets/icons/chevron-down.svg":Is,"../assets/icons/chevron-left.svg":Ms,"../assets/icons/chevron-right.svg":Vs,"../assets/icons/chevron-up.svg":Hs,"../assets/icons/dash-lg.svg":Ws,"../assets/icons/exclamation-circle-fill.svg":Ks,"../assets/icons/exclamation-triangle-fill.svg":Gs,"../assets/icons/eye-slash.svg":qs,"../assets/icons/eye.svg":Js,"../assets/icons/info-circle-fill.svg":Xs,"../assets/icons/plus-lg.svg":Ys,"../assets/icons/search.svg":Qs,"../assets/icons/star-fill.svg":Zs,"../assets/icons/star.svg":en,"../assets/icons/three-dots-vertical.svg":tn,"../assets/icons/three-dots.svg":rn,"../assets/icons/x-lg.svg":on})).map(([t,e])=>[t.split("/").pop()?.replace(".svg","")||"",e]).filter(([t])=>t!=="")),Q=class{static{this.cache=new Map}constructor(){}static makeKey(t,e){return`${t}:${e}`}static has(t,e){return this.cache.has(this.makeKey(t,e))}static get(t,e){return this.cache.get(this.makeKey(t,e))}static set(t,e,r){this.cache.set(this.makeKey(t,e),r)}static clear(){this.cache.clear()}},lt=class{static{this.libs=new Map}constructor(){}static has(t){return this.libs.has(t)}static register(t,e){this.libs.has(t)?console.warn(`Icon library "${t}" is already registered`):this.libs.set(t,e)}static unregister(t){this.libs.delete(t)}static async resolve(t,e){const r=this.libs.get(t);if(r)return(await r(e))?.trim()}};lt.register("internal",t=>an.get(t));lt.register("tabler",async t=>{if(Q.has("tabler",t))return Q.get("tabler",t);try{const[e,r="outline"]=t.split(":"),o=`https://cdn.jsdelivr.net/npm/@tabler/icons@3.40.0/icons/${r}/${e}.svg`,s=await fetch(o);if(!s.ok)return;const n=await s.text();return Q.set("tabler",t,n),n}catch{return}});lt.register("heroicons",async t=>{if(Q.has("heroicons",t))return Q.get("heroicons",t);try{const[e,r="outline",o="24"]=t.split(":"),s=`https://cdn.jsdelivr.net/npm/heroicons@2.2.0/${o}/${r}/${e}.svg`,n=await fetch(s);if(!n.ok)return;const i=await n.text();return Q.set("heroicons",t,i),i}catch{return}});lt.register("lucide",async t=>{if(Q.has("lucide",t))return Q.get("lucide",t);try{const e=`https://cdn.jsdelivr.net/npm/lucide-static@0.577.0/icons/${t}.svg`,r=await fetch(e);if(!r.ok)return;const o=await r.text();return Q.set("lucide",t,o),o}catch{return}});lt.register("bootstrap",async t=>{if(Q.has("bootstrap",t))return Q.get("bootstrap",t);try{const e=`https://cdn.jsdelivr.net/npm/bootstrap-icons@1.13.1/icons/${t}.svg`,r=await fetch(e);if(!r.ok)return;const o=await r.text();return Q.set("bootstrap",t,o),o}catch{return}});var ln=class{constructor(t){if(window===void 0)throw new Error("BrowserStorage can only be used in a browser environment.");if(t.type==="cookie"&&!("cookieStore"in window))throw new Error("Cookies are not supported in this browser.");this.options=t}makeKey(t){return`${this.options.prefix||""}${t}`}async set(t,e){t=this.makeKey(t);try{if(this.options.type==="localStorage")window.localStorage.setItem(t,e);else if(this.options.type==="cookie")await window.cookieStore.set({name:t,value:e,path:this.options.path,domain:this.options.domain,expires:this.options.expires,sameSite:this.options.sameSite,partitioned:this.options.partitioned});else throw new Error("Unsupported storage type")}catch(r){console.error("[BrowserStorage Error: set]",r)}}async get(t){t=this.makeKey(t);try{if(this.options.type==="localStorage")return window.localStorage.getItem(t);if(this.options.type==="cookie")return(await window.cookieStore.get({name:t}))?.value||null;throw new Error("Unsupported storage type")}catch(e){return console.error("[BrowserStorage Error: get]",e),null}}async remove(t){t=this.makeKey(t);try{if(this.options.type==="localStorage")window.localStorage.removeItem(t);else if(this.options.type==="cookie")await window.cookieStore.delete({name:t});else throw new Error("Unsupported storage type")}catch(e){console.error("[BrowserStorage Error: remove]",e)}}},cn=`:root[theme="dark"] {\r
  color-scheme: dark;\r
\r
  /* Neutral Colors */\r
  --u-neutral-0:     #000000;\r
  --u-neutral-50:    #0A0A0A;\r
  --u-neutral-100:   #121212;\r
  --u-neutral-200:   #1E1E1E;\r
  --u-neutral-300:   #2A2A2A;\r
  --u-neutral-400:   #3D3D3D;\r
  --u-neutral-500:   #525252;\r
  --u-neutral-600:   #6E6E6E;\r
  --u-neutral-700:   #8A8A8A;\r
  --u-neutral-800:   #B0B0B0;\r
  --u-neutral-900:   #D4D4D4;\r
  --u-neutral-1000:  #FFFFFF;\r
\r
  /* Blue Colors */\r
  --u-blue-0:        #0A1E3D;\r
  --u-blue-100:      #1E3A5F;\r
  --u-blue-200:      #2B4F7E;\r
  --u-blue-300:      #3D6B9E;\r
  --u-blue-400:      #5284BE;\r
  --u-blue-500:      #6BA3E3;\r
  --u-blue-600:      #87B8F5;\r
  --u-blue-700:      #A5CDFF;\r
  --u-blue-800:      #C2DEFF;\r
  --u-blue-900:      #DDEEFF;\r
  --u-blue-1000:     #F0F7FF;\r
\r
  /* Green Colors */\r
  --u-green-0:       #0D2818;\r
  --u-green-100:     #1A3F2A;\r
  --u-green-200:     #285639;\r
  --u-green-300:     #3A7450;\r
  --u-green-400:     #4E9268;\r
  --u-green-500:     #66B584;\r
  --u-green-600:     #81D19D;\r
  --u-green-700:     #9FE5B5;\r
  --u-green-800:     #BEF2CD;\r
  --u-green-900:     #DDF9E5;\r
  --u-green-1000:    #F0FDF5;\r
\r
  /* Yellow Colors */\r
  --u-yellow-0:      #2E2200;\r
  --u-yellow-100:    #4D3A00;\r
  --u-yellow-200:    #6B5200;\r
  --u-yellow-300:    #8A6B00;\r
  --u-yellow-400:    #B08900;\r
  --u-yellow-500:    #D4A712;\r
  --u-yellow-600:    #F5C842;\r
  --u-yellow-700:    #FFE066;\r
  --u-yellow-800:    #FFED99;\r
  --u-yellow-900:    #FFF5CC;\r
  --u-yellow-1000:   #FFFBE6;\r
\r
  /* Red Colors */\r
  --u-red-0:         #2E0A0A;\r
  --u-red-100:       #4D1717;\r
  --u-red-200:       #6B2424;\r
  --u-red-300:       #8F3535;\r
  --u-red-400:       #B24848;\r
  --u-red-500:       #D66060;\r
  --u-red-600:       #F57878;\r
  --u-red-700:       #FF9494;\r
  --u-red-800:       #FFB3B3;\r
  --u-red-900:       #FFD2D2;\r
  --u-red-1000:      #FFF0F0;\r
\r
  /* Orange Colors */\r
  --u-orange-0:      #2E1A00;\r
  --u-orange-100:    #4D2E00;\r
  --u-orange-200:    #6B4200;\r
  --u-orange-300:    #8A5A00;\r
  --u-orange-400:    #B07500;\r
  --u-orange-500:    #D49212;\r
  --u-orange-600:    #F5AE42;\r
  --u-orange-700:    #FFC566;\r
  --u-orange-800:    #FFD899;\r
  --u-orange-900:    #FFEBCC;\r
  --u-orange-1000:   #FFF5E6;\r
\r
  /* Teal Colors */\r
  --u-teal-0:        #002018;\r
  --u-teal-100:      #00382B;\r
  --u-teal-200:      #005040;\r
  --u-teal-300:      #006B55;\r
  --u-teal-400:      #00886C;\r
  --u-teal-500:      #1AA88A;\r
  --u-teal-600:      #42C4A5;\r
  --u-teal-700:      #6EDABD;\r
  --u-teal-800:      #9EEAD5;\r
  --u-teal-900:      #CFF5EA;\r
  --u-teal-1000:     #EDFCF7;\r
\r
  /* Cyan Colors */\r
  --u-cyan-0:        #002830;\r
  --u-cyan-100:      #00404D;\r
  --u-cyan-200:      #005A6B;\r
  --u-cyan-300:      #00768A;\r
  --u-cyan-400:      #0094B0;\r
  --u-cyan-500:      #12B4D4;\r
  --u-cyan-600:      #42CFF5;\r
  --u-cyan-700:      #66DFFF;\r
  --u-cyan-800:      #99EBFF;\r
  --u-cyan-900:      #CCF5FF;\r
  --u-cyan-1000:     #E6FAFF;\r
\r
  /* Purple Colors */\r
  --u-purple-0:      #1A0A2E;\r
  --u-purple-100:    #2E174D;\r
  --u-purple-200:    #42246B;\r
  --u-purple-300:    #5A358F;\r
  --u-purple-400:    #7548B2;\r
  --u-purple-500:    #9260D6;\r
  --u-purple-600:    #AE78F5;\r
  --u-purple-700:    #C494FF;\r
  --u-purple-800:    #D8B3FF;\r
  --u-purple-900:    #ECD2FF;\r
  --u-purple-1000:   #F7F0FF;\r
\r
  /* Pink Colors */\r
  --u-pink-0:        #2E0A1A;\r
  --u-pink-100:      #4D172E;\r
  --u-pink-200:      #6B2442;\r
  --u-pink-300:      #8F355A;\r
  --u-pink-400:      #B24875;\r
  --u-pink-500:      #D66092;\r
  --u-pink-600:      #F578AE;\r
  --u-pink-700:      #FF94C4;\r
  --u-pink-800:      #FFB3D8;\r
  --u-pink-900:      #FFD2EB;\r
  --u-pink-1000:     #FFF0F7;\r
\r
  /* Text Colors */\r
  --u-txt-color:             var(--u-neutral-900);\r
  --u-txt-color-inverse:     var(--u-neutral-100);\r
  --u-txt-color-hover:       var(--u-blue-600);\r
  --u-txt-color-active:      var(--u-blue-600);\r
  --u-txt-color-disabled:    var(--u-neutral-500);\r
  --u-txt-color-weak:        var(--u-neutral-700);\r
  --u-txt-color-strong:      var(--u-neutral-1000);\r
\r
  --u-link-txt-color:        var(--u-blue-700);\r
  --u-tooltip-txt-color:     var(--u-neutral-100);\r
\r
  /* Icon Colors */\r
  --u-icon-color:            var(--u-neutral-800);\r
  --u-icon-color-inverse:    var(--u-neutral-100);\r
  --u-icon-color-hover:      var(--u-blue-600);\r
  --u-icon-color-active:     var(--u-blue-600);\r
  --u-icon-color-disabled:   var(--u-neutral-500);\r
\r
  /* Border Colors */\r
  --u-border-color:          var(--u-neutral-400);\r
  --u-border-color-weak:     var(--u-neutral-300);\r
  --u-border-color-strong:   var(--u-neutral-500);\r
\r
  --u-input-border-color:         var(--u-neutral-400);\r
  --u-input-border-color-hover:   var(--u-neutral-500);\r
  --u-input-border-color-focus:   var(--u-blue-600);\r
  --u-input-border-color-invalid: var(--u-red-600);\r
\r
  /* Background Colors */\r
  --u-bg-color:              var(--u-neutral-100);\r
  --u-bg-color-inverse:      var(--u-neutral-0);\r
  --u-bg-color-hover:        var(--u-neutral-300);\r
  --u-bg-color-active:       var(--u-neutral-400);\r
  --u-bg-color-disabled:     var(--u-neutral-100);\r
  \r
  --u-input-bg-color:        var(--u-neutral-200);\r
  --u-panel-bg-color:        var(--u-neutral-200);\r
  --u-overlay-bg-color:      rgba(0, 0, 0, 0.7);\r
  --u-tooltip-bg-color:      rgba(255, 255, 255, 0.85);\r
\r
  /* Shadow Colors */\r
  --u-shadow-color-weaker:   rgba(0, 0, 0, 0.15);\r
  --u-shadow-color-weak:     rgba(0, 0, 0, 0.25);\r
  --u-shadow-color-normal:   rgba(0, 0, 0, 0.35);\r
  --u-shadow-color-strong:   rgba(0, 0, 0, 0.45);\r
  --u-shadow-color-stronger: rgba(0, 0, 0, 0.60);\r
\r
  /* Scrollbar Colors */\r
  --u-scrollbar-color:       var(--u-neutral-500);\r
  --u-scrollbar-color-hover: var(--u-neutral-600);\r
  --u-scrollbar-track-color: transparent;\r
\r
  /* Font Styles */\r
  --u-font-base:    -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans', Helvetica, Arial, sans-serif, 'Apple Color Emoji', 'Segoe UI Emoji';\r
  --u-font-mono:    ui-monospace, 'Cascadia Code', 'Source Code Pro', Menlo, Monaco, 'Courier New', monospace;\r
  --u-font-serif:   'Georgia', 'Times New Roman', Times, serif;\r
  --u-font-display: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans', Helvetica, Arial, sans-serif;\r
  --u-font-modern:  'Inter', 'SF Pro Display', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;\r
  --u-font-rounded: 'Nunito', 'Quicksand', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;\r
}`,un=`:root {\r
  color-scheme: light;\r
\r
  /* Neutral Colors */\r
  --u-neutral-0:     #FFFFFF;\r
  --u-neutral-50:    #FAFAFA;\r
  --u-neutral-100:   #F5F5F5;\r
  --u-neutral-200:   #EEEEEE;\r
  --u-neutral-300:   #E0E0E0;\r
  --u-neutral-400:   #BDBDBD;\r
  --u-neutral-500:   #9E9E9E;\r
  --u-neutral-600:   #757575;\r
  --u-neutral-700:   #616161;\r
  --u-neutral-800:   #424242;\r
  --u-neutral-900:   #212121;\r
  --u-neutral-1000:  #000000;\r
\r
  /* Blue Colors */\r
  --u-blue-0:        #E3F2FD;\r
  --u-blue-100:      #BBDEFB;\r
  --u-blue-200:      #90CAF9;\r
  --u-blue-300:      #64B5F6;\r
  --u-blue-400:      #42A5F5;\r
  --u-blue-500:      #2196F3;\r
  --u-blue-600:      #1E88E5;\r
  --u-blue-700:      #1976D2;\r
  --u-blue-800:      #1565C0;\r
  --u-blue-900:      #0D47A1;\r
  --u-blue-1000:     #062A5E;\r
\r
  /* Green Colors */\r
  --u-green-0:       #E8F5E9;\r
  --u-green-100:     #C8E6C9;\r
  --u-green-200:     #A5D6A7;\r
  --u-green-300:     #81C784;\r
  --u-green-400:     #66BB6A;\r
  --u-green-500:     #4CAF50;\r
  --u-green-600:     #43A047;\r
  --u-green-700:     #388E3C;\r
  --u-green-800:     #2E7D32;\r
  --u-green-900:     #1B5E20;\r
  --u-green-1000:    #0F3A12;\r
\r
  /* Yellow Colors */\r
  --u-yellow-0:      #FFFDE7;\r
  --u-yellow-100:    #FFF9C4;\r
  --u-yellow-200:    #FFF59D;\r
  --u-yellow-300:    #FFF176;\r
  --u-yellow-400:    #FFEE58;\r
  --u-yellow-500:    #FFEB3B;\r
  --u-yellow-600:    #FDD835;\r
  --u-yellow-700:    #FBC02D;\r
  --u-yellow-800:    #F9A825;\r
  --u-yellow-900:    #F57F17;\r
  --u-yellow-1000:   #8A4A00;\r
\r
  /* Red Colors */\r
  --u-red-0:         #FFEBEE;\r
  --u-red-100:       #FFCDD2;\r
  --u-red-200:       #EF9A9A;\r
  --u-red-300:       #E57373;\r
  --u-red-400:       #EF5350;\r
  --u-red-500:       #F44336;\r
  --u-red-600:       #E53935;\r
  --u-red-700:       #D32F2F;\r
  --u-red-800:       #C62828;\r
  --u-red-900:       #B71C1C;\r
  --u-red-1000:      #6B0F0F;\r
\r
  /* Orange Colors */\r
  --u-orange-0:      #FFF3E0;\r
  --u-orange-100:    #FFE0B2;\r
  --u-orange-200:    #FFCC80;\r
  --u-orange-300:    #FFB74D;\r
  --u-orange-400:    #FFA726;\r
  --u-orange-500:    #FF9800;\r
  --u-orange-600:    #FB8C00;\r
  --u-orange-700:    #F57C00;\r
  --u-orange-800:    #EF6C00;\r
  --u-orange-900:    #E65100;\r
  --u-orange-1000:   #8C3100;\r
\r
  /* Teal Colors */\r
  --u-teal-0:        #E0F2F1;\r
  --u-teal-100:      #B2DFDB;\r
  --u-teal-200:      #80CBC4;\r
  --u-teal-300:      #4DB6AC;\r
  --u-teal-400:      #26A69A;\r
  --u-teal-500:      #009688;\r
  --u-teal-600:      #00897B;\r
  --u-teal-700:      #00796B;\r
  --u-teal-800:      #00695C;\r
  --u-teal-900:      #004D40;\r
  --u-teal-1000:     #002E26;\r
\r
  /* Cyan Colors */\r
  --u-cyan-0:        #E0F7FA;\r
  --u-cyan-100:      #B2EBF2;\r
  --u-cyan-200:      #80DEEA;\r
  --u-cyan-300:      #4DD0E1;\r
  --u-cyan-400:      #26C6DA;\r
  --u-cyan-500:      #00BCD4;\r
  --u-cyan-600:      #00ACC1;\r
  --u-cyan-700:      #0097A7;\r
  --u-cyan-800:      #00838F;\r
  --u-cyan-900:      #006064;\r
  --u-cyan-1000:     #003A3D;\r
\r
  /* Purple Colors */\r
  --u-purple-0:      #F3E5F5;\r
  --u-purple-100:    #E1BEE7;\r
  --u-purple-200:    #CE93D8;\r
  --u-purple-300:    #BA68C8;\r
  --u-purple-400:    #AB47BC;\r
  --u-purple-500:    #9C27B0;\r
  --u-purple-600:    #8E24AA;\r
  --u-purple-700:    #7B1FA2;\r
  --u-purple-800:    #6A1B9A;\r
  --u-purple-900:    #4A148C;\r
  --u-purple-1000:   #2C0B54;\r
\r
  /* Pink Colors */\r
  --u-pink-0:        #FCE4EC;\r
  --u-pink-100:      #F8BBD0;\r
  --u-pink-200:      #F48FB1;\r
  --u-pink-300:      #F06292;\r
  --u-pink-400:      #EC407A;\r
  --u-pink-500:      #E91E63;\r
  --u-pink-600:      #D81B60;\r
  --u-pink-700:      #C2185B;\r
  --u-pink-800:      #AD1457;\r
  --u-pink-900:      #880E4F;\r
  --u-pink-1000:     #520830;\r
\r
  /* Text Colors */\r
  --u-txt-color:             var(--u-neutral-900);\r
  --u-txt-color-inverse:     var(--u-neutral-0);\r
  --u-txt-color-hover:       var(--u-blue-600);\r
  --u-txt-color-active:      var(--u-blue-600);\r
  --u-txt-color-disabled:    var(--u-neutral-400);\r
  --u-txt-color-weak:        var(--u-neutral-500);\r
  --u-txt-color-strong:      var(--u-neutral-1000);\r
\r
  --u-link-txt-color:        var(--u-blue-700);\r
  --u-tooltip-txt-color:     var(--u-neutral-0);\r
\r
  /* Icon Colors */\r
  --u-icon-color:            var(--u-neutral-700);\r
  --u-icon-color-inverse:    var(--u-neutral-0);\r
  --u-icon-color-hover:      var(--u-blue-600);\r
  --u-icon-color-active:     var(--u-blue-600);\r
  --u-icon-color-disabled:   var(--u-neutral-400);\r
\r
  /* Border Colors */\r
  --u-border-color:          var(--u-neutral-300);\r
  --u-border-color-weak:     var(--u-neutral-200);\r
  --u-border-color-strong:   var(--u-neutral-400);\r
\r
  --u-input-border-color:         var(--u-neutral-300);\r
  --u-input-border-color-hover:   var(--u-neutral-400);\r
  --u-input-border-color-focus:   var(--u-blue-600);\r
  --u-input-border-color-invalid: var(--u-red-600);\r
\r
  /* Background Colors */\r
  --u-bg-color:              var(--u-neutral-0);\r
  --u-bg-color-inverse:      var(--u-neutral-900);\r
  --u-bg-color-hover:        var(--u-neutral-100);\r
  --u-bg-color-active:       var(--u-neutral-200);\r
  --u-bg-color-disabled:     var(--u-neutral-50);\r
 \r
  --u-input-bg-color:        var(--u-neutral-0);\r
  --u-panel-bg-color:        var(--u-neutral-0);\r
  --u-overlay-bg-color:      rgba(0, 0, 0, 0.5);\r
  --u-tooltip-bg-color:      rgba(0, 0, 0, 0.75);\r
\r
  /* Shadow Colors */\r
  --u-shadow-color-weaker:   rgba(0, 0, 0, 0.04);\r
  --u-shadow-color-weak:     rgba(0, 0, 0, 0.08);\r
  --u-shadow-color-normal:   rgba(0, 0, 0, 0.12);\r
  --u-shadow-color-strong:   rgba(0, 0, 0, 0.16);\r
  --u-shadow-color-stronger: rgba(0, 0, 0, 0.24);\r
\r
  /* Scrollbar Colors */\r
  --u-scrollbar-color:       var(--u-neutral-400);\r
  --u-scrollbar-color-hover: var(--u-neutral-500);\r
  --u-scrollbar-track-color: transparent;\r
\r
  /* Font Styles */\r
  --u-font-base:    -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans', Helvetica, Arial, sans-serif, 'Apple Color Emoji', 'Segoe UI Emoji';\r
  --u-font-mono:    ui-monospace, 'Cascadia Code', 'Source Code Pro', Menlo, Monaco, 'Courier New', monospace;\r
  --u-font-serif:   'Georgia', 'Times New Roman', Times, serif;\r
  --u-font-display: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans', Helvetica, Arial, sans-serif;\r
  --u-font-modern:  'Inter', 'SF Pro Display', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;\r
  --u-font-rounded: 'Nunito', 'Quicksand', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;\r
}`,dn=Object.entries(Object.assign({"../assets/styles/dark.css":cn,"../assets/styles/light.css":un})).map(([t,e])=>[t.split("/").pop()?.replace(".css","")||"",e]).filter(([t])=>t!==""),so=class{static{this.STORAGE_THEME_KEY="theme"}static{this.storage=null}static{this._isInitialized=!1}static{this._isDebugMode=!1}constructor(){}static get isInitialized(){return this._isInitialized}static get isDebugMode(){return this._isDebugMode}static async init(t){if(this._isDebugMode=t?.debug||!1,this.log("init called",{options:t}),t?.store?(this.storage=new ln(t.store),this.log("store option provided, initializing BrowserStorage")):(this.storage=null,this.log("no store option provided, skipping BrowserStorage initialization")),t?.useBuiltIn??!0){this.log("Import enabled: loading styles via internal assets");for(let[r,o]of dn){const s=document.createElement("style");if(s.setAttribute("data-name",r),s.textContent=o,document.head.querySelector(`style[data-name="${r}"]`)){this.log("style already present, skipping",r);continue}document.head.appendChild(s),this.log("appended style to head",r)}}else document.head.querySelectorAll("style[data-name]").forEach(r=>r.remove()),this.log("Import disabled: removed internal styles from head");let e=t?.default??"system";if(this.storage!==null){const r=await this.storage.get(this.STORAGE_THEME_KEY);r?(e=r,this.log("loaded theme from storage",e)):this.log("no saved theme in storage, using default",e)}this.set(e),this._isInitialized=!0,this.log("theme initialized")}static get(){switch(document.documentElement.getAttribute("data-theme")){case"system":return"system";case"light":return"light";case"dark":return"dark";default:return}}static set(t){try{const e=window.matchMedia("(prefers-color-scheme: dark)");if(t==="system")document.documentElement.setAttribute("data-theme","system"),e.addEventListener("change",this.handleSystemThemeChanged),this.handleSystemThemeChanged(),this.log("system theme applied (data-theme=system)");else if(t==="dark")e.removeEventListener("change",this.handleSystemThemeChanged),document.documentElement.setAttribute("data-theme","dark"),document.documentElement.setAttribute("theme","dark"),this.log("dark theme applied (data-theme=dark, theme=dark)");else if(t==="light")e.removeEventListener("change",this.handleSystemThemeChanged),document.documentElement.setAttribute("data-theme","light"),document.documentElement.setAttribute("theme","light"),this.log("light theme applied (data-theme=light, theme=light)");else throw new Error(`Invalid theme: ${t}. Use 'light', 'dark', or 'system'.`)}catch(e){this.log("Error applying theme:",e)}this.storage!==null&&(this.storage.set(this.STORAGE_THEME_KEY,t),this.log("saved theme to storage",t))}static{this.handleSystemThemeChanged=t=>{if(document.documentElement.getAttribute("data-theme")==="system"){const e=window.matchMedia("(prefers-color-scheme: dark)").matches;document.documentElement.setAttribute("theme",e?"dark":"light"),this.log("system theme changed, applied",e?"dark":"light")}}}static log(...t){this._isDebugMode&&console.log("[theme]",...t)}},hn=S`
  :host {
    color: var(--u-txt-color, inherit);
    font-family: var(--u-font-base, inherit);
    box-sizing: border-box;
    overflow-wrap: anywhere;
  }

  :host *,
  :host *::before,
  :host *::after {
    box-sizing: inherit;
    overflow-wrap: inherit;
  }

  /* Focus Styles */
  :host(:focus-visible) {
    outline: 2px solid var(--u-blue-500);
    outline-offset: 2px;
  }
  :focus-visible {
    outline: 2px solid var(--u-blue-500);
    outline-offset: 2px;
  }

  /* Hidden Attribute */
  :host([hidden]) {
    display: none !important;
  }
  [hidden] {
    display: none !important;
  }

  /* Scrollbar Styles */
  :host([scrollable]) {
    scrollbar-width: thin;
    scrollbar-color: var(--u-scrollbar-color) var(--u-scrollbar-track-color);
  }
  :host([scrollable])::-webkit-scrollbar {
    width: 8px;
    height: 8px;
  }
  :host([scrollable])::-webkit-scrollbar-thumb {
    background: var(--u-scrollbar-color);
  }
  :host([scrollable])::-webkit-scrollbar-track {
    background: var(--u-scrollbar-track-color);
  }
  [scrollable] {
    scrollbar-width: thin;
    scrollbar-color: var(--u-scrollbar-color) var(--u-scrollbar-track-color);
  }
  [scrollable]::-webkit-scrollbar {
    width: 8px;
    height: 8px;
  }
  [scrollable]::-webkit-scrollbar-thumb {
    background: var(--u-scrollbar-color);
  }
  [scrollable]::-webkit-scrollbar-track {
    background: var(--u-scrollbar-track-color);
  }
`,j=class extends $r{static{this.styles=hn}fire(t,e){return this.dispatchEvent(new CustomEvent(t,{bubbles:!0,composed:!0,cancelable:!0,...e}))}relay(t,e){t.stopImmediatePropagation();const r=t.constructor;return this.dispatchEvent(new r(t.type,{...t,...e}))}replace(t,e){if(!this.renderRoot)return;Array.from(this.renderRoot.childNodes).forEach(o=>{o instanceof HTMLStyleElement||o.parentNode?.removeChild(o)});const r=document.createElement("div");r.style.display="contents",this.renderRoot.appendChild(r),xo(t,r,e)}};function v(t,e){if(typeof Reflect=="object"&&typeof Reflect.metadata=="function")return Reflect.metadata(t,e)}function b(t,e,r,o){var s=arguments.length,n=s<3?e:o,i;if(typeof Reflect=="object"&&typeof Reflect.decorate=="function")n=Reflect.decorate(t,e,r,o);else for(var a=t.length-1;a>=0;a--)(i=t[a])&&(n=(s<3?i(n):s>3?i(e,r,n):i(e,r))||n);return s>3&&n&&Object.defineProperty(e,r,n),n}var pn=S`
  :host {
    display: inline-flex;
    color: inherit;
    font-size: inherit;
  }

  svg {
    width: 1em;
    height: 1em;
  }
`,Pt=class extends j{static{this.styles=[super.styles,pn]}render(){return this.src?Br(fetch(this.src).then(e=>e.text()).then(e=>e?rt(this.sanitize(e)):M),M):this.name?Br(this.resolve(this.name).then(e=>e?rt(e):M),M):M}async resolve(e){let r;if(this.lib)r=await lt.resolve(this.lib,e);else try{const o=`${sn().replace(/\/$/,"")}/${e}.svg`,s=await fetch(o);s.ok&&(r=await s.text())}catch(o){console.error(o)}return this.sanitize(r)}sanitize(e){if(!e)return;const r=e.trim();try{const o=new DOMParser().parseFromString(r,"image/svg+xml");if(o.querySelector("parsererror"))return;const s=o.documentElement;return s?.tagName.toLowerCase()!=="svg"?void 0:(s.setAttribute("part","svg"),s.getAttribute("stroke")!=="currentColor"&&s.setAttribute("fill","currentColor"),s.outerHTML)}catch{return}}};b([h({type:String}),v("design:type",String)],Pt.prototype,"src",void 0);b([h({type:String}),v("design:type",Object)],Pt.prototype,"lib",void 0);b([h({type:String}),v("design:type",String)],Pt.prototype,"name",void 0);Pt=b([E("u-icon")],Pt);var fn=S`
  :host {
    --spinner-track-width: 0.125em;
    --spinner-track-color: var(--u-neutral-200);
    --spinner-indicator-color: var(--u-neutral-800);
    --spinner-indicator-speed: 2s;
  }

  /* Color variants */
  :host([color="blue"]) { --spinner-indicator-color: var(--u-blue-600); }
  :host([color="green"]) { --spinner-indicator-color: var(--u-green-600); }
  :host([color="yellow"]) { --spinner-indicator-color: var(--u-yellow-600); }
  :host([color="red"]) { --spinner-indicator-color: var(--u-red-600); }
  :host([color="orange"]) { --spinner-indicator-color: var(--u-orange-600); }
  :host([color="teal"]) { --spinner-indicator-color: var(--u-teal-600); }
  :host([color="cyan"]) { --spinner-indicator-color: var(--u-cyan-600); }
  :host([color="purple"]) { --spinner-indicator-color: var(--u-purple-600); }
  :host([color="pink"]) { --spinner-indicator-color: var(--u-pink-600); }

  :host {
    display: inline-flex;
    flex-direction: column;
    align-items: center;
    gap: 0.25em;
    font-size: inherit;
  }

  .spinner {
    flex: 0 0 auto;
    width: 1em;
    height: 1em;
  }
  .spinner circle {
    fill: none;
    stroke-width: var(--spinner-track-width);
    r: calc(0.5em - var(--spinner-track-width) / 2);
    cx: 0.5em;
    cy: 0.5em;
  }

  .track {
    stroke: var(--spinner-track-color);
    transform-origin: 0% 0%;
  }

  .indicator {
    stroke: var(--spinner-indicator-color);
    stroke-linecap: round;
    stroke-dasharray: 150% 75%;
    transform-origin: 50% 50%;
    animation: spin var(--spinner-indicator-speed) linear infinite;
  }

  .label {
    font-size: 0.5em;
    color: var(--spinner-indicator-color);
    user-select: none;
  }

  @keyframes spin {
    0% {
      transform: rotate(0deg);
      stroke-dasharray: 0.05em, 3em;
    }

    50% {
      transform: rotate(450deg);
      stroke-dasharray: 1.375em, 1.375em;
    }

    100% {
      transform: rotate(1080deg);
      stroke-dasharray: 0.05em, 3em;
    }
  }
`,Vt=class extends j{constructor(...e){super(...e),this.hasLabel=!1}static{this.styles=[super.styles,fn]}render(){return y`
      <svg class="spinner" part="svg">
        <circle class="track"></circle>
        <circle class="indicator"></circle>
      </svg>

      <span class="label" part="label" ?hidden=${!this.hasLabel}>
        <slot @slotchange=${this.handleSlotChange}></slot>
      </span>
    `}handleSlotChange(e){const r=e.target.assignedNodes({flatten:!0});this.hasLabel=r.some(o=>o.nodeType===Node.ELEMENT_NODE||o.nodeType===Node.TEXT_NODE&&o.textContent?.trim()!=="")}};b([h({type:String,reflect:!0}),v("design:type",Object)],Vt.prototype,"color",void 0);b([z(),v("design:type",Object)],Vt.prototype,"hasLabel",void 0);Vt=b([E("u-spinner")],Vt);var gn=S`
  :host {
    position: relative;
    display: inline-flex;

    font-size: 14px;
    font-weight: 500;
    line-height: 1.5;
    padding: 0.5em;
    border: 1px solid transparent;
    border-radius: 6px;
    background-color: transparent;

    transition: all 0.2s ease;
    overflow: hidden;
    user-select: none;
    cursor: pointer;
  }

  /* === States === */
  :host(:active) {
    transform: translateY(1px);
  }
  :host([disabled]) {
    opacity: 0.5;
    pointer-events: none;
    cursor: not-allowed;
  }
  :host([loading]) {
    opacity: 0.8;
    pointer-events: none;
    cursor: wait;
  }
  :host([loading]) button,
  :host([loading]) a {
    visibility: hidden;
  }
  :host([rounded]) {
    border-radius: 9999px;
  }
  :host([has-spinner]) u-spinner {
    display: none;
  }

  /* === Variant styles === */

  /* solid: 강한 채움 (neutral) */
  :host([variant="solid"]) {
    color: #fff;
    background-color: var(--u-neutral-600, #525252);
    border-color: var(--u-neutral-600, #525252);
  }
  :host([variant="solid"]:hover) {
    background-color: var(--u-neutral-700, #404040);
    border-color: var(--u-neutral-700, #404040);
  }
  :host([variant="solid"]:active) {
    background-color: var(--u-neutral-800, #262626);
    border-color: var(--u-neutral-800, #262626);
  }

  /* surface: 채우기 + 경계 */
  :host([variant="surface"]) {
    color: var(--u-txt-color);
    background-color: var(--u-neutral-100, #f5f5f5);
    border-color: var(--u-neutral-300, #d4d4d4);
  }
  :host([variant="surface"]:hover) {
    background-color: var(--u-neutral-200, #e5e5e5);
    border-color: var(--u-neutral-400, #a3a3a3);
  }
  :host([variant="surface"]:active) {
    background-color: var(--u-neutral-300, #d4d4d4);
    border-color: var(--u-neutral-500, #737373);
  }

  /* filled: 채우기만 */
  :host([variant="filled"]) {
    color: var(--u-txt-color);
    background-color: var(--u-neutral-100, #f5f5f5);
    border-color: transparent;
  }
  :host([variant="filled"]:hover) {
    background-color: var(--u-neutral-200, #e5e5e5);
  }
  :host([variant="filled"]:active) {
    background-color: var(--u-neutral-300, #d4d4d4);
  }

  /* outlined: 경계만 */
  :host([variant="outlined"]) {
    color: var(--u-txt-color);
    border-color: var(--u-neutral-300, #d4d4d4);
    background-color: transparent;
  }
  :host([variant="outlined"]:hover) {
    border-color: var(--u-neutral-400, #a3a3a3);
    background-color: var(--u-neutral-50, #fafafa);
  }
  :host([variant="outlined"]:active) {
    border-color: var(--u-neutral-500, #737373);
    background-color: var(--u-neutral-100, #f5f5f5);
  }

  /* ghost: transparent */
  :host([variant="ghost"]) {
    color: var(--u-txt-color);
    border-color: transparent;
    background-color: transparent;
  }
  :host([variant="ghost"]:hover) {
    background-color: var(--u-bg-color-hover);
  }
  :host([variant="ghost"]:active) {
    background-color: var(--u-bg-color-active);
  }

  /* link: blue 링크 스타일 */
  :host([variant="link"]) {
    color: var(--u-blue-500, #3b82f6);
    border-color: transparent;
    background-color: transparent;
    padding-left: 0;
    padding-right: 0;
  }
  :host([variant="link"]:hover) {
    color: var(--u-blue-600, #2563eb);
    text-decoration: underline;
  }
  :host([variant="link"]:active) {
    color: var(--u-blue-700, #1d4ed8);
  }

  /* === Inner === */
  button, a {
    all: unset;
    width: 100%;
    display: inline-flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
  }
  a {
    text-decoration: none;
    color: inherit;
  }
  a[disabled] {
    pointer-events: none;
  }

  /* === Slots === */
  ::slotted(*) {
    color: inherit;
    font-size: inherit;
  }
  ::slotted([slot="prefix"]) {
    margin-right: 0.5em;
  }
  ::slotted([slot="suffix"]) {
    margin-left: 0.5em;
  }

  .content {
    flex: 1 0 auto;
    min-width: 0;
    line-height: 1.5;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* === Mask (로딩 오버레이) === */
  .mask {
    position: absolute;
    z-index: 100;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;

    display: flex;
    align-items: center;
    justify-content: center;

    padding: inherit;
    font-size: inherit;
    border-radius: inherit;
    background-color: inherit;
    pointer-events: none;
  }
`,X=class extends j{constructor(...e){super(...e),this.variant="solid",this.rounded=!1,this.disabled=!1,this.loading=!1,this.type="button",this.handleClick=r=>{if(this.disabled||this.loading){r.preventDefault(),r.stopImmediatePropagation();return}this.type==="submit"?this.form?.requestSubmit():this.type==="reset"&&this.form?.reset()},this.handleSpinnerSlotChange=r=>{const o=r.target.assignedNodes().length>0;this.toggleAttribute("has-spinner",o)}}static{this.styles=[super.styles,gn]}static{this.formAssociated=!0}set form(e){e?this.setAttribute("form",e):this.removeAttribute("form")}get form(){return this.internals?.form??null}connectedCallback(){super.connectedCallback(),"attachInternals"in this&&(this.internals=this.attachInternals()),this.addEventListener("click",this.handleClick)}disconnectedCallback(){this.removeEventListener("click",this.handleClick),super.disconnectedCallback()}render(){return this.href?y`
        <a part="link"
          ?disabled=${this.disabled||this.loading}
          tabindex=${this.disabled||this.loading?-1:0}
          href=${xe(this.disabled||this.loading?void 0:this.href)}
          download=${xe(this.download)}
          target=${xe(this.target)}
          rel=${xe(this.rel)}
        >
          ${this.renderContent()}
        </a>
        ${this.renderMask()}
      `:y`
      <button part="button"
        type=${this.type}
        ?disabled=${this.disabled||this.loading}
      >
        ${this.renderContent()}
      </button>
      ${this.renderMask()}
    `}renderContent(){return y`
      <slot name="prefix"></slot>
      <div class="content" part="content">
        <slot></slot>
      </div>
      <slot name="suffix"></slot>
    `}renderMask(){return y`
      <div class="mask" part="mask" ?hidden=${!this.loading}>
        <u-spinner></u-spinner>
        <slot name="spinner" @slotchange=${this.handleSpinnerSlotChange}></slot>
      </div>
    `}};b([h({type:String,reflect:!0}),v("design:type",Object)],X.prototype,"variant",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Object)],X.prototype,"rounded",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Object)],X.prototype,"disabled",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Object)],X.prototype,"loading",void 0);b([h({type:String}),v("design:type",Object)],X.prototype,"type",void 0);b([h({type:String}),v("design:type",String)],X.prototype,"href",void 0);b([h({type:String}),v("design:type",String)],X.prototype,"target",void 0);b([h({type:String}),v("design:type",String)],X.prototype,"rel",void 0);b([h({type:String}),v("design:type",String)],X.prototype,"download",void 0);b([h({type:String,reflect:!0}),v("design:type",String)],X.prototype,"name",void 0);b([h({type:String,reflect:!0}),v("design:type",String)],X.prototype,"value",void 0);X=b([E("u-button")],X);var mn=S`
  /* === Status Colors === */
  :host {
    --alert-icon-color: var(--u-neutral-700);
    --alert-border-color: var(--u-neutral-300);
    --alert-background-color: var(--u-neutral-200);
  }
  :host([status="error"]) {
    --alert-icon-color: var(--u-red-700);
    --alert-border-color: var(--u-red-300);
    --alert-background-color: var(--u-red-200);
  }
  :host([status="warning"]) {
    --alert-icon-color: var(--u-yellow-700);
    --alert-border-color: var(--u-yellow-300);
    --alert-background-color: var(--u-yellow-200);
  }
  :host([status="info"]) {
    --alert-icon-color: var(--u-blue-700);
    --alert-border-color: var(--u-blue-300);
    --alert-background-color: var(--u-blue-200);
  }
  :host([status="success"]) {
    --alert-icon-color: var(--u-green-700);
    --alert-border-color: var(--u-green-300);
    --alert-background-color: var(--u-green-200);
  }
  :host([status="notice"]) {
    --alert-icon-color: var(--u-neutral-700);
    --alert-border-color: var(--u-neutral-300);
    --alert-background-color: var(--u-neutral-200);
  }

  :host {
    display: block;
    width: fit-content;
    min-width: 200px;
    max-width: 100%;
    max-height: 50vh;
    /*
     * 내부 여백은 .container 에 둔다(:host 아님).
     * 소비앱의 light-DOM 리셋(예: Tailwind preflight 의 *{padding:0})은
     * 호스트 엘리먼트의 :host padding 을 덮어버리므로, Shadow DOM 내부
     * 엘리먼트(.container)에 패딩을 두어 외부 리셋으로부터 보호한다.
     */
    border-radius: 8px;
    box-shadow: 0 4px 12px var(--u-shadow-color-normal);
    
    opacity: 0;
    transform: scale(0.8);
    visibility: hidden;
    pointer-events: none;
    transition: 
      visibility 0s 0.2s,
      opacity 0.2s ease,
      transform 0.2s ease-out;
  }
  :host([open]) {
    opacity: 1;
    transform: scale(1);
    visibility: visible;
    pointer-events: auto;
    transition-delay: 0s;
  }

  /* === Variant Styles === */
  :host([variant="solid"]) {
    border: 1px solid var(--alert-border-color);
    background-color: var(--alert-background-color);
  }
  :host([variant="filled"]) {
    border: 1px solid transparent;
    background-color: var(--alert-background-color);
  }
  :host([variant="outlined"]) {
    border: 1px solid var(--alert-border-color);
    background-color: transparent;
  }
  /* From https://css.glass */
  :host([variant="glass"]) {
    background: rgba(255, 255, 255, 0.2);
    border-radius: 16px;
    box-shadow: 0 4px 30px rgba(0, 0, 0, 0.1);
    backdrop-filter: blur(5px);
    -webkit-backdrop-filter: blur(5px);
    border: 1px solid rgba(255, 255, 255, 0.3);
  }

  .container {
    display: flex;
    flex-direction: column;
    overflow: hidden;
    padding: 12px 16px;
  }

  .header {
    width: 100%;
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 12px;
    margin-bottom: 4px;
    font-size: 16px;
    user-select: none;
  }
  .header .icon {
    flex-shrink: 0;
    color: var(--alert-icon-color);
  }
  .header .title {
    flex-grow: 1;
    font-weight: 600;
    line-height: 2;
  }
  .header .close-btn {
    flex-shrink: 0;
    padding: 4px;
    font-size: inherit;
    border-radius: 4px;
  }

  .content {
    font-size: 14px;
    font-weight: 300;
    line-height: 1.5;
    overflow-y: auto;
  }

  .footer {
    display: inline-block;
  }
`,ke=class extends j{constructor(...e){super(...e),this.open=!1,this.closable=!1,this.variant="solid",this.title="",this.duration=0}static{this.styles=[super.styles,mn]}updated(e){super.updated(e),e.has("open")&&this.setTimer(this.open)}render(){return y`
      <div class="container" part="container">
        <div class="header" part="header">
          <u-icon class="icon" part="icon"
            ?hidden=${!this.status}
            lib="internal"
            name=${this.mapIcon(this.status)}
          ></u-icon>
          <div class="title" part="title">
            ${this.title||this.status?.toUpperCase()||"MESSAGE"}
          </div>
          <u-button class="close-btn" part="close-btn"
            variant="ghost"
            ?hidden=${!this.closable}
            @click=${this.hide}>
            <u-icon lib="internal" name="x-lg"></u-icon>
          </u-button>
        </div>
        <div class="content" part="content" scrollable>
          <slot></slot>
        </div>
        <div class="footer" part="footer">
          <slot name="footer"></slot>
        </div>
      </div>
    `}show(){return this.open?!0:this.fire("show")?(this.open=!0,!0):!1}hide(){return this.open?this.fire("hide")?(this.open=!1,!0):!1:!0}mapIcon(e){switch(e){case"error":return"exclamation-circle-fill";case"warning":return"exclamation-triangle-fill";case"success":return"check-circle-fill";case"info":return"info-circle-fill";case"notice":return"bell-fill";default:return"bell-fill"}}setTimer(e){e?this.duration&&this.duration>0&&(this.timeoutId=window.setTimeout(()=>{this.hide()},this.duration)):clearTimeout(this.timeoutId)}};b([h({type:Boolean,reflect:!0}),v("design:type",Boolean)],ke.prototype,"open",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Boolean)],ke.prototype,"closable",void 0);b([h({type:String,reflect:!0}),v("design:type",Object)],ke.prototype,"variant",void 0);b([h({type:String,reflect:!0}),v("design:type",Object)],ke.prototype,"status",void 0);b([h({type:String}),v("design:type",String)],ke.prototype,"title",void 0);b([h({type:Number}),v("design:type",Number)],ke.prototype,"duration",void 0);ke=b([E("u-alert")],ke);var xt=class{static{this.containers=new Map}static{this.elements=new Set}constructor(){}static async message(t,e){return this.show(void 0,t,e)}static async notice(t,e){return this.show("notice",t,e)}static async info(t,e){return this.show("info",t,e)}static async success(t,e){return this.show("success",t,e)}static async warning(t,e){return this.show("warning",t,e)}static async error(t,e){return this.show("error",t,e)}static async show(t,e,r){const o=new ke;o.status=t,o.innerHTML=e||"",o.variant=r?.variant||"solid",o.title=r?.title||"",o.closable=r?.closable??!0,o.duration=r?.duration&&r.duration>0?r.duration:4e3,this.elements.add(o);const s=r?.position||"top-right",n=r?.target||document.body,i=this.getOrCreateContainer(s,n);i.appendChild(o),await o.updateComplete,o.show(),o.addEventListener("hide",async()=>{if(await new Promise(a=>setTimeout(a,200)),o.remove(),this.elements.delete(o),!i.hasChildNodes()){const a=this.getContainerKey(s,n);i.remove(),this.containers.delete(a)}})}static getContainerKey(t,e){return`${t}@${e===document.body?"body":e.id||`el-${Date.now()}`}`}static getOrCreateContainer(t,e){const r=this.getContainerKey(t,e);let o=this.containers.get(r);if(o)return o;o=document.createElement("div"),o.style.zIndex="9999",o.style.display="flex",o.style.gap="10px";const s=e!==document.body;s?(o.style.position="absolute",getComputedStyle(e).position==="static"&&(e.style.position="relative")):o.style.position="fixed";const n=[];return t.startsWith("top")?(o.style.top=s?"8px":"20px",o.style.flexDirection="column"):t.startsWith("bottom")?(o.style.bottom=s?"8px":"20px",o.style.flexDirection="column-reverse"):(o.style.top="50%",o.style.flexDirection="column",n.push("translateY(-50%)")),t.endsWith("left")?(o.style.left=s?"8px":"20px",o.style.alignItems="flex-start"):t.endsWith("right")?(o.style.right=s?"8px":"20px",o.style.alignItems="flex-end"):(o.style.left="50%",o.style.alignItems="center",n.unshift("translateX(-50%)")),o.style.transform=n.length?n.join(" "):"",e.appendChild(o),this.containers.set(r,o),o}};class bn{constructor(e){this._breakpoints=e.breakpoints,this._observer=new ResizeObserver(o=>{const s=o[0],n=this.calculate(s.contentRect.width);this._size!==n&&(this._size=n,window.dispatchEvent(new CustomEvent("screen-resize",{detail:{size:this._size}})))}),this._observer.observe(e.element);const r=e.element.getBoundingClientRect();this._size=this.calculate(r.width),window.dispatchEvent(new CustomEvent("screen-resize",{detail:{size:this._size}}))}destroy(){this._observer&&this._observer.disconnect()}get(){return this._size}calculate(e){const[r,o]=this._breakpoints;return e<r?"small":e<o?"medium":"large"}}class Ye{constructor(){}static get instance(){return Ye._instance||(Ye._instance=new Ye),Ye._instance}get config(){return this._config}get router(){return this._router}get screen(){return this._screen?.get()}get theme(){return so}get i18n(){return U}async load(e){if(this.unload(),this._config=e,await so.init(e.theme),e.iconBasepath&&nn(e.iconBasepath),e.i18n){for(const s of e.i18n.plugins||[])U.use(s);await U.init(e.i18n)}const r=e.root||document.body;this._layout=await this.createLayout(r,e.layout),this._screen=new bn({element:r,breakpoints:e.layout.breakpoints||[768,1024]});const o=document.createElement("u-outlet");this._layout.appendChild(o),this._router=new Bs({root:this._layout,basepath:e.basepath,routes:e.routes,fallback:e.fallback})}unload(){this._screen&&(this._screen.destroy(),this._screen=void 0),this._layout&&(this._layout.remove(),this._layout=void 0),this._router&&(this._router.destroy(),this._router=void 0),this._config&&(this._config=void 0)}navigate(e){this._router?.go(e)}async notice(e,r){await xt.notice(e,{...r})}async info(e,r){await xt.info(e,{...r})}async warning(e,r){await xt.warning(e,{...r})}async success(e,r){await xt.success(e,{...r})}async error(e,r){await xt.error(e,{...r})}async createLayout(e,r){e===document.body&&(document.body.style.margin="0",document.body.style.width="100vw",document.body.style.height="100vh");let o;if(r.type==="sidebar"){const{SidebarLayout:s}=await _r(async()=>{const{SidebarLayout:i}=await import("./SidebarLayout.js");return{SidebarLayout:i}},__vite__mapDeps([0,1,2,3,4])),n=new s;n.config=r,o=n}else throw new Error(`Unsupported layout type: ${r.type}`);return e.appendChild(o),"updateComplete"in o&&await o.updateComplete,o}}const vn=Ye.instance;class Ht extends Error{constructor(e){super(),this.name="CanceledError",e instanceof Error?(this.message=e.message,this.cause=e.cause,this.stack=e.stack):typeof ProgressEvent<"u"&&e instanceof ProgressEvent?(this.message="Request was cancelled",this.cause=e):typeof e=="string"?this.message=e:this.message="Request was cancelled"}}class yn{constructor(e=new TextDecoder("utf-8")){this.DELIMITER=/\r?\n/,this.decoder=e}async*parse(e){let r=!1,o="";for(;!r;){const{value:s,done:n}=await e.read();if(r=n,s){o+=this.decoder.decode(s,{stream:!0});const i=o.split(this.DELIMITER);o=i.pop()||"";for(const a of i)yield{type:"text",data:a}}}o&&(yield{type:"text",data:o})}}class wn{constructor(e=new TextDecoder("utf-8")){this.decoder=e}async*parse(e){let r=!1,o="";for(;!r;){const{value:s,done:n}=await e.read();if(r=n,s){o+=this.decoder.decode(s,{stream:!0});const{objects:i,remaining:a}=this.extractJsonObjects(o);o=a;for(const l of i)yield{type:"json",data:l}}}if(o.trim())try{const s=o.trim();JSON.parse(s),yield{type:"json",data:s}}catch(s){console.error("[JsonStreamParser] Failed to parse remaining JSON buffer:",{buffer:o.substring(0,200),error:s instanceof Error?s.message:String(s)})}}extractJsonObjects(e){const r=[];let o="",s=0,n=!1,i=!1,a=0;for(;a<e.length;){const l=e[a];if(s<1){(l==="{"||l==="[")&&(o+=l,s=1,n=!1,i=!1),a++;continue}if(o+=l,i){i=!1,a++;continue}if(l==="\\"&&n){i=!0,a++;continue}if(l==='"'){n=!n,a++;continue}if(n||(l==="{"||l==="["?s++:(l==="}"||l==="]")&&s--),s===0){const c=o.trim();try{JSON.parse(c),r.push(c)}catch(u){console.error("[JsonStreamParser] Failed to parse JSON object:",{json:c.substring(0,200),error:u instanceof Error?u.message:String(u)})}o="",n=!1,i=!1}a++}return{objects:r,remaining:o}}}class xn{constructor(e=new TextDecoder("utf-8")){this.DELIMITER=/\r?\n\r?\n/,this.decoder=e}async*parse(e){let r=!1,o="";for(;!r;){const{value:s,done:n}=await e.read();if(r=n,s){o+=this.decoder.decode(s,{stream:!0});const i=o.split(this.DELIMITER);if(i.length>1){for(let a=0;a<i.length-1;a++){const l=this.parseBlock(i[a].trim());l&&(yield l)}o=i[i.length-1]}}}if(o){const s=this.parseBlock(o.trim());s&&(yield s)}}parseBlock(e){if(!e)return;const r=e.split(/\r?\n/);if(r.length===0)return;const o={type:"sse",event:"message",data:""};for(const s of r){if(s===""||s.startsWith(":"))continue;const n=s.indexOf(":");let i,a;if(n===-1)i=s,a="";else{i=s.slice(0,n);const l=s.slice(n+1);a=l.startsWith(" ")?l.slice(1):l}if(i==="event")o.event=a;else if(i==="data")o.data=o.data?`${o.data}
${a}`:a;else if(i==="id")o.id=a;else if(i==="retry"){const l=parseInt(a,10);isNaN(l)||(o.retry=l)}}return o}}function kn(t){const e=t.get("content-type")?.toLowerCase()||"";return e.includes("text/event-stream")?"sse":e.includes("application/json")||e.includes("application/x-ndjson")?"json":(e.includes("text/"),"text")}function $n({format:t,decoder:e}){switch(t){case"sse":return new xn(e);case"json":return new wn(e);case"text":return new yn(e);default:throw new Error(`Unsupported stream format: ${t}`)}}class _n{constructor(e){this._response=e}get ok(){return this._response.ok}get redirected(){return this._response.redirected}get status(){return this._response.status}get statusText(){return this._response.statusText}get url(){return this._response.url}get headers(){return this._response.headers}get body(){return this._response.body}text(){return this._response.text()}json(){return this._response.json()}arrayBuffer(){return this._response.arrayBuffer()}async bytes(){const e=await this._response.arrayBuffer();return new Uint8Array(e)}blob(){return this._response.blob()}formData(){return this._response.formData()}async*stream(e){try{const r=this._response.body?.getReader();if(!r)throw new Error("Response body is not available for streaming.");const o=e?.decoder||new TextDecoder("utf-8"),s=!e||e.format==="auto"?kn(this._response.headers):e.format;yield*$n({format:s,decoder:o}).parse(r)}catch(r){throw r instanceof Error&&(r.name==="AbortError"||r.name==="CanceledError")?new Ht(r):r}}async*streamAsSse(e){yield*this.stream({format:"sse",decoder:e})}async*streamAsJson(e){yield*this.stream({format:"json",decoder:e})}async*streamAsText(e){yield*this.stream({format:"text",decoder:e})}}class Cn{constructor(){this._isCancelled=!1,this.controller=new AbortController,this.callbacks=[]}get signal(){return this.controller.signal}get isCancelled(){return this._isCancelled}register(e){this.callbacks.push(e)}cancel(e){if(!this._isCancelled){this._isCancelled=!0,this.controller.abort();for(const r of this.callbacks)try{r(e)}catch(o){console.error("CancelToken callback error:",o)}}}throwIfCancelled(){if(this._isCancelled)throw new Ht("Operation has been cancelled")}}function fr({baseUrl:t,path:e,query:r}){if(!t)throw new Error("Base URL is required for building the request URL.");const o=e?t.replace(/\/$/,"")+"/"+e.replace(/^\//,""):t,s=/^https?:\/\//.test(o);if(!s&&!globalThis.location?.origin)throw new Error("Relative base URL requires a browser environment. Use an absolute URL (e.g., 'http://localhost:3000/api') in SSR or Node.js.");const n=s?new URL(o):new URL(o,globalThis.location.origin);return r&&Object.entries(r).forEach(([i,a])=>{a!=null&&(Array.isArray(a)?a:[a]).forEach(l=>n.searchParams.append(i,l))}),n}function Xe(t,e){if(t.startsWith("http://")||t.startsWith("https://")){const r=new URL(t),o={};r.searchParams.forEach((n,i)=>{o[i]?Array.isArray(o[i])?o[i].push(n):o[i]=[o[i],n]:o[i]=n});const s={baseUrl:r.origin};return r.pathname&&r.pathname!=="/"&&(s.path=r.pathname),Object.keys(o).length>0&&(s.query=o),s}else{if(!e)throw new Error("Base URL is required for relative URLs.");const[r,o]=t.split("?",2),s={};return o&&new URLSearchParams(o).forEach((n,i)=>{s[i]?Array.isArray(s[i])?s[i].push(n):s[i]=[s[i],n]:s[i]=n}),{baseUrl:e,path:r,query:s}}}class Sn{constructor(e){this.baseUrl=e.baseUrl,this.headers=e.headers,this.timeout=e.timeout,this.credentials=e.credentials,this.mode=e.mode,this.cache=e.cache,this.keepalive=e.keepalive,this.onRequest=e.onRequest,this.onResponse=e.onResponse,this.onError=e.onError}async head(e,r){const{baseUrl:o,path:s,query:n}=Xe(e,this.baseUrl);return this.send({method:"HEAD",baseUrl:o,path:s,query:n},r)}async get(e,r){const{baseUrl:o,path:s,query:n}=Xe(e,this.baseUrl);return this.send({method:"GET",baseUrl:o,path:s,query:n},r)}async post(e,r,o){const{baseUrl:s,path:n,query:i}=Xe(e,this.baseUrl);return this.send({method:"POST",baseUrl:s,path:n,query:i,body:r},o)}async put(e,r,o){const{baseUrl:s,path:n,query:i}=Xe(e,this.baseUrl);return this.send({method:"PUT",baseUrl:s,path:n,query:i,body:r},o)}async patch(e,r,o){const{baseUrl:s,path:n,query:i}=Xe(e,this.baseUrl);return this.send({method:"PATCH",baseUrl:s,path:n,query:i,body:r},o)}async delete(e,r){const{baseUrl:o,path:s,query:n}=Xe(e,this.baseUrl);return this.send({method:"DELETE",baseUrl:o,path:s,query:n},r)}async send(e,r){const o=fr({baseUrl:e.baseUrl??this.baseUrl,path:e.path,query:e.query}),s=new Headers(this.headers);if(e.headers&&new Headers(e.headers).forEach((c,u)=>{s.set(u,c)}),!s.has("Content-Type")){const c=this.guessMimeType(e.body);c&&s.set("Content-Type",c)}let n=e.body;s.get("Content-Type")?.includes("application/json")&&typeof n=="object"&&n!==null&&(n=JSON.stringify(n)),this.onRequest&&await this.onRequest({method:e.method,path:e.path,query:e.query,baseUrl:e.baseUrl??this.baseUrl},s);const i=r||new Cn,a=e.timeout??this.timeout,l=a?setTimeout(()=>i.cancel(),a):null;try{const c=await fetch(o.toString(),{method:e.method,headers:s,body:n,cache:e.cache??this.cache,credentials:e.credentials??this.credentials,mode:e.mode??this.mode,keepalive:e.keepalive??this.keepalive,signal:i.signal});return this.onResponse&&await this.onResponse({ok:c.ok,status:c.status,statusText:c.statusText,headers:c.headers,url:c.url}),new _n(c)}catch(c){throw this.onError&&await this.onError({error:c}),i.isCancelled?new Ht(c):c}finally{l&&clearTimeout(l)}}async*upload(e,r){const o=fr({baseUrl:e.baseUrl??this.baseUrl,path:e.path,query:e.query}),s=new XMLHttpRequest;s.open(e.method,o,!0);const n=e.timeout??this.timeout;n&&(s.timeout=n);const i=e.credentials??this.credentials;i&&(s.withCredentials=i==="include");const a=new Headers;this.headers&&new Headers(this.headers).forEach((p,m)=>{a.set(m,p)}),e.headers&&new Headers(e.headers).forEach((p,m)=>{a.set(m,p)}),this.onRequest&&await this.onRequest({method:e.method,path:e.path,query:e.query,baseUrl:e.baseUrl??this.baseUrl},a),a.forEach((p,m)=>{s.setRequestHeader(m,p)});let l;if(e.body instanceof FormData)l=e.body;else if(Array.isArray(e.body)){const p=new FormData;for(let m=0;m<e.body.length;m++)p.append("files",e.body[m]);l=p}else{const p=new FormData;p.append("file",e.body),l=p}const c=[];let u=null,d=!1;const f=p=>{if(u){const m=u;u=null,m(p)}else c.push(p)},g=()=>new Promise(p=>{c.length>0?p(c.shift()):u=p});for(s.upload.onprogress=p=>{if(p.lengthComputable){const m=Math.round(p.loaded/p.total*100);f({type:"progress",loaded:p.loaded,total:p.total,progress:m})}},s.onload=()=>{if(s.status>=200&&s.status<300){const p={},k=s.getAllResponseHeaders().split(`\r
`);for(const w of k){const x=w.indexOf(": ");if(x!==-1){const _=w.substring(0,x).trim(),C=w.substring(x+2).trim();p[_]?p[_]=`${p[_]}, ${C}`:p[_]=C}}f({type:"success",status:s.status,headers:p,body:s.response})}else f({type:"failure",status:s.status,message:`Upload failed with status ${s.status}`})},s.onerror=()=>{f({type:"failure",message:"Network error occurred"})},s.ontimeout=()=>{f({type:"failure",message:"Request timed out"})},s.onabort=()=>{d=!0,f({type:"failure",message:"Request was cancelled"})},r&&r.register(()=>{s.abort()}),s.send(l);;){const p=await g();if(d)throw new Ht("Request was cancelled");if(yield p,p.type==="success"||p.type==="failure")break}}download(e){const r=fr({baseUrl:e.baseUrl??this.baseUrl,path:e.path,query:e.query}),o=document.createElement("a");o.style.display="none",o.href=r.toString(),o.download="",document.body.appendChild(o),o.click(),o.remove()}guessMimeType(e){if(e!=null)return typeof e=="object"?e instanceof Blob?e.type||"application/octet-stream":e instanceof ArrayBuffer||ArrayBuffer.isView(e)?"application/octet-stream":e instanceof URLSearchParams?"application/x-www-form-urlencoded;charset=UTF-8":e instanceof FormData||e instanceof ReadableStream?void 0:"application/json;charset=UTF-8":"text/plain;charset=UTF-8"}}const Do=new Sn({baseUrl:"/api/vault-ai"});async function En(){return(await Do.get("/reports")).json()}async function On(t,e){return(await Do.get(`/reports/${t}/outputs/${e}`)).text()}function Fo(t){const e=/[\u200B\u200C\u200D\u200E\u200F\uFEFF]/g;return e.test(t)?t.replace(e,""):t}var no=/[&<>"']/g,jn={"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"};function Pn(t){return no.test(t)?t.replace(no,e=>jn[e]):t}var io=/[&<>"]/g,Rn={"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"};function To(t){return io.test(t)?t.replace(io,e=>Rn[e]):t}var ao=/[\u0000-\u001F\u007F\s]+/g,An=/^(?:javascript|data|vbscript):/i;function Ln(t){let e=Fo(t).trim();return ao.test(e)&&(e=e.replace(ao,"")),e=e.replace(/\\/g,"/"),e.startsWith("//")||An.test(e)?"#":To(e)}function lo(t){if(t.parentElement)return t.parentElement;{const e=t.getRootNode({composed:!1});return e instanceof Document?e.documentElement:e instanceof ShadowRoot?e.host:e instanceof HTMLElement?e:void 0}}function Dn(t,e){if(!e)return[];const r=t.getRootNode({composed:!1});if(r instanceof ShadowRoot||r instanceof Document){const o=r.querySelectorAll(e);return Array.from(o)}else return[]}function Bt(t,e={},r=""){const o=new Set(["href","src"]),s=Object.entries(e).flatMap(([n,i])=>{if(i===void 0||i==null||i===!1)return[];if(i===!0)return[n];const a=typeof i=="object"?JSON.stringify(i):String(i);return[`${n}="${o.has(n)?Ln(a):To(a)}"`]}).join(" ");return s?`<${t} ${s}>${r}</${t}>`:`<${t}>${r}</${t}>`}var Fn=S`
  :host {
    position: absolute;
    z-index: 1000;
    top: 0;
    left: 0;
    width: max-content;

    opacity: 0;
    visibility: hidden;
    pointer-events: none;
    transition: opacity 0.2s ease, visibility 0s 0.2s;
  }
  :host([open]) {
    opacity: 1;
    visibility: visible;
    pointer-events: auto;
    transition-delay: 0s;
  }
  :host([strategy="absolute"]) {
    position: absolute;
  }
  :host([strategy="fixed"]) {
    position: fixed;
  }

  /* 화살표: clip-path로 삼각형 */
  #arrow {
    position: absolute;
    width: 0.5em;
    height: 0.5em;
    background-color: inherit;
  }
`;const Tn=["top","right","bottom","left"],co=["start","end"],uo=Tn.reduce((t,e)=>t.concat(e,e+"-"+co[0],e+"-"+co[1]),[]),ot=Math.min,Ue=Math.max,Wt=Math.round,Tt=Math.floor,he=t=>({x:t,y:t}),Bn={left:"right",right:"left",bottom:"top",top:"bottom"};function xr(t,e,r){return Ue(t,ot(e,r))}function ct(t,e){return typeof t=="function"?t(e):t}function $e(t){return t.split("-")[0]}function pe(t){return t.split("-")[1]}function Bo(t){return t==="x"?"y":"x"}function Cr(t){return t==="y"?"height":"width"}function Ee(t){const e=t[0];return e==="t"||e==="b"?"y":"x"}function Sr(t){return Bo(Ee(t))}function No(t,e,r){r===void 0&&(r=!1);const o=pe(t),s=Sr(t),n=Cr(s);let i=s==="x"?o===(r?"end":"start")?"right":"left":o==="start"?"bottom":"top";return e.reference[n]>e.floating[n]&&(i=Gt(i)),[i,Gt(i)]}function Nn(t){const e=Gt(t);return[Kt(t),e,Kt(e)]}function Kt(t){return t.includes("start")?t.replace("start","end"):t.replace("end","start")}const ho=["left","right"],po=["right","left"],Un=["top","bottom"],zn=["bottom","top"];function In(t,e,r){switch(t){case"top":case"bottom":return r?e?po:ho:e?ho:po;case"left":case"right":return e?Un:zn;default:return[]}}function Mn(t,e,r,o){const s=pe(t);let n=In($e(t),r==="start",o);return s&&(n=n.map(i=>i+"-"+s),e&&(n=n.concat(n.map(Kt)))),n}function Gt(t){const e=$e(t);return Bn[e]+t.slice(e.length)}function Vn(t){return{top:0,right:0,bottom:0,left:0,...t}}function Uo(t){return typeof t!="number"?Vn(t):{top:t,right:t,bottom:t,left:t}}function qt(t){const{x:e,y:r,width:o,height:s}=t;return{width:o,height:s,top:r,left:e,right:e+o,bottom:r+s,x:e,y:r}}function fo(t,e,r){let{reference:o,floating:s}=t;const n=Ee(e),i=Sr(e),a=Cr(i),l=$e(e),c=n==="y",u=o.x+o.width/2-s.width/2,d=o.y+o.height/2-s.height/2,f=o[a]/2-s[a]/2;let g;switch(l){case"top":g={x:u,y:o.y-s.height};break;case"bottom":g={x:u,y:o.y+o.height};break;case"right":g={x:o.x+o.width,y:d};break;case"left":g={x:o.x-s.width,y:d};break;default:g={x:o.x,y:o.y}}switch(pe(e)){case"start":g[i]-=f*(r&&c?-1:1);break;case"end":g[i]+=f*(r&&c?-1:1);break}return g}async function Hn(t,e){var r;e===void 0&&(e={});const{x:o,y:s,platform:n,rects:i,elements:a,strategy:l}=t,{boundary:c="clippingAncestors",rootBoundary:u="viewport",elementContext:d="floating",altBoundary:f=!1,padding:g=0}=ct(e,t),p=Uo(g),k=a[f?d==="floating"?"reference":"floating":d],w=qt(await n.getClippingRect({element:(r=await(n.isElement==null?void 0:n.isElement(k)))==null||r?k:k.contextElement||await(n.getDocumentElement==null?void 0:n.getDocumentElement(a.floating)),boundary:c,rootBoundary:u,strategy:l})),x=d==="floating"?{x:o,y:s,width:i.floating.width,height:i.floating.height}:i.reference,_=await(n.getOffsetParent==null?void 0:n.getOffsetParent(a.floating)),C=await(n.isElement==null?void 0:n.isElement(_))?await(n.getScale==null?void 0:n.getScale(_))||{x:1,y:1}:{x:1,y:1},P=qt(n.convertOffsetParentRelativeRectToViewportRelativeRect?await n.convertOffsetParentRelativeRectToViewportRelativeRect({elements:a,rect:x,offsetParent:_,strategy:l}):x);return{top:(w.top-P.top+p.top)/C.y,bottom:(P.bottom-w.bottom+p.bottom)/C.y,left:(w.left-P.left+p.left)/C.x,right:(P.right-w.right+p.right)/C.x}}const Wn=50,Kn=async(t,e,r)=>{const{placement:o="bottom",strategy:s="absolute",middleware:n=[],platform:i}=r,a=i.detectOverflow?i:{...i,detectOverflow:Hn},l=await(i.isRTL==null?void 0:i.isRTL(e));let c=await i.getElementRects({reference:t,floating:e,strategy:s}),{x:u,y:d}=fo(c,o,l),f=o,g=0;const p={};for(let m=0;m<n.length;m++){const k=n[m];if(!k)continue;const{name:w,fn:x}=k,{x:_,y:C,data:P,reset:R}=await x({x:u,y:d,initialPlacement:o,placement:f,strategy:s,middlewareData:p,rects:c,platform:a,elements:{reference:t,floating:e}});u=_??u,d=C??d,p[w]={...p[w],...P},R&&g<Wn&&(g++,typeof R=="object"&&(R.placement&&(f=R.placement),R.rects&&(c=R.rects===!0?await i.getElementRects({reference:t,floating:e,strategy:s}):R.rects),{x:u,y:d}=fo(c,f,l)),m=-1)}return{x:u,y:d,placement:f,strategy:s,middlewareData:p}},Gn=t=>({name:"arrow",options:t,async fn(e){const{x:r,y:o,placement:s,rects:n,platform:i,elements:a,middlewareData:l}=e,{element:c,padding:u=0}=ct(t,e)||{};if(c==null)return{};const d=Uo(u),f={x:r,y:o},g=Sr(s),p=Cr(g),m=await i.getDimensions(c),k=g==="y",w=k?"top":"left",x=k?"bottom":"right",_=k?"clientHeight":"clientWidth",C=n.reference[p]+n.reference[g]-f[g]-n.floating[p],P=f[g]-n.reference[g],R=await(i.getOffsetParent==null?void 0:i.getOffsetParent(c));let L=R?R[_]:0;(!L||!await(i.isElement==null?void 0:i.isElement(R)))&&(L=a.floating[_]||n.floating[p]);const ye=C/2-P/2,B=L/2-m[p]/2-1,O=ot(d[w],B),W=ot(d[x],B),re=O,D=L-m[p]-W,A=L/2-m[p]/2+ye,ue=xr(re,A,D),T=!l.arrow&&pe(s)!=null&&A!==ue&&n.reference[p]/2-(A<re?O:W)-m[p]/2<0,F=T?A<re?A-re:A-D:0;return{[g]:f[g]+F,data:{[g]:ue,centerOffset:A-ue-F,...T&&{alignmentOffset:F}},reset:T}}});function qn(t,e,r){return(t?[...r.filter(s=>pe(s)===t),...r.filter(s=>pe(s)!==t)]:r.filter(s=>$e(s)===s)).filter(s=>t?pe(s)===t||(e?Kt(s)!==s:!1):!0)}const Jn=function(t){return t===void 0&&(t={}),{name:"autoPlacement",options:t,async fn(e){var r,o,s;const{rects:n,middlewareData:i,placement:a,platform:l,elements:c}=e,{crossAxis:u=!1,alignment:d,allowedPlacements:f=uo,autoAlignment:g=!0,...p}=ct(t,e),m=d!==void 0||f===uo?qn(d||null,g,f):f,k=await l.detectOverflow(e,p),w=((r=i.autoPlacement)==null?void 0:r.index)||0,x=m[w];if(x==null)return{};const _=No(x,n,await(l.isRTL==null?void 0:l.isRTL(c.floating)));if(a!==x)return{reset:{placement:m[0]}};const C=[k[$e(x)],k[_[0]],k[_[1]]],P=[...((o=i.autoPlacement)==null?void 0:o.overflows)||[],{placement:x,overflows:C}],R=m[w+1];if(R)return{data:{index:w+1,overflows:P},reset:{placement:R}};const L=P.map(O=>{const W=pe(O.placement);return[O.placement,W&&u?O.overflows.slice(0,2).reduce((re,D)=>re+D,0):O.overflows[0],O.overflows]}).sort((O,W)=>O[1]-W[1]),B=((s=L.filter(O=>O[2].slice(0,pe(O[0])?2:3).every(W=>W<=0))[0])==null?void 0:s[0])||L[0][0];return B!==a?{data:{index:w+1,overflows:P},reset:{placement:B}}:{}}}},Xn=function(t){return t===void 0&&(t={}),{name:"flip",options:t,async fn(e){var r,o;const{placement:s,middlewareData:n,rects:i,initialPlacement:a,platform:l,elements:c}=e,{mainAxis:u=!0,crossAxis:d=!0,fallbackPlacements:f,fallbackStrategy:g="bestFit",fallbackAxisSideDirection:p="none",flipAlignment:m=!0,...k}=ct(t,e);if((r=n.arrow)!=null&&r.alignmentOffset)return{};const w=$e(s),x=Ee(a),_=$e(a)===a,C=await(l.isRTL==null?void 0:l.isRTL(c.floating)),P=f||(_||!m?[Gt(a)]:Nn(a)),R=p!=="none";!f&&R&&P.push(...Mn(a,m,p,C));const L=[a,...P],ye=await l.detectOverflow(e,k),B=[];let O=((o=n.flip)==null?void 0:o.overflows)||[];if(u&&B.push(ye[w]),d){const A=No(s,i,C);B.push(ye[A[0]],ye[A[1]])}if(O=[...O,{placement:s,overflows:B}],!B.every(A=>A<=0)){var W,re;const A=(((W=n.flip)==null?void 0:W.index)||0)+1,ue=L[A];if(ue&&(!(d==="alignment"?x!==Ee(ue):!1)||O.every(N=>Ee(N.placement)===x?N.overflows[0]>0:!0)))return{data:{index:A,overflows:O},reset:{placement:ue}};let T=(re=O.filter(F=>F.overflows[0]<=0).sort((F,N)=>F.overflows[1]-N.overflows[1])[0])==null?void 0:re.placement;if(!T)switch(g){case"bestFit":{var D;const F=(D=O.filter(N=>{if(R){const K=Ee(N.placement);return K===x||K==="y"}return!0}).map(N=>[N.placement,N.overflows.filter(K=>K>0).reduce((K,Dt)=>K+Dt,0)]).sort((N,K)=>N[1]-K[1])[0])==null?void 0:D[0];F&&(T=F);break}case"initialPlacement":T=a;break}if(s!==T)return{reset:{placement:T}}}return{}}}},Yn=new Set(["left","top"]);async function Qn(t,e){const{placement:r,platform:o,elements:s}=t,n=await(o.isRTL==null?void 0:o.isRTL(s.floating)),i=$e(r),a=pe(r),l=Ee(r)==="y",c=Yn.has(i)?-1:1,u=n&&l?-1:1,d=ct(e,t);let{mainAxis:f,crossAxis:g,alignmentAxis:p}=typeof d=="number"?{mainAxis:d,crossAxis:0,alignmentAxis:null}:{mainAxis:d.mainAxis||0,crossAxis:d.crossAxis||0,alignmentAxis:d.alignmentAxis};return a&&typeof p=="number"&&(g=a==="end"?p*-1:p),l?{x:g*u,y:f*c}:{x:f*c,y:g*u}}const Zn=function(t){return t===void 0&&(t=0),{name:"offset",options:t,async fn(e){var r,o;const{x:s,y:n,placement:i,middlewareData:a}=e,l=await Qn(e,t);return i===((r=a.offset)==null?void 0:r.placement)&&(o=a.arrow)!=null&&o.alignmentOffset?{}:{x:s+l.x,y:n+l.y,data:{...l,placement:i}}}}},ei=function(t){return t===void 0&&(t={}),{name:"shift",options:t,async fn(e){const{x:r,y:o,placement:s,platform:n}=e,{mainAxis:i=!0,crossAxis:a=!1,limiter:l={fn:w=>{let{x,y:_}=w;return{x,y:_}}},...c}=ct(t,e),u={x:r,y:o},d=await n.detectOverflow(e,c),f=Ee($e(s)),g=Bo(f);let p=u[g],m=u[f];if(i){const w=g==="y"?"top":"left",x=g==="y"?"bottom":"right",_=p+d[w],C=p-d[x];p=xr(_,p,C)}if(a){const w=f==="y"?"top":"left",x=f==="y"?"bottom":"right",_=m+d[w],C=m-d[x];m=xr(_,m,C)}const k=l.fn({...e,[g]:p,[f]:m});return{...k,data:{x:k.x-r,y:k.y-o,enabled:{[g]:i,[f]:a}}}}}};function tr(){return typeof window<"u"}function ut(t){return zo(t)?(t.nodeName||"").toLowerCase():"#document"}function q(t){var e;return(t==null||(e=t.ownerDocument)==null?void 0:e.defaultView)||window}function ve(t){var e;return(e=(zo(t)?t.ownerDocument:t.document)||window.document)==null?void 0:e.documentElement}function zo(t){return tr()?t instanceof Node||t instanceof q(t).Node:!1}function le(t){return tr()?t instanceof Element||t instanceof q(t).Element:!1}function Ce(t){return tr()?t instanceof HTMLElement||t instanceof q(t).HTMLElement:!1}function go(t){return!tr()||typeof ShadowRoot>"u"?!1:t instanceof ShadowRoot||t instanceof q(t).ShadowRoot}function Lt(t){const{overflow:e,overflowX:r,overflowY:o,display:s}=ce(t);return/auto|scroll|overlay|hidden|clip/.test(e+o+r)&&s!=="inline"&&s!=="contents"}function ti(t){return/^(table|td|th)$/.test(ut(t))}function rr(t){try{if(t.matches(":popover-open"))return!0}catch{}try{return t.matches(":modal")}catch{return!1}}const ri=/transform|translate|scale|rotate|perspective|filter/,oi=/paint|layout|strict|content/,Ne=t=>!!t&&t!=="none";let gr;function Er(t){const e=le(t)?ce(t):t;return Ne(e.transform)||Ne(e.translate)||Ne(e.scale)||Ne(e.rotate)||Ne(e.perspective)||!Or()&&(Ne(e.backdropFilter)||Ne(e.filter))||ri.test(e.willChange||"")||oi.test(e.contain||"")}function si(t){let e=Fe(t);for(;Ce(e)&&!st(e);){if(Er(e))return e;if(rr(e))return null;e=Fe(e)}return null}function Or(){return gr==null&&(gr=typeof CSS<"u"&&CSS.supports&&CSS.supports("-webkit-backdrop-filter","none")),gr}function st(t){return/^(html|body|#document)$/.test(ut(t))}function ce(t){return q(t).getComputedStyle(t)}function or(t){return le(t)?{scrollLeft:t.scrollLeft,scrollTop:t.scrollTop}:{scrollLeft:t.scrollX,scrollTop:t.scrollY}}function Fe(t){if(ut(t)==="html")return t;const e=t.assignedSlot||t.parentNode||go(t)&&t.host||ve(t);return go(e)?e.host:e}function Io(t){const e=Fe(t);return st(e)?t.ownerDocument?t.ownerDocument.body:t.body:Ce(e)&&Lt(e)?e:Io(e)}function Rt(t,e,r){var o;e===void 0&&(e=[]),r===void 0&&(r=!0);const s=Io(t),n=s===((o=t.ownerDocument)==null?void 0:o.body),i=q(s);if(n){const a=kr(i);return e.concat(i,i.visualViewport||[],Lt(s)?s:[],a&&r?Rt(a):[])}else return e.concat(s,Rt(s,[],r))}function kr(t){return t.parent&&Object.getPrototypeOf(t.parent)?t.frameElement:null}function Mo(t){const e=ce(t);let r=parseFloat(e.width)||0,o=parseFloat(e.height)||0;const s=Ce(t),n=s?t.offsetWidth:r,i=s?t.offsetHeight:o,a=Wt(r)!==n||Wt(o)!==i;return a&&(r=n,o=i),{width:r,height:o,$:a}}function jr(t){return le(t)?t:t.contextElement}function Ze(t){const e=jr(t);if(!Ce(e))return he(1);const r=e.getBoundingClientRect(),{width:o,height:s,$:n}=Mo(e);let i=(n?Wt(r.width):r.width)/o,a=(n?Wt(r.height):r.height)/s;return(!i||!Number.isFinite(i))&&(i=1),(!a||!Number.isFinite(a))&&(a=1),{x:i,y:a}}const ni=he(0);function Vo(t){const e=q(t);return!Or()||!e.visualViewport?ni:{x:e.visualViewport.offsetLeft,y:e.visualViewport.offsetTop}}function ii(t,e,r){return e===void 0&&(e=!1),!r||e&&r!==q(t)?!1:e}function He(t,e,r,o){e===void 0&&(e=!1),r===void 0&&(r=!1);const s=t.getBoundingClientRect(),n=jr(t);let i=he(1);e&&(o?le(o)&&(i=Ze(o)):i=Ze(t));const a=ii(n,r,o)?Vo(n):he(0);let l=(s.left+a.x)/i.x,c=(s.top+a.y)/i.y,u=s.width/i.x,d=s.height/i.y;if(n){const f=q(n),g=o&&le(o)?q(o):o;let p=f,m=kr(p);for(;m&&o&&g!==p;){const k=Ze(m),w=m.getBoundingClientRect(),x=ce(m),_=w.left+(m.clientLeft+parseFloat(x.paddingLeft))*k.x,C=w.top+(m.clientTop+parseFloat(x.paddingTop))*k.y;l*=k.x,c*=k.y,u*=k.x,d*=k.y,l+=_,c+=C,p=q(m),m=kr(p)}}return qt({width:u,height:d,x:l,y:c})}function sr(t,e){const r=or(t).scrollLeft;return e?e.left+r:He(ve(t)).left+r}function Ho(t,e){const r=t.getBoundingClientRect(),o=r.left+e.scrollLeft-sr(t,r),s=r.top+e.scrollTop;return{x:o,y:s}}function ai(t){let{elements:e,rect:r,offsetParent:o,strategy:s}=t;const n=s==="fixed",i=ve(o),a=e?rr(e.floating):!1;if(o===i||a&&n)return r;let l={scrollLeft:0,scrollTop:0},c=he(1);const u=he(0),d=Ce(o);if((d||!d&&!n)&&((ut(o)!=="body"||Lt(i))&&(l=or(o)),d)){const g=He(o);c=Ze(o),u.x=g.x+o.clientLeft,u.y=g.y+o.clientTop}const f=i&&!d&&!n?Ho(i,l):he(0);return{width:r.width*c.x,height:r.height*c.y,x:r.x*c.x-l.scrollLeft*c.x+u.x+f.x,y:r.y*c.y-l.scrollTop*c.y+u.y+f.y}}function li(t){return Array.from(t.getClientRects())}function ci(t){const e=ve(t),r=or(t),o=t.ownerDocument.body,s=Ue(e.scrollWidth,e.clientWidth,o.scrollWidth,o.clientWidth),n=Ue(e.scrollHeight,e.clientHeight,o.scrollHeight,o.clientHeight);let i=-r.scrollLeft+sr(t);const a=-r.scrollTop;return ce(o).direction==="rtl"&&(i+=Ue(e.clientWidth,o.clientWidth)-s),{width:s,height:n,x:i,y:a}}const mo=25;function ui(t,e){const r=q(t),o=ve(t),s=r.visualViewport;let n=o.clientWidth,i=o.clientHeight,a=0,l=0;if(s){n=s.width,i=s.height;const u=Or();(!u||u&&e==="fixed")&&(a=s.offsetLeft,l=s.offsetTop)}const c=sr(o);if(c<=0){const u=o.ownerDocument,d=u.body,f=getComputedStyle(d),g=u.compatMode==="CSS1Compat"&&parseFloat(f.marginLeft)+parseFloat(f.marginRight)||0,p=Math.abs(o.clientWidth-d.clientWidth-g);p<=mo&&(n-=p)}else c<=mo&&(n+=c);return{width:n,height:i,x:a,y:l}}function di(t,e){const r=He(t,!0,e==="fixed"),o=r.top+t.clientTop,s=r.left+t.clientLeft,n=Ce(t)?Ze(t):he(1),i=t.clientWidth*n.x,a=t.clientHeight*n.y,l=s*n.x,c=o*n.y;return{width:i,height:a,x:l,y:c}}function bo(t,e,r){let o;if(e==="viewport")o=ui(t,r);else if(e==="document")o=ci(ve(t));else if(le(e))o=di(e,r);else{const s=Vo(t);o={x:e.x-s.x,y:e.y-s.y,width:e.width,height:e.height}}return qt(o)}function Wo(t,e){const r=Fe(t);return r===e||!le(r)||st(r)?!1:ce(r).position==="fixed"||Wo(r,e)}function hi(t,e){const r=e.get(t);if(r)return r;let o=Rt(t,[],!1).filter(a=>le(a)&&ut(a)!=="body"),s=null;const n=ce(t).position==="fixed";let i=n?Fe(t):t;for(;le(i)&&!st(i);){const a=ce(i),l=Er(i);!l&&a.position==="fixed"&&(s=null),(n?!l&&!s:!l&&a.position==="static"&&!!s&&(s.position==="absolute"||s.position==="fixed")||Lt(i)&&!l&&Wo(t,i))?o=o.filter(u=>u!==i):s=a,i=Fe(i)}return e.set(t,o),o}function pi(t){let{element:e,boundary:r,rootBoundary:o,strategy:s}=t;const i=[...r==="clippingAncestors"?rr(e)?[]:hi(e,this._c):[].concat(r),o],a=bo(e,i[0],s);let l=a.top,c=a.right,u=a.bottom,d=a.left;for(let f=1;f<i.length;f++){const g=bo(e,i[f],s);l=Ue(g.top,l),c=ot(g.right,c),u=ot(g.bottom,u),d=Ue(g.left,d)}return{width:c-d,height:u-l,x:d,y:l}}function fi(t){const{width:e,height:r}=Mo(t);return{width:e,height:r}}function gi(t,e,r){const o=Ce(e),s=ve(e),n=r==="fixed",i=He(t,!0,n,e);let a={scrollLeft:0,scrollTop:0};const l=he(0);function c(){l.x=sr(s)}if(o||!o&&!n)if((ut(e)!=="body"||Lt(s))&&(a=or(e)),o){const g=He(e,!0,n,e);l.x=g.x+e.clientLeft,l.y=g.y+e.clientTop}else s&&c();n&&!o&&s&&c();const u=s&&!o&&!n?Ho(s,a):he(0),d=i.left+a.scrollLeft-l.x-u.x,f=i.top+a.scrollTop-l.y-u.y;return{x:d,y:f,width:i.width,height:i.height}}function mr(t){return ce(t).position==="static"}function vo(t,e){if(!Ce(t)||ce(t).position==="fixed")return null;if(e)return e(t);let r=t.offsetParent;return ve(t)===r&&(r=r.ownerDocument.body),r}function Ko(t,e){const r=q(t);if(rr(t))return r;if(!Ce(t)){let s=Fe(t);for(;s&&!st(s);){if(le(s)&&!mr(s))return s;s=Fe(s)}return r}let o=vo(t,e);for(;o&&ti(o)&&mr(o);)o=vo(o,e);return o&&st(o)&&mr(o)&&!Er(o)?r:o||si(t)||r}const mi=async function(t){const e=this.getOffsetParent||Ko,r=this.getDimensions,o=await r(t.floating);return{reference:gi(t.reference,await e(t.floating),t.strategy),floating:{x:0,y:0,width:o.width,height:o.height}}};function bi(t){return ce(t).direction==="rtl"}const vi={convertOffsetParentRelativeRectToViewportRelativeRect:ai,getDocumentElement:ve,getClippingRect:pi,getOffsetParent:Ko,getElementRects:mi,getClientRects:li,getDimensions:fi,getScale:Ze,isElement:le,isRTL:bi};function Go(t,e){return t.x===e.x&&t.y===e.y&&t.width===e.width&&t.height===e.height}function yi(t,e){let r=null,o;const s=ve(t);function n(){var a;clearTimeout(o),(a=r)==null||a.disconnect(),r=null}function i(a,l){a===void 0&&(a=!1),l===void 0&&(l=1),n();const c=t.getBoundingClientRect(),{left:u,top:d,width:f,height:g}=c;if(a||e(),!f||!g)return;const p=Tt(d),m=Tt(s.clientWidth-(u+f)),k=Tt(s.clientHeight-(d+g)),w=Tt(u),_={rootMargin:-p+"px "+-m+"px "+-k+"px "+-w+"px",threshold:Ue(0,ot(1,l))||1};let C=!0;function P(R){const L=R[0].intersectionRatio;if(L!==l){if(!C)return i();L?i(!1,L):o=setTimeout(()=>{i(!1,1e-7)},1e3)}L===1&&!Go(c,t.getBoundingClientRect())&&i(),C=!1}try{r=new IntersectionObserver(P,{..._,root:s.ownerDocument})}catch{r=new IntersectionObserver(P,_)}r.observe(t)}return i(!0),n}function wi(t,e,r,o){o===void 0&&(o={});const{ancestorScroll:s=!0,ancestorResize:n=!0,elementResize:i=typeof ResizeObserver=="function",layoutShift:a=typeof IntersectionObserver=="function",animationFrame:l=!1}=o,c=jr(t),u=s||n?[...c?Rt(c):[],...e?Rt(e):[]]:[];u.forEach(w=>{s&&w.addEventListener("scroll",r,{passive:!0}),n&&w.addEventListener("resize",r)});const d=c&&a?yi(c,r):null;let f=-1,g=null;i&&(g=new ResizeObserver(w=>{let[x]=w;x&&x.target===c&&g&&e&&(g.unobserve(e),cancelAnimationFrame(f),f=requestAnimationFrame(()=>{var _;(_=g)==null||_.observe(e)})),r()}),c&&!l&&g.observe(c),e&&g.observe(e));let p,m=l?He(t):null;l&&k();function k(){const w=He(t);m&&!Go(m,w)&&r(),m=w,p=requestAnimationFrame(k)}return r(),()=>{var w;u.forEach(x=>{s&&x.removeEventListener("scroll",r),n&&x.removeEventListener("resize",r)}),d?.(),(w=g)==null||w.disconnect(),g=null,l&&cancelAnimationFrame(p)}}const xi=Zn,ki=Jn,$i=ei,_i=Xn,Ci=Gn,Si=(t,e,r)=>{const o=new Map,s={platform:vi,...r},n={...s.platform,_c:o};return Kn(t,e,{...s,platform:n})};var te=class extends j{constructor(...t){super(...t),this.open=!1,this.disabled=!1,this.strategy="absolute",this.offset=0,this.shift=!1,this.arrow=!1,this.showDelay=0,this.hideDelay=0,this.cleanup=null}static{this.styles=[super.styles,Fn]}connectedCallback(){if(super.connectedCallback(),!this.anchors||this.anchors.length===0){const t=lo(this);this.anchors=t?[t]:[]}}disconnectedCallback(){this.cleanup!==null&&this.cleanup(),this.cleanup=null,clearTimeout(this.showTimer),clearTimeout(this.hideTimer),this.showTimer=void 0,this.hideTimer=void 0,super.disconnectedCallback()}willUpdate(t){super.willUpdate(t),t.has("for")&&(this.anchors=this.getAnchors(this.for)),t.has("arrow")&&this.toggleArrowElement(this.arrow)}async show(t){return this.disabled?!1:(this.hideTimer&&(clearTimeout(this.hideTimer),this.hideTimer=void 0),this.showTimer||(this.cleanup!==null&&(this.cleanup(),this.cleanup=null),await this.reposition(t),this.cleanup=wi(t,this,()=>{this.reposition(t)}),this.targetEl=t,this.open)?!0:this.fire("show")?(this.showDelay>0?this.showTimer=window.setTimeout(()=>{this.showTimer=void 0,this.open=!0},this.showDelay):this.open=!0,!0):!1)}async hide(){return this.disabled?!1:(this.showTimer&&(clearTimeout(this.showTimer),this.showTimer=void 0),this.hideTimer||(this.cleanup!==null&&(this.cleanup(),this.cleanup=null),await this.updateComplete,this.targetEl=void 0,!this.open)?!0:this.fire("hide")?(this.hideDelay>0?this.hideTimer=window.setTimeout(()=>{this.hideTimer=void 0,this.open=!1},this.hideDelay):this.open=!1,!0):!1)}async reposition(t){const e=await Si(t,this,{strategy:this.strategy,placement:this.placement,middleware:[xi(this.offset),$i({mainAxis:this.shift}),this.placement?_i():ki(),...this.arrowEl?[Ci({element:this.arrowEl})]:[]]}),r={top:{transform:"center bottom",aside:"bottom",clipPath:"polygon(0 0, 100% 0, 50% 70%)"},bottom:{transform:"center top",aside:"top",clipPath:"polygon(50% 30%, 100% 100%, 0 100%)"},left:{transform:"right center",aside:"right",clipPath:"polygon(0 0, 70% 50%, 0 100%)"},right:{transform:"left center",aside:"left",clipPath:"polygon(100% 0, 100% 100%, 30% 50%)"}}[e.placement.split("-")[0]];Object.assign(this.style,{left:`${e.x}px`,top:`${e.y}px`,transformOrigin:r.transform});const o=e.middlewareData.arrow;!this.arrowEl||!o||Object.assign(this.arrowEl.style,{left:o.x!=null?`${o.x}px`:"",top:o.y!=null?`${o.y}px`:"",right:"",bottom:"",[r.aside]:`-${this.arrowEl.offsetWidth-1}px`,clipPath:r.clipPath})}createVirtualTarget(t){return{getBoundingClientRect:()=>({width:0,height:0,x:t.clientX,y:t.clientY,top:t.clientY,left:t.clientX,right:t.clientX,bottom:t.clientY})}}toggleArrowElement(t){if(t){if(this.arrowEl)return;this.arrowEl=document.createElement("div"),this.arrowEl.id="arrow",this.renderRoot.appendChild(this.arrowEl)}else{if(!this.arrowEl)return;this.renderRoot.removeChild(this.arrowEl),this.arrowEl=void 0}}getAnchors(t){if(t){const e=Dn(this,t);return Array.from(e)}else{const e=lo(this);return e?[e]:[]}}};b([h({type:Boolean,reflect:!0}),v("design:type",Boolean)],te.prototype,"open",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Boolean)],te.prototype,"disabled",void 0);b([h({type:String,reflect:!0}),v("design:type",String)],te.prototype,"for",void 0);b([h({type:String}),v("design:type",Object)],te.prototype,"strategy",void 0);b([h({type:String}),v("design:type",Object)],te.prototype,"placement",void 0);b([h({type:Number}),v("design:type",Object)],te.prototype,"offset",void 0);b([h({type:Boolean}),v("design:type",Boolean)],te.prototype,"shift",void 0);b([h({type:Boolean}),v("design:type",Boolean)],te.prototype,"arrow",void 0);b([h({type:Number,attribute:"show-delay"}),v("design:type",Number)],te.prototype,"showDelay",void 0);b([h({type:Number,attribute:"hide-delay"}),v("design:type",Number)],te.prototype,"hideDelay",void 0);b([z(),v("design:type",Array)],te.prototype,"anchors",void 0);var Ei=S`
  :host {
    --tooltip-bridge-area: 0px;
  }

  :host {
    padding: 6px 8px;
    color: var(--u-tooltip-txt-color);
    font-family: var(--u-font-display, inherit);
    font-size: 12px;
    line-height: 1.25;
    border: none;
    border-radius: 4px;
    background-color: var(--u-tooltip-bg-color);
    box-shadow: 0 6px 18px rgba(0, 0, 0, 0.2);
    transform: scale(0.9);
    transition: opacity 0.2s ease, transform 0.2s ease, visibility 0s 0.2s;
  }
  :host([open]) {
    transform: scale(1);
  }

  /* 툴팁을 offset만큼 감싸는 보이지 않는 영역 */
  :host([interactive])::before {
    content: '';
    position: absolute;
    inset: calc(var(--tooltip-bridge-area) * -1);
    background: transparent;
    pointer-events: auto;
    z-index: -1;
  }
`,Jt=class extends te{constructor(...e){super(...e),this.interactive=!1,this.tracking=!1,this.isEmpty=!0,this.handleSlotChange=r=>{const o=r.target;this.isEmpty=o.assignedNodes({flatten:!0}).every(s=>s.nodeType===Node.TEXT_NODE&&s.textContent?.trim()===""||s.nodeType===Node.COMMENT_NODE)},this.handleAnchorTrigger=r=>{if(this.isEmpty)return;let o=r.currentTarget;o&&(this.tracking&&r instanceof PointerEvent&&(o=this.createVirtualTarget(r)),this.show(o))},this.handleAnchorDismiss=r=>{if(this.interactive){const o=r.relatedTarget;if(o instanceof Node&&(this.contains(o)||this.targetEl instanceof Element&&this.targetEl.contains(o)))return}this.hide()},this.handleAnchorPointerMove=r=>{if(!this.tracking||!this.open)return;const o=this.createVirtualTarget(r);this.reposition(o)}}static{this.styles=[super.styles,Ei]}disconnectedCallback(){this.anchors&&this.unbind(this.anchors),super.disconnectedCallback()}updated(e){if(super.updated(e),e.has("anchors")){const r=e.get("anchors"),o=this.anchors;r&&this.unbind(r),o&&this.bind(o)}if(e.has("offset")||e.has("interactive"))if(this.interactive){let r=0;if(typeof this.offset=="number"&&(r=this.offset),typeof this.offset=="object"){const{mainAxis:o=0,crossAxis:s=0}=this.offset;r=Math.max(o,s)}this.style.setProperty("--tooltip-bridge-area",`${r}px`)}else this.style.removeProperty("--tooltip-bridge-area")}render(){return y`
      <slot @slotchange=${this.handleSlotChange}></slot>
    `}bind(e){for(const r of e)r.addEventListener("pointerenter",this.handleAnchorTrigger),r.addEventListener("pointerleave",this.handleAnchorDismiss),r.addEventListener("pointermove",this.handleAnchorPointerMove),r.addEventListener("focusin",this.handleAnchorTrigger),r.addEventListener("focusout",this.handleAnchorDismiss);this.addEventListener("pointerleave",this.handleAnchorDismiss),this.addEventListener("focusout",this.handleAnchorDismiss)}unbind(e){for(const r of e)r.removeEventListener("pointerenter",this.handleAnchorTrigger),r.removeEventListener("pointerleave",this.handleAnchorDismiss),r.removeEventListener("pointermove",this.handleAnchorPointerMove),r.removeEventListener("focusin",this.handleAnchorTrigger),r.removeEventListener("focusout",this.handleAnchorDismiss);this.removeEventListener("pointerleave",this.handleAnchorDismiss),this.removeEventListener("focusout",this.handleAnchorDismiss)}};b([h({type:Boolean,reflect:!0}),v("design:type",Boolean)],Jt.prototype,"interactive",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Boolean)],Jt.prototype,"tracking",void 0);Jt=b([E("u-tooltip")],Jt);var Oi=S`
  :host {
    display: inline-block;
    color: var(--u-icon-color);
    font-size: 20px;
  }

  :host([variant="solid"]) {
    color: #fff;
  }
  :host([variant="link"]) {
    color: var(--u-blue-500);
  }

  u-button {
    padding: 0.4em;
    color: inherit;
    font-size: inherit;
  }
`,H=class extends j{constructor(...e){super(...e),this.variant="ghost",this.rounded=!1,this.disabled=!1,this.loading=!1,this.tooltipPlacement="top",this.tooltipOffset=4}static{this.styles=[super.styles,Oi]}render(){return y`
      <u-button part="button"
        .disabled=${this.disabled}
        .loading=${this.loading}
        .variant=${this.variant}
        .rounded=${this.rounded}
        .href=${this.href}
        .target=${this.target}
        .rel=${this.rel}
      >
        <u-icon part="icon"
          .lib=${this.lib}
          .name=${this.name}
          .src=${this.src}
        ></u-icon>
      </u-button>

      <u-tooltip part="tooltip"
        .placement=${this.tooltipPlacement}
        .offset=${this.tooltipOffset}
        @show=${this.handleStopTooltipEvent}
        @hide=${this.handleStopTooltipEvent}
      >
        <slot></slot>
      </u-tooltip>
    `}handleStopTooltipEvent(e){e.stopImmediatePropagation()}};b([h({type:String,reflect:!0}),v("design:type",Object)],H.prototype,"variant",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Object)],H.prototype,"rounded",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Object)],H.prototype,"disabled",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Object)],H.prototype,"loading",void 0);b([h({type:String}),v("design:type",String)],H.prototype,"href",void 0);b([h({type:String}),v("design:type",String)],H.prototype,"target",void 0);b([h({type:String}),v("design:type",String)],H.prototype,"rel",void 0);b([h({type:String}),v("design:type",String)],H.prototype,"src",void 0);b([h({type:String}),v("design:type",Object)],H.prototype,"lib",void 0);b([h({type:String}),v("design:type",String)],H.prototype,"name",void 0);b([h({type:String,attribute:"tooltip-placement"}),v("design:type",Object)],H.prototype,"tooltipPlacement",void 0);b([h({type:Number,attribute:"tooltip-offset"}),v("design:type",Object)],H.prototype,"tooltipOffset",void 0);H=b([E("u-icon-button")],H);const ji=S`
  :host(:empty) u-tooltip {
    display: none;
  }

  u-button {
    color: var(--u-txt-color-weak);
    font-size: 16px;
  }

  u-icon[name="check-lg"] {
    color: var(--u-green-500);
  }
`;var Pi=Object.defineProperty,Ri=Object.getOwnPropertyDescriptor,Ai=Object.getPrototypeOf,Li=Reflect.get,nr=(t,e,r,o)=>{for(var s=o>1?void 0:o?Ri(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Pi(e,r,s),s},Di=(t,e,r)=>Li(Ai(t),r,e);let je=class extends j{constructor(){super(...arguments),this.delay=1e3,this.isCopied=!1,this.copyToClipboard=async()=>{if(this.value&&!this.isCopied)try{if(await window.navigator.clipboard.writeText(this.value),this.delay<=0)return;this.isCopied=!0,setTimeout(()=>{this.isCopied=!1},this.delay)}catch(t){console.error("Failed to copy text to clipboard:",t),this.isCopied=!1}}}render(){return y`
      <u-button part="base" 
        variant="ghost"
        ?disabled=${this.isCopied}
        @click=${this.copyToClipboard}>
        <u-icon part="icon"
          lib="bootstrap"
          name=${this.isCopied?"check-lg":"copy"}
        ></u-icon>
      </u-button>

      <u-tooltip for="u-button" placement="bottom" distance="8">
        <slot></slot>
      </u-tooltip>
    `}};je.styles=[Di(je,je,"styles"),ji];nr([h({type:Number})],je.prototype,"delay",2);nr([h({type:String})],je.prototype,"value",2);nr([z()],je.prototype,"isCopied",2);je=nr([E("u-copy-button")],je);const Fi=S`
  :host {
    --hljs-text-color: #24292e;
    --hljs-background-color: #ffffff;
    --hljs-keyword-color: #d73a49;
    --hljs-entity-color: #6f42c1;
    --hljs-constant-color: #005cc5;
    --hljs-string-color: #032f62;
    --hljs-variable-color: #e36209;
    --hljs-comment-color: #6a737d;
    --hljs-entity-tag-color: #22863a;
    --hljs-markup-heading-color: #005cc5;
    --hljs-markup-list-color: #735c0f;
    --hljs-addition-color: #22863a;
    --hljs-addition-bg-color: #f0fff4;
    --hljs-deletion-color: #b31d28;
    --hljs-deletion-bg-color: #ffeef0;
  }
  :host-context([theme="dark"]) {
    --hljs-text-color: #c9d1d9;
    --hljs-background-color: #0d1117;
    --hljs-keyword-color: #ff7b72;
    --hljs-entity-color: #d2a8ff;
    --hljs-constant-color: #79c0ff;
    --hljs-string-color: #a5d6ff;
    --hljs-variable-color: #ffa657;
    --hljs-comment-color: #8b949e;
    --hljs-entity-tag-color: #7ee787;
    --hljs-markup-heading-color: #1f6feb;
    --hljs-markup-list-color: #f2cc60;
    --hljs-addition-color: #aff5b4;
    --hljs-addition-bg-color: #033a16;
    --hljs-deletion-color: #ffdcd7;
    --hljs-deletion-bg-color: #67060c;
  }

  :host {
    display: block;
    width: 100%;
    padding: 8px 16px;
    border: 1px solid var(--u-border-color);
    border-radius: 8px;
    background-color: var(--u-neutral-100);
  }

  .header {
    width: 100%;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 12px;
    user-select: none;
  }
  .header .status {
    display: inline-flex;
    font-size: 12px;
    color: var(--u-txt-color-strong);
  }
  .header .lang {
    font-family: Arial, Helvetica, sans-serif;
    font-size: 12px;
    font-weight: 300;
    color: var(--u-txt-color-strong);
  }

  /* highlight.js styles */
  .hljs {
    display: block;
    margin: 0;
    padding: 0;
    color: var(--hljs-text-color);
    font-family: ui-monospace, SFMono-Regular, SF Mono, Menlo, Consolas, Liberation Mono, monospace;
    font-size: 14px;
    line-height: 1.45;
    white-space: pre;
    overflow: auto;
    scrollbar-width: thin;
    scrollbar-color: var(--u-scrollbar-color) transparent;
  }

  .hljs-doctag,
  .hljs-keyword,
  .hljs-meta .hljs-keyword,
  .hljs-template-tag,
  .hljs-template-variable,
  .hljs-type,
  .hljs-variable.language_ {
    color: var(--hljs-keyword-color);
  }

  .hljs-title,
  .hljs-title.class_,
  .hljs-title.class_.inherited__,
  .hljs-title.function_ {
    color: var(--hljs-entity-color);
  }

  .hljs-attr,
  .hljs-attribute,
  .hljs-literal,
  .hljs-meta,
  .hljs-number,
  .hljs-operator,
  .hljs-variable,
  .hljs-selector-attr,
  .hljs-selector-class,
  .hljs-selector-id {
    color: var(--hljs-constant-color);
  }

  .hljs-regexp,
  .hljs-string,
  .hljs-meta .hljs-string {
    color: var(--hljs-string-color);
  }

  .hljs-built_in,
  .hljs-symbol {
    color: var(--hljs-variable-color);
  }

  .hljs-comment,
  .hljs-code,
  .hljs-formula {
    color: var(--hljs-comment-color);
  }

  .hljs-name,
  .hljs-quote,
  .hljs-selector-tag,
  .hljs-selector-pseudo {
    color: var(--hljs-entity-tag-color);
  }

  .hljs-subst {
    color: var(--hljs-text-color);
  }

  .hljs-section {
    color: var(--hljs-markup-heading-color);
    font-weight: bold;
  }

  .hljs-bullet {
    color: var(--hljs-markup-list-color);
  }

  .hljs-emphasis {
    color: var(--hljs-text-color);
    font-style: italic;
  }

  .hljs-strong {
    color: var(--hljs-text-color);
    font-weight: bold;
  }

  .hljs-addition {
    color: var(--hljs-addition-color);
    background-color: var(--hljs-addition-bg-color);
  }

  .hljs-deletion {
    color: var(--hljs-deletion-color);
    background-color: var(--hljs-deletion-bg-color);
  }

  .hljs-char.escape_,
  .hljs-link,
  .hljs-params,
  .hljs-property,
  .hljs-punctuation,
  .hljs-tag {
    color: currentColor;
  }
`;var Ti=Object.defineProperty,Bi=Object.getOwnPropertyDescriptor,Ni=Object.getPrototypeOf,Ui=Reflect.get,dt=(t,e,r,o)=>{for(var s=o>1?void 0:o?Bi(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Ti(e,r,s),s},zi=(t,e,r)=>Ui(Ni(t),r,e);let fe=class extends j{constructor(){super(...arguments),this.loading=!1,this.headless=!1,this.lang="plaintext",this.isCopied=!1,this.handleSlotChange=t=>{const r=t.target.assignedNodes({flatten:!0});this.value=r.map(o=>o.textContent).join(`

`)}}render(){const t=Ur.getLanguage(this.lang)?this.lang:"plaintext",e=this.value||"";return y`
      <div class="header" ?hidden=${this.headless}>
        <span class="status">
          ${this.loading?y`<u-spinner></u-spinner>`:y`<u-icon lib="bootstrap" name="code-slash"></u-icon>`}
        </span>
        <span class="lang">
          ${t}
        </span>
        <div style="flex: 1"></div>
        <u-copy-button
          .value=${e}
        ></u-copy-button>
      </div>

      <pre class="hljs">${rt(Ur.highlight(e,{language:t}).value)}</pre>

      <div hidden aria-hidden="true">
        <slot @slotchange=${this.handleSlotChange}></slot>
      </div>
    `}};fe.styles=[zi(fe,fe,"styles"),Fi];dt([h({type:Boolean,reflect:!0})],fe.prototype,"loading",2);dt([h({type:Boolean,reflect:!0})],fe.prototype,"headless",2);dt([h({type:String,reflect:!0})],fe.prototype,"lang",2);dt([h({type:String})],fe.prototype,"value",2);dt([z()],fe.prototype,"isCopied",2);fe=dt([E("u-code-block")],fe);const Ii=S`
  :host {
    position: relative;
    display: flex;
    flex-direction: row;
    align-items: center;
    min-width: 0;
    max-width: 200px;
    gap: 10px;
    padding: 8px 10px;
    border: 1px solid var(--u-border-color);
    border-radius: 8px;
    background-color: var(--u-neutral-50);
    overflow: visible;
    cursor: pointer;
    transition: background-color 0.12s ease, border-color 0.12s ease;
    font-size: 14px;
  }
  :host(:hover) {
    background-color: var(--u-neutral-100);
    border-color: var(--u-border-color-strong);
  }

  u-icon[name="file-earmark-x"] {
    color: var(--u-red-600);
  }

  .thumbnail {
    position: relative;
    flex-shrink: 0;
    width: 36px;
    height: 36px;
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--u-icon-color);
    font-size: 16px;
    border-radius: 6px;
    background-color: var(--u-neutral-200);
    transition: background 0.15s ease;
    overflow: hidden;
  }

  .info {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .name {
    font-size: 13px;
    font-weight: 500;
    color: var(--u-txt-color-strong);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .meta {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 6px;
    color: var(--u-txt-color-weak);
    font-size: 10px;
  }

  .type {
    padding: 1px 5px;
    border-radius: 4px;
    background-color: var(--u-neutral-200);
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  .size {
    font-variant-numeric: tabular-nums;
  }

  .download-btn {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    color: var(--u-neutral-0);
    font-size: 14px;
    border-radius: 6px;
    background-color: color-mix(in srgb, var(--u-neutral-900) 70%, transparent);
    opacity: 0;
    pointer-events: none;
    transition: opacity 0.15s ease;
  }
  .thumbnail:hover .download-btn {
    opacity: 1;
    pointer-events: auto;
  }
  .download-btn:hover {
    background-color: color-mix(in srgb, var(--u-neutral-900) 85%, transparent);
  }

  .remove-btn {
    position: absolute;
    z-index: 10;
    top: -8px;
    right: -8px;
    border-radius: 50%;
    font-size: 10px;
    background-color: var(--u-neutral-600);
    color: var(--u-neutral-0);
    opacity: 0;
    pointer-events: none;
    transition: opacity 0.12s ease, background-color 0.12s ease;
  }
  :host(:hover) .remove-btn {
    opacity: 1;
    pointer-events: auto;
  }
  .remove-btn:hover {
    background-color: var(--u-red-600);
  }
`;var Mi=Object.defineProperty,Vi=Object.getOwnPropertyDescriptor,Hi=Object.getPrototypeOf,Wi=Reflect.get,Ke=(t,e,r,o)=>{for(var s=o>1?void 0:o?Vi(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Mi(e,r,s),s},Ki=(t,e,r)=>Wi(Hi(t),r,e);let oe=class extends j{constructor(){super(...arguments),this.removable=!1,this.handleDownloadClick=t=>{if(t.stopPropagation(),!this.url)return;const e=document.createElement("a");e.href=this.url,e.download=this.name||"unknown-file",e.target="_blank",e.rel="noopener noreferrer",document.body.appendChild(e),e.click(),e.remove()},this.handleRemoveClick=t=>{t.stopPropagation(),this.fire("remove")&&this.remove()}}render(){return y`
      <div class="thumbnail">
        <u-spinner 
          ?hidden=${this.status!=="uploading"}
        ></u-spinner>  
        <u-icon
          ?hidden=${this.status!=="error"}
          lib="bootstrap" 
          name="file-earmark-x"
        ></u-icon>
        <u-icon
          ?hidden=${this.status&&this.status!=="idle"}
          lib="bootstrap" 
          name=${this.resolveIcon(this.type)}
        ></u-icon>
        <u-button class="download-btn"
          ?hidden=${!this.url||this.status==="uploading"||this.status==="error"}
          title="Download"
          @click=${this.handleDownloadClick}>
          <u-icon lib="bootstrap" name="download"></u-icon>
        </u-button>
      </div>

      <div class="info">
        <div class="name">${this.name}</div>
        <div class="meta">
          <span class="type">
            ${this.resolveExt(this.name,this.type)}
          </span>
          <span class="size">
            ${this.formatSize(this.size||0)}
          </span>
        </div>
      </div>
      
      <u-button class="remove-btn"
        ?hidden=${!this.removable}
        title="Remove"
        @click=${this.handleRemoveClick}>
        <u-icon lib="internal" name="x-lg"></u-icon>
      </u-button>
    `}resolveIcon(t){return t?t.startsWith("image/")?"file-earmark-image":t.startsWith("video/")?"file-earmark-play":t.startsWith("audio/")?"file-earmark-music":t==="application/pdf"?"file-earmark-pdf":["application/json","application/javascript","application/typescript","application/xml","text/html","text/css","text/javascript","text/x-python","text/x-java-source"].includes(t)||t.startsWith("text/x-")?"file-earmark-code":["application/vnd.ms-excel","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet","text/csv"].includes(t)?"file-earmark-spreadsheet":["application/zip","application/x-zip-compressed","application/x-tar","application/x-rar-compressed","application/gzip","application/x-7z-compressed"].includes(t)?"file-earmark-zip":t.startsWith("text/")?"file-earmark-text":"file-earmark":"file-earmark"}resolveExt(t,e){if(t){const o=t.lastIndexOf(".");if(o!==-1&&o<t.length-1)return t.slice(o+1).toLowerCase()}return e?{"image/jpeg":"jpg","image/png":"png","image/gif":"gif","image/webp":"webp","image/svg+xml":"svg","application/pdf":"pdf","text/plain":"txt","text/csv":"csv","application/json":"json","application/zip":"zip"}[e]??e.split("/").pop()??"":""}formatSize(t){return t<1024?`${t} B`:t<1024**2?`${(t/1024).toFixed(1)} KB`:t<1024**3?`${(t/1024**2).toFixed(1)} MB`:`${(t/1024**3).toFixed(2)} GB`}};oe.styles=[Ki(oe,oe,"styles"),Ii];Ke([h({type:Boolean,reflect:!0})],oe.prototype,"removable",2);Ke([h({type:String,reflect:!0})],oe.prototype,"status",2);Ke([h({type:String})],oe.prototype,"name",2);Ke([h({type:String})],oe.prototype,"type",2);Ke([h({type:Number})],oe.prototype,"size",2);Ke([h({type:String})],oe.prototype,"url",2);oe=Ke([E("u-file-block")],oe);function Gi(t=r=>r,e=","){return{fromAttribute:r=>{if(r)return r.split(e).map(o=>t(o.trim()))},toAttribute:r=>r?r.join(e):null}}function Pr(t=JSON.parse){return{fromAttribute:e=>{if(e)try{return t(e)}catch{console.warn(`Failed to parse JSON attribute: ${e}`);return}},toAttribute:e=>{if(e===void 0)return null;try{return JSON.stringify(e)}catch{return console.warn("Failed to stringify JSON attribute:",e),null}}}}const qi=S`
  :host {
    --indent-size: 2em;
    
    --json-property-color: #0451a5;
    --json-string-color: #a31515;
    --json-number-color: #098658;
    --json-boolean-color: #0000ff;
    --json-null-color: #0000ff;
    --json-preview-color: #999999;
    --json-arrow-color: #666968;
    --json-guide-color: #c0c0c0;
  }
  :host-context([theme="dark"]) {
    --json-property-color: #9cdcfe;
    --json-string-color: #ce9178;
    --json-number-color: #b5cea8;
    --json-boolean-color: #569cd6;
    --json-null-color: #569CD6;
    --json-preview-color: #999999;
    --json-arrow-color: #d4d4d4;
    --json-guide-color: #3c3c3c;
  }

  :host {
    display: block;
    font-family: inherit;
    font-size: inherit;
    line-height: 1.5;
  }

  .key {
    color: var(--json-property-color);
    display: inline-flex;
    align-items: center;
  }
  .key[collapsable] {
    cursor: pointer;
    user-select: none;
  }
  .key[collapsable]::before {
    display: inline-flex;
    font-size: 0.5em;
    content: '▶';
    align-items: center;
    justify-content: center;

    transform: rotate(90deg);
    transition: transform 0.2s ease-out;
    margin-right: 0.5em;

    color: var(--json-arrow-color);
  }
  .key[collapsable][collapsed]::before {
    transform: rotate(0);
  }

  .preview {
    color: var(--json-preview-color);
    margin-left: 0.5em;
  }

  .string {
    color: var(--json-string-color);
  }

  .number {
    color: var(--json-number-color);
  }

  .boolean {
    color: var(--json-boolean-color);
  }

  .null {
    color: var(--json-null-color);
  }

  /* object styles */
  ul {
    position: relative;
    list-style: none;
    margin: 0;
    padding: 0;
    clear: both;
  }

  /* property styles */
  li {
    position: relative;
    list-style: none;
    outline: none;
  }

  /* nested property styles */
  li ul > li {
    position: relative;
    margin-left: var(--indent-size);
    padding-left: 0px;
  }

  /* guide line styles */
  ul ul::before {
    content: '';
    border-left: 1px solid var(--json-guide-color);
    position: absolute;
    left: calc(1.5em / 2 - 1px);
    top: 0.2em;
    bottom: 0.2em;
  }
`;var Ji=Object.defineProperty,Xi=Object.getOwnPropertyDescriptor,Yi=Object.getPrototypeOf,Qi=Reflect.get,ir=(t,e,r,o)=>{for(var s=o>1?void 0:o?Xi(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Ji(e,r,s),s},Zi=(t,e,r)=>Qi(Yi(t),r,e);let Pe=class extends j{constructor(){super(...arguments),this.expanded=!0,this.value={},this.state={},this.isValueType=t=>t!==Object(t)}connectedCallback(){super.connectedCallback(),this.setAttribute("tabindex","0")}willUpdate(t){super.willUpdate(t),(t.has("value")||t.has("expanded"))&&(this.state=this.setState(this.value))}render(){return y`
      <div part="base">
        ${this.renderNode(this.value)}
      </div>
    `}renderNode(t,e=""){return this.isValueType(t)?this.renderValue(t):this.renderObject(t,e)}renderValue(t){const e=t===null?"null":Array.isArray(t)?"array":typeof t=="object"?"object":typeof t;return y`
      <span part="${e}" class="${e}" role="treeitem">
        ${JSON.stringify(t)}
      </span>
    `}renderObject(t,e){return y`
      <ul part="object" role="group">
        ${Qo(Object.entries(t),([r,o])=>{const s=e?`${e}.${r}`:r,n=this.isValueType(o),i=this.state[s]??this.expanded;return y`
            <li part="property" role="treeitem"
                data-path="${s}"
                aria-expanded="${i?"true":"false"}">
              <span part="key"
                    class="key"
                    ?collapsable="${!n}"
                    ?collapsed="${!n&&!i}"
                    @click=${n?null:()=>this.handlePropertyKeyClick(s)}>
                ${r}:
                ${Nr(!n&&!i,()=>this.renderPreview(o))}
              </span>

              ${Nr(n||i,()=>this.renderNode(o,s))}
            </li>`})}
      </ul>
    `}renderPreview(t){return y`
      <span part="preview" class="preview">
        ${Array.isArray(t)?t.length===0?"[ ]":`[ ${t.length} items ]`:Object.keys(t).length===0?"{ }":`{ ${Object.keys(t).length} properties }`}
      </span>
    `}setState(t,e=""){if(typeof t!="object"||t===null)return{};const r={};return Object.entries(t).forEach(([o,s])=>{const n=e?`${e}.${o}`:o;r[n]=this.expanded,Object.assign(r,this.setState(s,n))}),r}handlePropertyKeyClick(t){const e=this.state[t]??!1;this.state={...this.state,[t]:!e}}};Pe.styles=[Zi(Pe,Pe,"styles"),qi];ir([h({type:Boolean})],Pe.prototype,"expanded",2);ir([h({type:Object,converter:Pr()})],Pe.prototype,"value",2);ir([z()],Pe.prototype,"state",2);Pe=ir([E("u-json-block")],Pe);const ea=S`
  :host {
    display: inline-flex;
    flex-direction: row;
    align-items: center;
    gap: 4px;
    color: var(--u-neutral-800);
    font-size: 10px;
    border: 1px solid var(--u-neutral-300);
    border-radius: 9999px;
    background-color: var(--u-neutral-100);
    padding: 2px 6px;
    transition: background-color 0.2s ease-in-out;
    cursor: pointer;
  }
  :host(:hover) {
    color: var(--u-neutral-100);
    background-color: var(--u-neutral-900);
  }

  a {
    min-width: 1em;
    max-width: 6em;
    display: inline-block;
    text-decoration: none;
    color: inherit;
    line-height: 1.5;
    white-space: nowrap;
    text-overflow: ellipsis;
    overflow: hidden;
  }

  u-icon {
    color: inherit;
    font-size: inherit;
  }

  u-tooltip {
    padding: 0;
    border: none;
    background-color: transparent;
    box-shadow: none;
  }
  u-tooltip[visible] {
    opacity: 1;
  }
`;var ta=Object.defineProperty,ra=Object.getOwnPropertyDescriptor,oa=Object.getPrototypeOf,sa=Reflect.get,qo=(t,e,r,o)=>{for(var s=o>1?void 0:o?ra(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&ta(e,r,s),s},na=(t,e,r)=>sa(oa(t),r,e);let et=class extends j{render(){return y`
      <a href="${xe(this.href)}" target="_blank" rel="noopener noreferrer"
        @click=${this.handleAnchorClick}>
        <slot></slot>
      </a>

      <u-icon lib="bootstrap" name="box-arrow-up-right"></u-icon>

      <u-tooltip interactive placement="bottom" distance="4">
        <slot name="tooltip"></slot>
      </u-tooltip>
    `}handleAnchorClick(t){this.href||(t.preventDefault(),t.stopPropagation())}};et.styles=[na(et,et,"styles"),ea];qo([h({type:String})],et.prototype,"href",2);et=qo([E("u-ref-tag")],et);var ia=S`
  .error-container {
    display: flex;
    flex-direction: column;
    border: 1px solid var(--u-red-200);
    border-radius: 10px;
    background: var(--u-red-0);
    color: var(--u-red-700);
    overflow: hidden;
    font-size: 0.875em;
    line-height: 1.5;
  }

  .error-header {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5em;
    padding: 0.55em 0.9em;
    background: var(--u-red-50);
    border-bottom: 1px solid var(--u-red-200);
  }

  .error-icon {
    font-size: 0.95em;
    color: var(--u-red-500);
    flex-shrink: 0;
  }

  .error-title {
    font-weight: 700;
    letter-spacing: 0.01em;
    flex: 1;
  }

  .error-tag {
    font-family: var(--u-font-mono);
    font-size: 0.85em;
    padding: 0.1em 0.45em;
    color: var(--u-red-600);
    border: 1px solid var(--u-red-200);
    border-radius: 4px;
    background: var(--u-red-100);
  }

  .error-body {
    display: flex;
    flex-direction: column;
    padding: 0.6em 0.9em;
    gap: 0.45em;
  }

  .error-row {
    display: flex;
    flex-direction: row;
    align-items: baseline;
    gap: 0.6em;
    min-height: 1.6em;
  }

  .error-label {
    flex-shrink: 0;
    width: 5em;
    font-size: 0.78em;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    opacity: 0.55;
    padding-top: 0.15em;
  }

  .error-type {
    font-family: var(--u-font-mono);
    font-size: 0.85em;
    padding: 0.1em 0.4em;
    color: var(--u-red-700);
    background: var(--u-red-100);
    border-radius: 4px;
  }

  .error-message {
    margin: 0;
    flex: 1;
    white-space: pre-wrap;
    word-break: break-all;
    font-family: var(--u-font-mono);
    font-size: 0.9em;
    opacity: 0.85;
    line-height: 1.55;
  }
`,Rr=class extends j{static{this.styles=[super.styles,ia]}connectedCallback(){super.connectedCallback(),queueMicrotask(()=>this.load())}static buildHTML(t,e={}){const r=customElements.getName(this);return r?Bt(r,e,`<script type="application/json">${(typeof t=="string"?t:JSON.stringify(t)).replace(/&/g,"\\u0026").replace(/</g,"\\u003c").replace(/>/g,"\\u003e").replace(/\u2028/g,"\\u2028").replace(/\u2029/g,"\\u2029").replace(/-->/g,"--\\>")}<\/script>`):(console.warn(`Custom element for ${this.name} is not defined.`),"")}async load(t){try{if(!t){const e=this.querySelector('script[type="application/json"]');if(!e)throw new Error('Missing <script type="application/json">');t=JSON.parse(e.textContent??"{}"),e.remove()}if(typeof t!="object"||t===null||Array.isArray(t))throw new Error("Invalid JSON data: expected an object at the top level");Object.assign(this,t)}catch(e){await this.error(e)}}async error(t){if(await this.updateComplete,!this.shadowRoot)return;const e=this.localName,r=t instanceof Error?t.constructor.name:"UnknownError",o=t instanceof Error?t.message:String(t);this.replace(y`
      <div class="error-container">
        <div class="error-header">
          <u-icon class="error-icon" lib="internal" name="exclamation-triangle-fill"></u-icon>
          <span class="error-title">Render Error</span>
          <code class="error-tag">&lt;${e}&gt;</code>
        </div>
        <div class="error-body">
          <div class="error-row">
            <span class="error-label">Type</span>
            <code class="error-type">${r}</code>
          </div>
          <div class="error-row">
            <span class="error-label">Message</span>
            <pre class="error-message">${o}</pre>
          </div>
        </div>
      </div>
    `)}};const aa=S`
  :host {
    display: block;
    width: 380px;
    padding: 6px;
    border: 1px solid var(--u-border-color);
    border-radius: 8px;
    background: var(--u-panel-bg-color);
  }

  a {
    display: flex;
    flex-direction: column;
    padding: 6px;
    color: inherit;
    text-decoration: none;
    transition: all 0.2s ease;
  }
  a:hover {
    color: var(--u-blue-600);
    background-color: var(--u-neutral-50);
  }

  .header {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
  }

  .favicon {
    width: 20px;
    height: 20px;
    object-fit: contain;
  }

  .title {
    font-size: 14px;
    font-weight: 600;
    line-height: 1.4;
    white-space: nowrap;
    text-overflow: ellipsis;
    overflow: hidden;
  }

  .badge {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 4px;
    font-size: 12px;
    line-height: 1;
    font-weight: 600;
    padding: 4px 8px;
    border-radius: 4px;
    white-space: nowrap;
  }
  .badge[type="web"] {
    color: var(--u-blue-700);
    background: var(--u-blue-0);
  }
  .badge[type="document"] {
    color: var(--u-green-700);
    background: var(--u-green-0);
  }
  .badge u-icon {
    color: inherit;
  }

  .body {
    display: -webkit-box;
    color: var(--u-txt-color-weak);
    font-size: 12px;
    line-height: 1.5;
    -webkit-line-clamp: 3;
    -webkit-box-orient: vertical;
    overflow: hidden;
    text-overflow: ellipsis;
    margin-top: 8px;
  }

  .footer {
    display: flex;
    gap: 4px;
    flex-wrap: wrap;
    margin-top: 8px;
  }

  .tag {
    font-size: 10px;
    line-height: 1.4;
    color: var(--u-txt-color-weak);
    background: var(--u-neutral-100);
    padding: 2px 6px;
    border-radius: 4px;
    white-space: nowrap;
  }
`;var la=Object.defineProperty,ca=Object.getOwnPropertyDescriptor,ua=Object.getPrototypeOf,da=Reflect.get,ht=(t,e,r,o)=>{for(var s=o>1?void 0:o?ca(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&la(e,r,s),s},ha=(t,e,r)=>da(ua(t),r,e);let Z=class extends Rr{constructor(){super(...arguments),this.type="web",this.title=""}render(){return y`
      <a href="${xe(this.url)}" target="_blank" rel="noopener noreferrer"
        @click=${this.handleAnchorClick}>
        <div class="header">
          <img class="favicon" 
            src="${this.getFaviconUrl(this.url)}" 
            alt="favicon"
          />
          <div class="title">
            ${this.title||this.getDomainName(this.url)}
          </div>

          <div style="flex: 1;"></div>

          <div class="badge" type=${this.type}>
            <u-icon 
              lib="bootstrap" 
              name=${this.type==="web"?"globe":"file-earmark"}
            ></u-icon>
            ${this.type.toUpperCase()}
          </div>
        </div>

        <div class="body">
          ${this.snippet}
        </div>
        
        <div class="footer" ?hidden=${!this.tags||this.tags.length===0}>
          ${this.tags?.map(t=>y`<span class="tag">${t}</span>`)}
        </div>
      </a>
    `}error(t){return Promise.resolve(console.error("Error in URefCard:",t))}handleAnchorClick(t){this.url||(t.preventDefault(),t.stopPropagation())}getFaviconUrl(t){if(!t)return"/favicon.ico";try{return`https://www.google.com/s2/favicons?sz=64&domain=${new URL(t).hostname}`}catch{return"/favicon.ico"}}getDomainName(t){if(!t)return"";try{return new URL(t).hostname}catch{return""}}};Z.styles=[ha(Z,Z,"styles"),aa];ht([h({type:String,reflect:!0})],Z.prototype,"type",2);ht([h({type:String})],Z.prototype,"url",2);ht([h({type:String})],Z.prototype,"title",2);ht([h({type:String})],Z.prototype,"snippet",2);ht([h({type:Array,converter:Gi(t=>t)})],Z.prototype,"tags",2);Z=ht([E("u-ref-card")],Z);const pa=S`
  :host {
    display: flex;
    flex-direction: column;
    width: 380px;
    border: 1px solid var(--u-border-color);
    border-radius: 8px;
    background: var(--u-panel-bg-color);
    overflow: hidden;
  }

  .header {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    padding: 6px 8px;
    color: var(--u-txt-color-weak);
    background-color: var(--u-neutral-100);
    border-bottom: 1px solid var(--u-border-color);
    user-select: none;
  }

  .nav-button {
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0;
    border: none;
    background-color: transparent;
    color: inherit;
    font-size: 16px;
    cursor: pointer;
  }
  .nav-button:hover u-icon {
    opacity: 0.6;
  }
  .nav-button u-icon {
    color: inherit;
  }

  .page-indicator {
    font-size: 12px;
    font-weight: 600;
  }

  .viewport {
    width: 100%;
    overflow: hidden;
  }

  .track {
    display: flex;
    flex-direction: row;
    width: 100%;
    will-change: transform;
    transition: transform 260ms ease;
  }

  /* slot 안의 각 카드가 한 페이지(100%)를 차지하도록 */
  ::slotted(u-ref-card) {
    flex: 0 0 100%;
    border: none;
  }
`;var fa=Object.defineProperty,ga=Object.getOwnPropertyDescriptor,ma=Object.getPrototypeOf,ba=Reflect.get,Ar=(t,e,r,o)=>{for(var s=o>1?void 0:o?ga(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&fa(e,r,s),s},va=(t,e,r)=>ba(ma(t),r,e);let ze=class extends j{constructor(){super(...arguments),this.cards=[],this.currentIndex=0}render(){return y`
      <div class="header" ?hidden=${this.cards.length<=1}>
        <button class="nav-button"
          @click=${this.handlePreviousButtonClick}>
          <u-icon lib="internal" name="chevron-left"></u-icon>
        </button>
        
        <span class="page-indicator">
          ${this.currentIndex+1} / ${this.cards.length}
        </span>
        
        <button class="nav-button"
          @click=${this.handleNextButtonClick}>
          <u-icon lib="internal" name="chevron-right"></u-icon>
        </button>
      </div>
      
      <div class="viewport">
        <div class="track" style=${`transform: translateX(-${this.currentIndex*100}%);`}>
          <slot @slotchange=${this.handleSlotChange}></slot>
        </div>
      </div>
    `}switch(t){const e=this.cards.length;e!==0&&(this.currentIndex=t<0?e-1:t>=e?0:t)}handlePreviousButtonClick(){this.switch(this.currentIndex-1)}handleNextButtonClick(){this.switch(this.currentIndex+1)}handleSlotChange(t){const r=t.target.assignedElements({flatten:!0});this.cards=r.filter(o=>o instanceof Z),this.switch(0)}};ze.styles=[va(ze,ze,"styles"),pa];Ar([z()],ze.prototype,"cards",2);Ar([z()],ze.prototype,"currentIndex",2);ze=Ar([E("u-ref-card-group")],ze);var ya=S`
  :host {
    --skeleton-width: 100%;
    --skeleton-height: 1em;
    --skeleton-color: var(--u-neutral-200);
    --skeleton-shimmer-color: var(--u-neutral-100);
  }

  :host {
    display: inline-block;
    width: var(--skeleton-width);
    height: var(--skeleton-height);
    background-color: var(--skeleton-color);
    animation: none;
  }

  /* 모양 설정 */
  :host([shape="rectangle"]) {
    border-radius: 4px;
  }
  :host([shape="circle"]) {
    border-radius: 50%;
  }
  :host([shape="rounded"]) {
    border-radius: 9999px;
  }

  /* 애니메이션 효과 설정 */
  :host([effect="pulse"]) {
    animation: pulse 1.5s ease-in-out infinite;
  }
  :host([effect="shimmer"]) {
    position: relative;
    overflow: hidden;
  }
  :host([effect="shimmer"])::after {
    content: '';
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background: linear-gradient(
      90deg,
      transparent,
      var(--skeleton-shimmer-color),
      transparent
    );
    animation: shimmer 1.5s infinite;
    transform: translateX(-100%);
  }

  @keyframes pulse {
    0%, 100% {
      opacity: 1;
    }
    50% {
      opacity: 0.4;
    }
  }

  @keyframes shimmer {
    100% {
      transform: translateX(100%);
    }
  }
`,nt=class extends j{constructor(...e){super(...e),this.shape="rectangle",this.effect="shimmer"}static{this.styles=[super.styles,ya]}updated(e){super.updated(e),e.has("width")&&this.width&&this.style.setProperty("--skeleton-width",this.width),e.has("height")&&this.height&&this.style.setProperty("--skeleton-height",this.height)}render(){return y`<slot></slot>`}};b([h({type:String,reflect:!0}),v("design:type",Object)],nt.prototype,"shape",void 0);b([h({type:String,reflect:!0}),v("design:type",Object)],nt.prototype,"effect",void 0);b([h({type:String}),v("design:type",String)],nt.prototype,"width",void 0);b([h({type:String}),v("design:type",String)],nt.prototype,"height",void 0);nt=b([E("u-skeleton")],nt);const wa=S`
  :host {
    display: block;
    width: 100%;
    margin: 8px 0;
    border: 1px solid var(--u-border-color);
    border-radius: 8px;
    overflow: hidden;
  }

  .toolbar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 6px 12px;
    background-color: var(--u-neutral-100);
    border-bottom: 1px solid var(--u-border-color);
    gap: 8px;
  }

  .toolbar-left {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: 1;
    min-width: 0;
  }

  .toolbar-search {
    flex: 1;
    max-width: 200px;
    font-size: 12px;
  }

  .toolbar-count {
    font-size: 12px;
    color: var(--u-txt-color-weak);
  }

  .toolbar-right {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-shrink: 0;
  }

  .toolbar-right u-button {
    font-size: 12px;
    padding: 0.25em 0.5em;
  }

  .table-wrapper {
    overflow-x: auto;
    overflow-y: auto;
    max-height: 480px;
    width: 100%;
  }

  table {
    width: 100%;
    border-collapse: collapse;
    font-size: 14px;
  }

  thead {
    position: sticky;
    top: 0;
  }

  thead tr {
    background-color: var(--u-neutral-100);
  }

  th {
    padding: 8px 12px;
    font-weight: 600;
    text-align: left;
    border-bottom: 2px solid var(--u-border-color);
    white-space: nowrap;
    cursor: pointer;
    user-select: none;
  }
  th:hover {
    background-color: var(--u-neutral-200);
  }
  th:hover .sort-icon {
    opacity: 0.7;
  }
  th[active] {
    color: var(--u-primary, #4a90e2);
  }
  th[active] .sort-icon {
    opacity: 1;
  }

  th .sort-icon {
    display: inline-flex;
    margin-left: 4px;
    font-size: 12px;
    opacity: 0.3;
    transition: opacity 0.15s;
  }

  tbody tr {
    background-color: var(--u-neutral-0);
  }
  tbody tr:hover td {
    background-color: var(--u-neutral-50, rgba(0,0,0,0.02));
  }
  tbody tr:last-child td {
    border-bottom: none;
  }

  td {
    padding: 8px 12px;
    border-bottom: 1px solid var(--u-border-color);
    vertical-align: top;
  }

  th[align="left"], td[align="left"] {
    text-align: left; 
  }
  th[align="center"], td[align="center"] { 
    text-align: center; 
  }
  th[align="right"], td[align="right"] { 
    text-align: right; 
  }
`;var xa=Object.defineProperty,ka=Object.getOwnPropertyDescriptor,$a=Object.getPrototypeOf,_a=Reflect.get,pt=(t,e,r,o)=>{for(var s=o>1?void 0:o?ka(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&xa(e,r,s),s},Ca=(t,e,r)=>_a($a(t),r,e);let se=class extends Rr{constructor(){super(...arguments),this.headers=[],this.rows=[],this.loading=!1,this.sort={index:-1,dir:"asc"},this.search="",this.searchTimer=null}render(){const t=this.getFilteredSortedRows();return y`
      <div class="toolbar">
        <div class="toolbar-left">
          <u-input
            class="toolbar-search"
            type="search"
            placeholder="Search..."
            .value=${this.search}
            @input=${this.handleSearchInput}
          >
            <u-icon slot="prefix" lib="internal" name="search"></u-icon>
          </u-input>
          <span class="toolbar-count">
            ${t.length} / ${this.rows.length} Rows
          </span>
        </div>
        <div class="toolbar-right">
          <u-button @click=${this.handleDownloadXLS} title="Excel Download">
            XLS
            <u-icon slot="suffix" lib="bootstrap" name="download"></u-icon>
          </u-button>
          <u-button @click=${this.handleDownloadCSV} title="CSV Download">
            CSV
            <u-icon slot="suffix" lib="bootstrap" name="download"></u-icon>
          </u-button>
        </div>
      </div>
      <div class="table-wrapper" scrollable>
        <table>
          <thead ?hidden=${!this.headers.length}>
            <tr>
              ${Oe(this.headers,(e,r)=>r,(e,r)=>{const o=this.sort.index===r;return y`
                  <th
                    ?active=${o}
                    align=${e.align??"left"}
                    @click=${()=>this.handleSortColumn(r)}
                  >
                    ${rt(e.text)}
                    <u-icon
                      class="sort-icon"
                      lib="bootstrap"
                      name=${o?this.sort.dir==="asc"?"sort-alpha-up":"sort-alpha-down":"arrow-down-up"}
                    ></u-icon>
                  </th>
                `})}
            </tr>
          </thead>
          <tbody>
            ${this.loading?y`
                <tr>
                  <td colspan=${this.headers.length||1}>
                    <u-skeleton width="80%" height="1.2em"></u-skeleton>
                    <u-skeleton width="60%" height="1.2em"></u-skeleton>
                    <u-skeleton width="70%" height="1.2em"></u-skeleton>
                    <u-skeleton width="50%" height="1.2em"></u-skeleton>
                  </td>
                </tr>
              `:Oe(t,(e,r)=>r,e=>y`
                <tr>
                  ${Oe(e,(r,o)=>o,r=>y`
                    <td align=${r.align??"left"}>
                      ${rt(this.renderHighlightedText(r.text))}
                    </td>
                  `)}
                </tr>
            `)}
          </tbody>
        </table>
      </div>
    `}renderHighlightedText(t){const e=this.search.trim();if(!e)return t;const r=e.replace(/[.*+?^${}()|[\]\\]/g,"\\$&"),o=new RegExp(`(${r})`,"gi"),s=String(t).split(o);return s.length===1?t:s.map((n,i)=>i%2===1?`<mark>${n}</mark>`:n).join("")}getFilteredSortedRows(){let t=this.rows;const e=this.search.trim();if(e){const s=e.toLowerCase();t=t.filter(n=>n.some(i=>i.text.toLowerCase().includes(s)))}const{index:r,dir:o}=this.sort;return r<0?t:[...t].sort((s,n)=>{const i=s[r]?.text??"",a=n[r]?.text??"",l=Number(i),c=Number(a),u=!isNaN(l)&&!isNaN(c)?l-c:i.localeCompare(a,void 0,{sensitivity:"base"});return o==="asc"?u:-u})}handleSearchInput(t){const e=t.target;this.searchCache=e.value||"",this.searchTimer!==null&&window.clearTimeout(this.searchTimer),this.loading=!0,this.searchTimer=window.setTimeout(()=>{this.search=this.searchCache||"",this.loading=!1,this.searchTimer=null},250)}handleSortColumn(t){this.sort.index===t?this.sort={...this.sort,dir:this.sort.dir==="asc"?"desc":"asc"}:this.sort={index:t,dir:"asc"}}handleDownloadCSV(){const t=o=>o.includes(",")||o.includes('"')||o.includes(`
`)?`"${o.replace(/"/g,'""')}"`:o,e=[this.headers.map(o=>t(o.text)).join(","),...this.rows.map(o=>o.map(s=>t(s.text)).join(","))],r=new Blob([e.join(`
`)],{type:"text/csv;charset=utf-8;"});this.triggerDownload(`table-${Date.now()}.csv`,r)}handleDownloadXLS(){const t=n=>n.replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;"),e=n=>`<Cell><Data ss:Type="String">${t(n)}</Data></Cell>`,r=n=>`<Row>${n.map(e).join("")}</Row>`,o=['<?xml version="1.0" encoding="UTF-8"?>','<?mso-application progid="Excel.Sheet"?>','<Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"',' xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">','<Worksheet ss:Name="Sheet1"><Table>',r(this.headers.map(n=>n.text)),...this.rows.map(n=>r(n.map(i=>i.text))),"</Table></Worksheet></Workbook>"].join(`
`),s=new Blob([o],{type:"application/vnd.ms-excel;charset=utf-8;"});this.triggerDownload(`table-${Date.now()}.xls`,s)}triggerDownload(t,e){const r=URL.createObjectURL(e),o=document.createElement("a");o.href=r,o.download=t,o.click(),URL.revokeObjectURL(r)}};se.styles=[Ca(se,se,"styles"),wa];pt([h({type:Array})],se.prototype,"headers",2);pt([h({type:Array})],se.prototype,"rows",2);pt([z()],se.prototype,"loading",2);pt([z()],se.prototype,"sort",2);pt([z()],se.prototype,"search",2);se=pt([E("u-table-block")],se);const Sa=S`
  :host {
    display: contents;
  }

  /* ── 스켈레톤: 카드(좌) + 문서(우) ── */
  .sk-container {
    display: flex;
    flex-direction: row;
    align-items: stretch;
    gap: 1em;
    margin: 0.75em 0;
    width: 100%;
  }

  .sk-card {
    flex: 0 0 40%;
    display: flex;
    flex-direction: column;
    gap: 0.55em;
  }

  .sk-doc {
    flex: 1;
    display: flex;
    flex-direction: column;
    justify-content: space-between;
  }
`;var Ea=Object.defineProperty,Oa=Object.getOwnPropertyDescriptor,ja=Object.getPrototypeOf,Pa=Reflect.get,ft=(t,e,r,o)=>{for(var s=o>1?void 0:o?Oa(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Ea(e,r,s),s},Ra=(t,e,r)=>Pa(ja(t),r,e);const Aa=["innerHTML","outerHTML","textContent","innerText","outerText","srcdoc"];let ne=class extends Rr{constructor(){super(...arguments),this.loading=!1,this.blacklist=Aa,this.element=null}willUpdate(t){super.willUpdate(t),t.has("tag")&&this.updateElement(this.tag),t.has("properties")&&this.element&&this.updateProperties(this.element,this.properties),t.has("blacklist")&&this.element&&this.properties&&this.updateProperties(this.element,this.properties)}render(){return this.loading===!0?y`
        <div class="sk-container">
          <div class="sk-card">
            <u-skeleton effect="shimmer" width="100%" height="5em"   shape="rectangle"></u-skeleton>
            <u-skeleton effect="shimmer" width="80%"  height="0.8em" shape="rounded"></u-skeleton>
            <u-skeleton effect="shimmer" width="40%"  height="0.75em" shape="rounded"></u-skeleton>
          </div>
          <div class="sk-doc">
            <u-skeleton effect="shimmer" width="40%"  height="0.75em" shape="rounded"></u-skeleton>
            <u-skeleton effect="shimmer" width="80%"  height="0.75em" shape="rounded"></u-skeleton>
            <u-skeleton effect="shimmer" width="60%" height="0.8em"  shape="rounded"></u-skeleton>
            <u-skeleton effect="shimmer" width="100%" height="0.8em"  shape="rounded"></u-skeleton>
            <u-skeleton effect="shimmer" width="80%"  height="0.8em"  shape="rounded"></u-skeleton>
          </div>
        </div>`:this.element!=null?this.element:M}async error(t){this.loading||await super.error(t)}updateElement(t){if(t=t?.trim(),!t){this.element=null,super.error(new Error("Tag is required"));return}if(!customElements.get(t)){this.element=null,super.error(new Error(`Unknown tag: ${t}`));return}this.element=document.createElement(t)}updateProperties(t,e){if(this.validateProperties(e))try{Object.assign(t,e)}catch(r){super.error(r)}else super.error(new Error(`Not allowed properties: ${JSON.stringify(e)}`))}validateProperties(t){if(typeof t!="object"||t===null||Array.isArray(t))return!1;const e=t;for(const r of Object.keys(e))if(this.blacklist.includes(r))return!1;return!0}};ne.styles=[Ra(ne,ne,"styles"),Sa];ft([h({type:Boolean,reflect:!0})],ne.prototype,"loading",2);ft([h({type:String})],ne.prototype,"tag",2);ft([h({type:Object})],ne.prototype,"properties",2);ft([h({type:Array})],ne.prototype,"blacklist",2);ft([z()],ne.prototype,"element",2);ne=ft([E("u-view")],ne);class La{constructor(){this.map={},this.idx=0}reset(){this.map={},this.idx=0}store(e){const r=`<!--ref:${this.idx++}-->`;return this.map[r]=e,r}restore(e){return this.idx===0?e:e.replace(/<!--ref:\d+-->/g,r=>this.map[r]??"")}}const Da=S`
  /* github-markdown styles */
  :host {
    --fontStack-monospace: ui-monospace, SFMono-Regular, SF Mono, Menlo, Consolas, Liberation Mono, monospace;
    --base-size-4: 0.25rem;
    --base-size-8: 0.5rem;
    --base-size-16: 1rem;
    --base-size-24: 1.5rem;
    --base-size-40: 2.5rem;
    --base-text-weight-normal: 400;
    --base-text-weight-medium: 500;
    --base-text-weight-semibold: 600;

    --focus-outlineColor: #0969da;
    --fgColor-default: #1f2328;
    --fgColor-muted: #59636e;
    --fgColor-accent: #0969da;
    --bgColor-default: #ffffff;
    --bgColor-muted: #f6f8fa;
    --bgColor-neutral-muted: #818b981f;
    --bgColor-attention-muted: #fff8c5;
    --borderColor-default: #d1d9e0;
    --borderColor-muted: #d1d9e0b3;
    --borderColor-neutral-muted: #d1d9e0b3;
  }
  :host-context([theme="dark"]) {
    --focus-outlineColor: #1f6feb;
    --fgColor-default: #f0f6fc;
    --fgColor-muted: #9198a1;
    --fgColor-accent: #4493f8;
    --bgColor-default: #0d1117;
    --bgColor-muted: #151b23;
    --bgColor-neutral-muted: #656c7633;
    --bgColor-attention-muted: #bb800926;
    --borderColor-default: #3d444d;
    --borderColor-muted: #3d444db3;
    --borderColor-neutral-muted: #3d444db3;
  }

  :host {
    display: block;
    width: 100%;
    height: auto;
    -ms-text-size-adjust: 100%;
    -webkit-text-size-adjust: 100%;
    margin: 0;
    font-family: -apple-system,BlinkMacSystemFont,"Segoe UI","Noto Sans",Helvetica,Arial,sans-serif,"Apple Color Emoji","Segoe UI Emoji";
    font-size: 16px;
    line-height: 1.5;
    word-wrap: break-word;
  }

  /* Custom Block Start */
  u-code-block {
    margin-bottom: var(--base-size-16);
  }

  .katex math {
    margin: var(--base-size-16) 0;
  }

  u-tooltip {
    padding: 0;
    border: none;
    box-shadow: none;
    background-color: transparent;
  }
  u-tooltip[visible] {
    opacity: 1;
  }
  /* Custom Block End */

  a {
    background-color: transparent;
    color: var(--fgColor-accent);
    text-decoration: none;
  }

  a:hover {
    text-decoration: underline;
  }

  a:focus,
  input[type=checkbox]:focus {
    outline: 2px solid var(--focus-outlineColor);
    outline-offset: -2px;
    box-shadow: none;
  }

  a:focus:not(:focus-visible),
  input[type=checkbox]:focus:not(:focus-visible) {
    outline: solid 1px transparent;
  }

  a:focus-visible,
  input[type=checkbox]:focus-visible {
    outline: 2px solid var(--focus-outlineColor);
    outline-offset: -2px;
    box-shadow: none;
  }

  a:not([class]):focus,
  a:not([class]):focus-visible,
  input[type=checkbox]:focus,
  input[type=checkbox]:focus-visible {
    outline-offset: 0;
  }

  a:not([href]) {
    color: inherit;
    text-decoration: none;
  }

  b,
  strong {
    font-weight: var(--base-text-weight-semibold, 600);
  }

  em {
    font-style: italic;
  }

  del {
    text-decoration: line-through;
  }

  h1 {
    margin: .67em 0;
    font-weight: var(--base-text-weight-semibold, 600);
    padding-bottom: .3em;
    font-size: 2em;
    border-bottom: 1px solid var(--borderColor-muted);
  }

  h1,
  h2,
  h3,
  h4,
  h5,
  h6 {
    margin-top: var(--base-size-24);
    margin-bottom: var(--base-size-16);
    font-weight: var(--base-text-weight-semibold, 600);
    line-height: 1.25;
  }

  h2 {
    font-weight: var(--base-text-weight-semibold, 600);
    padding-bottom: .3em;
    font-size: 1.5em;
    border-bottom: 1px solid var(--borderColor-muted);
  }

  h3 {
    font-weight: var(--base-text-weight-semibold, 600);
    font-size: 1.25em;
  }

  h4 {
    font-weight: var(--base-text-weight-semibold, 600);
    font-size: 1em;
  }

  h5 {
    font-weight: var(--base-text-weight-semibold, 600);
    font-size: .875em;
  }

  h6 {
    font-weight: var(--base-text-weight-semibold, 600);
    font-size: .85em;
    color: var(--fgColor-muted);
  }

  h1 tt,
  h1 code,
  h2 tt,
  h2 code,
  h3 tt,
  h3 code,
  h4 tt,
  h4 code,
  h5 tt,
  h5 code,
  h6 tt,
  h6 code {
    padding: 0 .2em;
    font-size: inherit;
  }

  p {
    margin-top: 0;
    margin-bottom: 10px;
  }

  blockquote {
    margin: 0;
    padding: 0 1em;
    color: var(--fgColor-muted);
    border-left: .25em solid var(--borderColor-default);
  }

  blockquote>:first-child {
    margin-top: 0;
  }

  blockquote>:last-child {
    margin-bottom: 0;
  }

  ul,
  ol {
    margin-top: 0;
    margin-bottom: 0;
    padding-left: 2em;
  }

  ul ul,
  ul ol,
  ol ol,
  ol ul {
    margin-top: 0;
    margin-bottom: 0;
  }

  ol ol,
  ul ol {
    list-style-type: lower-roman;
  }

  ul ul ol,
  ul ol ol,
  ol ul ol,
  ol ol ol {
    list-style-type: lower-alpha;
  }

  ol[type="a s"] {
    list-style-type: lower-alpha;
  }

  ol[type="A s"] {
    list-style-type: upper-alpha;
  }

  ol[type="i s"] {
    list-style-type: lower-roman;
  }

  ol[type="I s"] {
    list-style-type: upper-roman;
  }

  ol[type="1"] {
    list-style-type: decimal;
  }

  div>ol:not([type]) {
    list-style-type: decimal;
  }

  li>p {
    margin-top: var(--base-size-16);
  }

  li+li {
    margin-top: .25em;
  }

  dl {
    padding: 0;
  }

  dl dt {
    padding: 0;
    margin-top: var(--base-size-16);
    font-size: 1em;
    font-style: italic;
    font-weight: var(--base-text-weight-semibold, 600);
  }

  dl dd {
    padding: 0 var(--base-size-16);
    margin-bottom: var(--base-size-16);
  }

  dd {
    margin-left: 0;
  }

  mark {
    background-color: var(--bgColor-attention-muted);
    color: var(--fgColor-default);
  }

  small {
    font-size: 90%;
  }

  sub,
  sup {
    font-size: 75%;
    line-height: 0;
    position: relative;
    vertical-align: baseline;
  }

  sub {
    bottom: -0.25em;
  }

  sup {
    top: -0.5em;
  }

  img {
    border-style: none;
    max-width: 100%;
    box-sizing: content-box;
    background-color: transparent;
  }

  .emoji {
    max-width: none;
    vertical-align: text-top;
    background-color: transparent;
  }

  code,
  kbd,
  pre,
  samp,
  tt {
    font-family: var(--fontStack-monospace);
    font-size: 1em;
  }

  tt,
  code,
  samp {
    font-size: 12px;
  }

  code,
  tt {
    padding: .2em .4em;
    margin: 0;
    font-size: 85%;
    font-weight: 600;
    white-space: break-spaces;
    background-color: var(--bgColor-neutral-muted);
    border-radius: 6px;
  }

  code br,
  tt br {
    display: none;
  }

  del code {
    text-decoration: inherit;
  }

  samp {
    font-size: 85%;
  }

  pre {
    margin-top: 0;
    margin-bottom: 0;
    font-size: 12px;
    word-wrap: normal;
  }

  pre code {
    font-size: 100%;
  }

  pre>code {
    padding: 0;
    margin: 0;
    word-break: normal;
    white-space: pre;
    background: transparent;
    border: 0;
  }

  .highlight {
    margin-bottom: var(--base-size-16);
  }

  .highlight pre {
    margin-bottom: 0;
    word-break: normal;
  }

  .highlight pre,
  pre {
    padding: var(--base-size-16);
    overflow: auto;
    font-size: 85%;
    line-height: 1.45;
    color: var(--fgColor-default);
    background-color: var(--bgColor-muted);
    border-radius: 6px;
  }

  pre code,
  pre tt {
    display: inline;
    max-width: auto;
    padding: 0;
    margin: 0;
    overflow: visible;
    line-height: inherit;
    word-wrap: normal;
    background-color: transparent;
    border: 0;
  }

  kbd {
    display: inline-block;
    padding: var(--base-size-4);
    font: 11px var(--fontStack-monospace);
    line-height: 10px;
    color: var(--fgColor-default);
    vertical-align: middle;
    background-color: var(--bgColor-muted);
    border: solid 1px var(--borderColor-neutral-muted);
    border-bottom-color: var(--borderColor-neutral-muted);
    border-radius: 6px;
    box-shadow: inset 0 -1px 0 var(--borderColor-neutral-muted);
  }

  hr {
    box-sizing: content-box;
    overflow: hidden;
    background: transparent;
    border-bottom: 1px solid var(--borderColor-muted);
    height: .25em;
    padding: 0;
    margin: var(--base-size-24) 0;
    background-color: var(--borderColor-default);
    border: 0;
  }

  hr::before {
    display: table;
    content: "";
  }

  hr::after {
    display: table;
    clear: both;
    content: "";
  }

  table {
    border-spacing: 0;
    border-collapse: collapse;
    display: block;
    width: max-content;
    max-width: 100%;
    overflow: auto;
    font-variant: tabular-nums;
  }

  table th {
    font-weight: var(--base-text-weight-semibold, 600);
  }

  table th,
  table td {
    padding: 6px 13px;
    border: 1px solid var(--borderColor-default);
  }

  table td>:last-child {
    margin-bottom: 0;
  }

  table tr {
    background-color: var(--bgColor-default);
    border-top: 1px solid var(--borderColor-muted);
  }

  table tr:nth-child(2n) {
    background-color: var(--bgColor-muted);
  }

  table img {
    background-color: transparent;
  }

  td,
  th {
    padding: 0;
  }

  input {
    font: inherit;
    margin: 0;
    overflow: visible;
    font-family: inherit;
    font-size: inherit;
    line-height: inherit;
  }

  [type=checkbox] {
    box-sizing: border-box;
    padding: 0;
  }

  ::placeholder {
    color: var(--fgColor-muted);
    opacity: 1;
  }

  input::-webkit-outer-spin-button,
  input::-webkit-inner-spin-button {
    margin: 0;
    appearance: none;
  }

  .markdown-body::before {
    display: table;
    content: "";
  }

  .markdown-body::after {
    display: table;
    clear: both;
    content: "";
  }

  .markdown-body>*:first-child {
    margin-top: 0 !important;
  }

  .markdown-body>*:last-child {
    margin-bottom: 0 !important;
  }

  p,
  blockquote,
  ul,
  ol,
  dl,
  table,
  pre {
    margin-top: 0;
    margin-bottom: var(--base-size-16);
  }

  .task-list-item {
    list-style-type: none;
  }

  .task-list-item+.task-list-item {
    margin-top: var(--base-size-4);
  }

  .task-list-item-checkbox {
    margin: 0 .2em .25em -1.4em;
    vertical-align: middle;
  }

  ul:dir(rtl) .task-list-item-checkbox {
    margin: 0 -1.6em .25em .2em;
  }

  ol:dir(rtl) .task-list-item-checkbox {
    margin: 0 -1.6em .25em .2em;
  }
`;var Fa=Object.defineProperty,Ta=Object.getOwnPropertyDescriptor,Ba=Object.getPrototypeOf,Na=Reflect.get,Lr=(t,e,r,o)=>{for(var s=o>1?void 0:o?Ta(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Fa(e,r,s),s},Ua=(t,e,r)=>Na(Ba(t),r,e);let Ie=class extends j{constructor(){super(...arguments),this.parser=new ts({pedantic:!1,gfm:!0,breaks:!0,silent:!0,renderer:{code:t=>this.renderCode(t),table:t=>this.renderTable(t)}}).use(rs({output:"mathml"})),this.placeholder=new La,this.queued=!1}disconnectedCallback(){clearTimeout(this.timer),super.disconnectedCallback()}shouldUpdate(t){return!this.queued}willUpdate(t){if(super.willUpdate(t),t.has("value")){if(this.queued)return;this.queued=!0,this.timer=window.setTimeout(()=>{this.queued=!1,this.requestUpdate()},80)}}render(){let t=this.value;if(!t)return M;this.placeholder.reset(),t=Fo(t),this.refs?.length&&(t=this.insertRefs(t,this.refs));let e=this.parser.parse(t,{async:!1});return rt(this.placeholder.restore(e))}renderCode(t){const e=t.lang??"plaintext",r=t.raw.trimEnd(),o=r.endsWith("```")||r.endsWith("~~~");if(e==="view-json")return ne.buildHTML(t.text,{loading:!o});if(e==="intent-json")return"";const s=this.removeRefs(t.text);return Bt("u-code-block",{lang:e,loading:!o},s)}renderTable(t){const e=t.header.map(o=>({text:o.tokens?zr.parseInline(o.tokens):o.text,align:o.align})),r=t.rows.map(o=>o.map(s=>({text:s.tokens?zr.parseInline(s.tokens):s.text,align:s.align})));return se.buildHTML({headers:e,rows:r})}insertRefs(t,e){const r=[...e].sort((o,s)=>s.endIndex-o.endIndex);for(const o of r){const s=o.sources??[],n=Pn(o.label??`[${e.indexOf(o)+1}]`),i=s.at(0)?.url;let a="";if(s.length>0){const u=s.map(d=>Z.buildHTML(d)).join("");a=Bt("u-ref-card-group",{slot:"tooltip"},u)}const l=Bt("u-ref-tag",{href:i},n+a),c=this.placeholder.store(l);t=t.slice(0,o.endIndex)+c+t.slice(o.endIndex)}return t}removeRefs(t){return t=t.replace(/<u-ref-tag\b[^>]*>[\s\S]*?<\/u-ref-tag>/gi,""),t=t.replace(/<!--ref:\d+-->/g,""),t}};Ie.styles=[Ua(Ie,Ie,"styles"),Da];Lr([h({type:String})],Ie.prototype,"value",2);Lr([h({type:Array})],Ie.prototype,"refs",2);Ie=Lr([E("u-marked-block")],Ie);const za=S`
  :host {
    display: block;
    font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
    font-size: 16px;
    font-weight: 400;
    line-height: 1.5;
    color: currentColor;
    background-color: transparent;
  }

  .container {
    position: relative;
    display: block;
    overflow: hidden;
  }

  textarea {
    position: absolute;
    top: 0;
    left: 0;
    bottom: 0;
    right: 0;

    display: block;
    border: none;
    outline: none;
    resize: none;
    color: inherit;
    background-color: inherit;
    
    /* 동일 스타일 크기 싱크 */
    margin: 0;
    padding: 0;
    white-space: pre;
    font-family: inherit;
    font-size: inherit;
    line-height: inherit;
    overflow: auto;
    scrollbar-width: thin;
    scrollbar-color: var(--u-scrollbar-color) transparent;
  }
  textarea[readonly] {
    cursor: default;
  }

  /* 숨김 처리 */
  pre {
    visibility: hidden;
    position: relative;
    display: block;
    color: red;

    /* 동일 스타일 크기 싱크 */
    margin: 0;
    padding: 0;
    white-space: pre;
    font-family: inherit;
    font-size: inherit;
    line-height: inherit;
    overflow: auto;
    scrollbar-width: thin;

    pointer-events: none;
    min-height: 1.5em;
  }
`;var Ia=Object.defineProperty,Ma=Object.getOwnPropertyDescriptor,Va=Object.getPrototypeOf,Ha=Reflect.get,Se=(t,e,r,o)=>{for(var s=o>1?void 0:o?Ma(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Ia(e,r,s),s},Wa=(t,e,r)=>Ha(Va(t),r,e);let J=class extends j{constructor(){super(...arguments),this.editable=!1,this.spellcheck=!1,this.minRows=1,this.updateValueFrom=t=>{t.stopPropagation(),this.value=this.textareaEl.value,this.dispatchEvent(new InputEvent("input",{bubbles:!0,composed:!0,cancelable:!0,data:this.value,inputType:t.inputType,isComposing:t.isComposing,dataTransfer:t.dataTransfer,detail:t.detail,targetRanges:t.getTargetRanges(),view:t.view}))}}updated(t){if(super.updated(t),t.has("minRows")&&this.minRows>0){const e=parseFloat(getComputedStyle(this.preEl).lineHeight);this.preEl.style.minHeight=`${this.minRows*e}px`}if(t.has("maxRows")&&this.maxRows&&this.maxRows>0&&this.maxRows>this.minRows){const e=parseFloat(getComputedStyle(this.preEl).lineHeight);this.preEl.style.maxHeight=`${this.maxRows*e}px`}t.has("editable")&&this.editable&&requestAnimationFrame(()=>{this.textareaEl.focus(),this.textareaEl.setSelectionRange(this.value?.length||0,this.value?.length||0)})}render(){return y`
      <div class="container">
        <textarea
          name="message"
          placeholder=${this.placeholder||""}
          spellcheck=${this.spellcheck}
          .readOnly=${!this.editable}
          .value=${this.value||""}
          @input=${this.updateValueFrom}
        ></textarea>
        <pre
          .textContent=${this.normalizeValue(this.value)}
        ></pre>
      </div>
    `}focus(t){this.textareaEl.focus(t)}normalizeValue(t){return t?t.endsWith(`
`)?t+"​":t:"​"}};J.styles=[Wa(J,J,"styles"),za];Se([h({type:Boolean,reflect:!0})],J.prototype,"editable",2);Se([h({type:Boolean})],J.prototype,"spellcheck",2);Se([h({type:String})],J.prototype,"placeholder",2);Se([h({type:Number})],J.prototype,"minRows",2);Se([h({type:Number})],J.prototype,"maxRows",2);Se([h({type:String})],J.prototype,"value",2);Se([it("textarea")],J.prototype,"textareaEl",2);Se([it("pre")],J.prototype,"preEl",2);J=Se([E("u-text-block")],J);const Ka=S`
  :host {
    display: flex;
    flex-direction: column;
    width: 100%;
    font-size: 14px;
    border: 1px solid var(--u-border-color);
    border-radius: 8px;
  }

  .header {
    width: 100%;
    padding: 8px;
    border-radius: inherit;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    user-select: none;
    cursor: pointer;
  }
  .header:hover {
    background: linear-gradient(var(--u-neutral-100), transparent);
  }
  .header:active {
    background: linear-gradient(var(--u-neutral-200), transparent);
  }

  .header .prefix-icon {
    color: var(--u-yellow-800);
    transition: all 0.3s ease-in-out;
  }
  .header .prefix-icon[loading] {
    animation: pulse-glow 3s ease-in-out infinite;
  }
  .header .title {
    flex: 1;
    font-size: inherit;
    font-weight: 600;
    line-height: 1;
  }

  .body {
    font-size: inherit;
    font-weight: 300;
    line-height: 1.5;
    padding: 8px;
    max-height: 210px;
    overflow: auto;
  }
  .body u-marked-block {
    font-size: inherit;
    font-family: inherit;
    line-height: inherit;
  }

  @keyframes pulse-glow {
    0%, 100% {
      opacity: 1;
      filter: brightness(1);
      transform: scale(1);
    }
    50% {
      opacity: 0.7;
      filter: brightness(1.3);
      transform: scale(1.1);
    }
  }
`;var Ga=Object.defineProperty,qa=Object.getOwnPropertyDescriptor,Ja=Object.getPrototypeOf,Xa=Reflect.get,gt=(t,e,r,o)=>{for(var s=o>1?void 0:o?qa(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Ga(e,r,s),s},Ya=(t,e,r)=>Xa(Ja(t),r,e);let ge=class extends j{constructor(){super(...arguments),this.loading=!1,this.collapsed=!0,this.autoScroll=!1,this.handleUserInterrupt=()=>{this.autoScroll&&(this.autoScroll=!1)}}updated(t){super.updated(t),t.has("value")&&this.autoScroll&&this.scrollToBottom()}render(){return y`
      <div class="header" part="header"
        @click=${()=>this.collapsed=!this.collapsed}>
        <u-icon class="prefix-icon"
          ?loading=${this.loading}
          lib="bootstrap"
          name="lightbulb-fill"
        ></u-icon>
        <span class="title">
          ${this.loading?"Thinking...":"Thought"}
        </span>
        <u-icon class="suffix-icon"
          lib="internal" 
          name=${this.collapsed?"plus-lg":"dash-lg"}
        ></u-icon>
      </div>
      
      <div class="body" part="body" scrollable 
        ?hidden=${this.collapsed}
        @wheel=${this.handleUserInterrupt}
        @touchstart=${this.handleUserInterrupt}>
        <u-marked-block
          .value=${this.value}
        ></u-marked-block>
      </div>
    `}scrollToBottom(){return this.bodyEl?(requestAnimationFrame(()=>{this.bodyEl.scrollTo({top:this.bodyEl.scrollHeight,behavior:"smooth"})}),!0):!1}};ge.styles=[Ya(ge,ge,"styles"),Ka];gt([h({type:Boolean,reflect:!0})],ge.prototype,"loading",2);gt([h({type:Boolean,reflect:!0})],ge.prototype,"collapsed",2);gt([h({type:Boolean,attribute:"auto-scroll"})],ge.prototype,"autoScroll",2);gt([h({type:String})],ge.prototype,"value",2);gt([it(".body")],ge.prototype,"bodyEl",2);ge=gt([E("u-think-block")],ge);const Qa=S`
  :host {
    display: flex;
    flex-direction: column;
    width: 100%;
    font-size: 14px;
    border: 1px solid var(--u-border-color);
    border-radius: 8px;
  }

  .header {
    width: 100%;
    padding: 8px;
    border-radius: inherit;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    cursor: pointer;
    user-select: none;
  }
  .header:hover {
    background: linear-gradient(var(--u-neutral-100), transparent);
  }
  .header:active {
    background: linear-gradient(var(--u-neutral-200), transparent);
  }

  .header u-icon[name="tools"] {
    color: var(--u-blue-800);
  }
  .header .title {
    flex: 1;
    font-size: inherit;
    font-weight: 600;
    line-height: 1;
  }

  .body {
    display: block;
    font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
    width: 100%;
    max-height: 260px;
    padding: 8px;
    overflow: auto;
  }
  .body u-json-viewer {
    font-size: inherit;
    font-family: inherit;
    line-height: 1.5;
  }
  .body u-icon[name="chevron-down"] {
    align-self: center;
    font-size: 16px;
    color: var(--u-neutral-600, #4b5563);
  }
  .body .output-view {
    display: flex;
    flex-direction: column;
  }
`;var Za=Object.defineProperty,el=Object.getOwnPropertyDescriptor,tl=Object.getPrototypeOf,rl=Reflect.get,mt=(t,e,r,o)=>{for(var s=o>1?void 0:o?el(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Za(e,r,s),s},ol=(t,e,r)=>rl(tl(t),r,e);let me=class extends j{constructor(){super(...arguments),this.loading=!1,this.collapsed=!0,this.title=""}render(){return y`
      <div class="header"  @click=${()=>this.collapsed=!this.collapsed}>
        ${this.loading?y`<u-spinner></u-spinner>`:y`<u-icon lib="bootstrap" name="tools"></u-icon>`}
        <div class="title">
          ${this.title||"Tool Usage"}
        </div>
        <u-icon
          lib="internal"
          name=${this.collapsed?"plus-lg":"dash-lg"}
        ></u-icon>
      </div>

      <div class="body" part="body" scrollable ?hidden=${this.collapsed}>
        <div class="input-view" ?hidden=${!this.input}>
          <u-json-block
            .value=${this.input||{}}
          ></u-json-block>
        </div>
        <div class="output-view" ?hidden=${!this.output}>
          <u-icon lib="internal" name="chevron-down"></u-icon>
          <u-json-block
            .value=${this.output||{}}
          ></u-json-block>
        </div>
      </div>
    `}};me.styles=[ol(me,me,"styles"),Qa];mt([h({type:Boolean,reflect:!0})],me.prototype,"loading",2);mt([h({type:Boolean,reflect:!0})],me.prototype,"collapsed",2);mt([h({type:String})],me.prototype,"title",2);mt([h({type:Object,converter:Pr()})],me.prototype,"input",2);mt([h({type:Object,converter:Pr()})],me.prototype,"output",2);me=mt([E("u-tool-block")],me);const sl=S`
  :host {
    display: flex;
    flex-direction: column;
    width: 100%;
  }

  /* 헤더 영역 */
  .header {
    all: unset;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 0.5em;
    color: var(--u-txt-color-strong);
    user-select: none;
    cursor: pointer;
  }
  .header:hover {
    color: var(--u-txt-color-hover);
  }
  .header:focus-visible {
    outline: 2px solid rgba(100, 150, 250, 0.6);
    outline-offset: 2px;
  }

  u-icon {
    color: inherit;
    font-size: 1em;
    transition: transform 0.2s ease-in-out;
  }
  u-icon[collapsed] {
    transform: rotate(-90deg);
  }

  .title {
    color: inherit;
    font-size: 1em;
    line-height: 1.5;
    font-weight: 600;
    color: var(--u-text-color-weak);
  }

  .count {
    color: inherit;
    font-size: 0.75em;
    font-weight: 400;
    line-height: 2em;
  }

  /* 바디 영역 */
  .body {
    display: flex;
    flex-direction: column;
    gap: 0.75em;
    margin-top: 0.75em;
    transition: all 0.3s ease-in-out;
  }
  .body[collapsed] {
    height: 0;
    opacity: 0;
    margin: 0;
    padding: 0;
    overflow: hidden;
  }
  
  .body u-ref-card {
    width: 100%;
  }
`;var nl=Object.defineProperty,il=Object.getOwnPropertyDescriptor,al=Object.getPrototypeOf,ll=Reflect.get,ar=(t,e,r,o)=>{for(var s=o>1?void 0:o?il(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&nl(e,r,s),s},cl=(t,e,r)=>ll(al(t),r,e);let Re=class extends j{constructor(){super(...arguments),this.collapsed=!0,this.title=""}render(){return y`
      <button class="header" @click=${()=>this.collapsed=!this.collapsed}>
        <u-icon
          ?collapsed=${this.collapsed}
          lib="internal" 
          name="chevron-down"
        ></u-icon>
        <div class="title">
          ${this.title||"References"}
        </div>
        <div style="flex: 1;"></div>
        <div class="count">
          ${this.sources?this.sources.length:0}
        </div>
      </button>

      <div class="body" ?collapsed=${this.collapsed}>
        ${Oe(this.sources||[],(t,e)=>e,t=>y`
            <u-ref-card
              .type=${t.type}
              .url=${t.url}
              .title=${t.title||""}
              .snippet=${t.snippet||""}
              .tags=${t.tags}
            ></u-ref-card>
          `)}
      </div>
    `}};Re.styles=[cl(Re,Re,"styles"),sl];ar([h({type:Boolean,reflect:!0})],Re.prototype,"collapsed",2);ar([h({type:String})],Re.prototype,"title",2);ar([h({type:Array})],Re.prototype,"sources",2);Re=ar([E("u-ref-block")],Re);const ul=S`
  :host(:empty) u-tooltip {
    display: none;
  }

  u-button {
    color: var(--u-txt-color-weak);
    font-size: 16px;
  }
`;var dl=Object.defineProperty,hl=Object.getOwnPropertyDescriptor,pl=Object.getPrototypeOf,fl=Reflect.get,lr=(t,e,r,o)=>{for(var s=o>1?void 0:o?hl(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&dl(e,r,s),s},gl=(t,e,r)=>fl(pl(t),r,e);let Ae=class extends j{constructor(){super(...arguments),this.multiple=!1,this.handleButtonClick=()=>{this.input&&this.input.click()},this.handleInputChange=t=>{t.preventDefault(),t.stopPropagation();const e=t.target,r=e.files;!r||r.length===0||(this.fire("attach",{detail:{files:Array.from(r)}}),e.value="")}}render(){return y`
      <u-button part="base"
        variant="ghost"
        @click=${this.handleButtonClick}>
        <u-icon part="icon" 
          lib="bootstrap" 
          name="paperclip"
        ></u-icon>
      </u-button>

      <u-tooltip for="u-button" placement="bottom" distance="8">
        <slot></slot>
      </u-tooltip>
      
      <input 
        hidden
        type="file"
        ?multiple=${this.multiple}
        .accept=${this.accept||"*"}
        @change=${this.handleInputChange}
      />
    `}};Ae.styles=[gl(Ae,Ae,"styles"),ul];lr([h({type:String})],Ae.prototype,"accept",2);lr([h({type:Boolean})],Ae.prototype,"multiple",2);lr([it('input[type="file"]')],Ae.prototype,"input",2);Ae=lr([E("u-attach-button")],Ae);const ml=S`
  :host {
    display: flex;
    flex-direction: row;
    align-items: center;
  }
  :host(:empty) u-tooltip {
    display: none;
  }

  u-button {
    color: var(--u-txt-color-weak);
    font-size: 16px;
  }
`;var bl=Object.defineProperty,vl=Object.getOwnPropertyDescriptor,yl=Object.getPrototypeOf,wl=Reflect.get,Jo=(t,e,r,o)=>{for(var s=o>1?void 0:o?vl(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&bl(e,r,s),s},xl=(t,e,r)=>wl(yl(t),r,e);let tt=class extends j{constructor(){super(...arguments),this.value="none",this.handleUpButtonClick=()=>{this.value=this.value==="up"?"none":"up",this.dispatchEvent(new Event("change",{bubbles:!0,composed:!0}))},this.handleDownButtonClick=()=>{this.value=this.value==="down"?"none":"down",this.dispatchEvent(new Event("change",{bubbles:!0,composed:!0}))}}render(){return y`
      <u-button class="up-btn" part="up-btn"
        variant="ghost"
        @click=${this.handleUpButtonClick}>
        <u-icon part="icon"
          lib="bootstrap"
          name=${this.value==="up"?"hand-thumbs-up-fill":"hand-thumbs-up"}
        ></u-icon>
      </u-button>
      <u-button class="down-btn" part="down-btn"
        variant="ghost"
        @click=${this.handleDownButtonClick}>
        <u-icon part="icon"
          lib="bootstrap"
          name=${this.value==="down"?"hand-thumbs-down-fill":"hand-thumbs-down"}
        ></u-icon>
      </u-button>

      <u-tooltip for=".up-btn" placement="bottom" distance="8">
        <slot name="up"></slot>
      </u-tooltip>
      <u-tooltip for=".down-btn" placement="bottom" distance="8">
        <slot name="down"></slot>
      </u-tooltip>
    `}};tt.styles=[xl(tt,tt,"styles"),ml];Jo([h({type:String})],tt.prototype,"value",2);tt=Jo([E("u-vote-button")],tt);const kl=S`
  :host {
    width: auto;
    max-width: 100%;
    display: flex;
    flex-direction: column;
  }
  :host([position="left"]) {
    align-self: flex-start;
    align-items: flex-start;
  }
  :host([position="right"]) {
    align-self: flex-end;
    align-items: flex-end;
  }

  .body {
    width: 100%;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  .body[variant="default"] {
    padding: 12px;
    background-color: transparent;
  }
  .body[variant="bubble"] {
    padding: 12px 18px;
    border-radius: 18px;
    background-color: var(--u-neutral-200, #f3f4f6);
  }
  .body[variant="bubble"][position="left"] {
    border-bottom-left-radius: 4px;
  }
  .body[variant="bubble"][position="right"] {
    border-bottom-right-radius: 4px;
  }

  .dot-loader {
    width: 24px;
    height: 24px;
    fill: var(--u-neutral-800, #6b7280);
  }
  .dot-loader circle {
    animation: bounce 1.05s infinite;
    transform-box: fill-box;
    transform-origin: center;
  }
  .dot-loader circle.d1 {
    animation-delay: 0.1s;
  }
  .dot-loader circle.d2 {
    animation-delay: 0.2s;
  }

  @media (prefers-reduced-motion: reduce) {
    .dot-loader circle {
      animation: none;
      transform: none;
    }
  }

  @keyframes bounce {
    0%,57.14% {
      animation-timing-function: cubic-bezier(0.33, 0.66, 0.66, 1);
      transform: translateY(0);
    }
    28.57% {
      animation-timing-function: cubic-bezier(0.33, 0, 0.66, 0.33);
      transform: translateY(-6px);
    }
    100% {
      transform: translateY(0);
    }
  }
`;var $l=Object.defineProperty,_l=Object.getOwnPropertyDescriptor,Cl=Object.getPrototypeOf,Sl=Reflect.get,cr=(t,e,r,o)=>{for(var s=o>1?void 0:o?_l(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&$l(e,r,s),s},El=(t,e,r)=>Sl(Cl(t),r,e);let Le=class extends j{constructor(){super(...arguments),this.loading=!1,this.variant="default",this.position="left"}render(){return y`
      <slot name="header"></slot>

      <div class="body" part="body" variant=${this.variant} position=${this.position}>
        <slot></slot>
        <svg class="dot-loader" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"
          ?hidden=${!this.loading}>
          <circle class="d0" cx="4" cy="12" r="3" />
          <circle class="d1" cx="12" cy="12" r="3" />
          <circle class="d2" cx="20" cy="12" r="3" />
        </svg>
      </div>

      <slot name="footer" ?hidden=${this.loading}></slot>
    `}};Le.styles=[El(Le,Le,"styles"),kl];cr([h({type:Boolean,reflect:!0})],Le.prototype,"loading",2);cr([h({type:String,reflect:!0})],Le.prototype,"variant",2);cr([h({type:String,reflect:!0})],Le.prototype,"position",2);Le=cr([E("u-message")],Le);const Ol=S`
  :host {
    position: relative;
    display: flex;
    flex-direction: column;
    padding: 12px;
    border: 1px solid var(--u-border-color, #e0e0e0);
    border-radius: 8px;
    background-color: var(--u-bg-color);
  }
  :host(:focus-within) {
    outline: none;
    border-color: var(--u-neutral-400, #9ca3af);
    box-shadow: 0 0 0 1px var(--u-neutral-200, #9ca3af);
  }
  :host([loading])::before {
    content: '';
    position: absolute;
    inset: -2px;
    border-radius: 10px;
    padding: 2px;
    background: linear-gradient(90deg,
      transparent 0%,
      transparent 25%,
      #f9a8d4 45%,
      #fbbf24 55%,
      transparent 75%,
      transparent 100%
    );
    background-size: 300% 100%;
    animation: border-shimmer 2s linear infinite;
    -webkit-mask:
      linear-gradient(#fff 0 0) content-box,
      linear-gradient(#fff 0 0);
    -webkit-mask-composite: xor;
    mask-composite: exclude;
  }

  .files {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 8px;
  }
  .files u-file-block {
    flex: 0 1 auto;
  }

  .input {
    flex: 1;
    padding: 8px;
  }

  .control {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
  }
  
  .send-btn {
    font-size: 16px;
    color: var(--u-neutral-0);
    background-color: var(--u-neutral-800);
  }
  .send-btn[disabled] {
    opacity: 0.5;
    cursor: not-allowed;
  }

  @keyframes border-shimmer {
    0%   { background-position: 100% 0; }
    100% { background-position: 0% 0; }
  }
`;var jl=Object.defineProperty,Pl=Object.getOwnPropertyDescriptor,Rl=Object.getPrototypeOf,Al=Reflect.get,Ge=(t,e,r,o)=>{for(var s=o>1?void 0:o?Pl(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&jl(e,r,s),s},Ll=(t,e,r)=>Al(Rl(t),r,e);let ie=class extends j{constructor(){super(...arguments),this.loading=!1,this.minRows=1,this.maxRows=10,this.handleSendButtonClick=t=>{t.preventDefault(),t.stopPropagation(),this.submit()}}connectedCallback(){super.connectedCallback(),this.setAttribute("tabindex","0")}render(){return y`
      <slot name="header"></slot>

      <div class="files" part="files"
        ?hidden=${!this.files||this.files.length===0}>
        ${Oe(this.files||[],(t,e)=>e,(t,e)=>y`
          <u-file-block
            data-index=${e}
            .removable=${!0}
            .status=${t.status}
            .name=${t.name}
            .type=${t.mimeType}
            .size=${t.size}
            @remove=${this.handleRemoveFile}
          ></u-file-block>
        `)}
      </div>

      <u-text-block class="input" part="input"
        .editable=${!0}
        .minRows=${this.minRows}
        .maxRows=${this.maxRows}
        .value=${this.value}
        .placeholder=${this.placeholder}
        @input=${this.handleTextBlockInput}
        @keydown=${this.handleTextBlockKeydown}
      ></u-text-block>
      
      <div class="control" part="control">
        <slot name="left-actions"></slot>
        <div style="flex: 1;"></div>
        <slot name="right-actions"></slot>
        
        <u-button class="send-btn" part="send-btn"
          ?disabled=${!this.loading&&!this.value}
          @click=${this.handleSendButtonClick}>
          <u-icon
            lib="bootstrap"
            name=${this.loading?"stop-circle":"arrow-up"}
          ></u-icon>
        </u-button>
      </div>
      
      <slot name="footer"></slot>
    `}submit(){if(this.loading){const r={};this.fire("stop",{detail:r}),this.dispatchLegacy("u-cancel",r);return}const t=this.value&&this.value.trim()!=="",e=this.files&&this.files.length>0;if(t||e){const r={value:this.value?.trim()??"",files:this.files?[...this.files]:void 0};this.fire("send",{detail:r}),this.dispatchLegacy("u-submit",r)}}dispatchLegacy(t,e){this.dispatchEvent(new CustomEvent(t,{bubbles:!0,composed:!0,cancelable:!0,detail:e}))}handleRemoveFile(t){t.stopPropagation();const e=t.target.dataset.index;e!==void 0&&this.files&&(this.files=this.files.filter((r,o)=>o!==Number(e)),this.relay(t))}handleTextBlockInput(t){t.preventDefault();const e=t.target;this.value=e.value}handleTextBlockKeydown(t){t.key==="Enter"&&!t.shiftKey&&(t.preventDefault(),this.submit())}};ie.styles=[Ll(ie,ie,"styles"),Ol];Ge([h({type:Boolean,reflect:!0})],ie.prototype,"loading",2);Ge([h({type:Number})],ie.prototype,"minRows",2);Ge([h({type:Number})],ie.prototype,"maxRows",2);Ge([h({type:String})],ie.prototype,"placeholder",2);Ge([h({type:String})],ie.prototype,"value",2);Ge([h({type:Array})],ie.prototype,"files",2);ie=Ge([E("u-prompt")],ie);const Dl=S`
  :host {
    display: flex;
    flex-direction: column;
    gap: 0.5em;
    margin: 0.75em 0;
  }

  .question {
    margin: 0 0 0.25em;
    font-size: 0.9em;
    color: var(--u-txt-color-weak);
    line-height: 1.5;
  }

  .choices {
    display: flex;
    flex-direction: column;
    gap: 0.4em;
  }

  u-button {
    display: block;
    width: fit-content;
    padding: 0.5em 0.75em;
    font-size: 0.875em;
    justify-content: flex-start;
    line-height: 1.4;
    border-radius: 10px;
    box-shadow: 0 1px 2px var(--u-shadow-color-weaker);
    transition: all 0.15s ease;
  }
  u-button:hover {
    transform: translateY(-1px);
  }
  u-button:active {
    transform: translateY(0);
  }
`;var Fl=Object.defineProperty,Tl=Object.getOwnPropertyDescriptor,Bl=Object.getPrototypeOf,Nl=Reflect.get,Dr=(t,e,r,o)=>{for(var s=o>1?void 0:o?Tl(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Fl(e,r,s),s},Ul=(t,e,r)=>Nl(Bl(t),r,e);let Me=class extends j{constructor(){super(...arguments),this.choices=[]}render(){return!this.choices||this.choices.length===0?M:y`
      <p class="question" ?hidden=${!this.question}>${this.question}</p>
      <div class="choices">
        ${Oe(this.choices,(t,e)=>y`
          <u-button @click=${()=>this.handleChoiceClick(e)}>
            ${t}
          </u-button>
        `)}
      </div>
    `}handleChoiceClick(t){const e=this.choices[t];e&&this.fire("choice",{detail:{value:e}})}};Me.styles=[Ul(Me,Me,"styles"),Dl];Dr([h({type:String})],Me.prototype,"question",2);Dr([h({type:Array})],Me.prototype,"choices",2);Me=Dr([E("u-question-intent")],Me);var zl=S`
  :host {
    --slide-gap: 0px;
    --slides-per-view: 1;
  }

  :host {
    position: relative;
    display: block;
    width: 100%;
    overflow: hidden;
  }
  :host([draggable]) .slides-wrapper {
    user-select: none;
    cursor: grab;
  }
  :host([draggable]) .slides-wrapper:active {
    cursor: grabbing;
  }
  :host([draggable]) ::slotted(*) {
    -webkit-user-drag: none;
    user-select: none;
  }

  /* ── Slides ── */
  .slides-wrapper {
    position: relative;
    width: 100%;
    height: 100%;
    overflow: hidden;
    touch-action: pan-y;
  }

  .slides {
    display: flex;
    flex-direction: row;
    width: 100%;
    height: 100%;
    gap: var(--slide-gap, 0px);
    transition: transform 0.3s ease-in-out;
  }

  ::slotted(*) {
    flex: 0 0 calc(
      (100% - (var(--slides-per-view) - 1) * var(--slide-gap, 0px)) / var(--slides-per-view)
    );
    min-width: 0;
    height: 100%;
  }

  /* ── Navigation ── */
  .nav-button {
    position: absolute;
    z-index: 10;
    top: 50%;
    transform: translateY(-50%);
    padding: 8px;
    font-size: 20px;
    color: var(--u-neutral-900);
    background-color: var(--u-neutral-100);
    box-shadow: 0 2px 8px rgba(0, 0, 0, 0.15);
  }
  .nav-button:hover {
    background-color: var(--u-neutral-200);
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.2);
    transform: translateY(-50%) scale(1.1);
  }
  .nav-button:active {
    background-color: var(--u-neutral-300);
    transform: translateY(-50%) scale(0.95);
  }

  .nav-button.prev { left: 12px; }
  .nav-button.next { right: 12px; }

  /* ── Pagination ── */
  .indicator {
    position: absolute;
    z-index: 10;
    bottom: 16px;
    left: 50%;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: center;
    gap: 8px;
    transform: translateX(-50%);
  }

  .dot {
    width: 10px;
    height: 10px;
    padding: 0;
    border: none;
    border-radius: 50%;
    background-color: var(--u-neutral-400);
    cursor: pointer;
    transition: all 0.2s ease;
  }
  .dot:hover {
    background-color: var(--u-neutral-200);
  }
  .dot[active] {
    width: 24px;
    border-radius: 5px;
    background-color: var(--u-neutral-100);
  }
`,V=class extends j{constructor(...e){super(...e),this.autoplay=!1,this.autoplayInterval=3e3,this.loop=!1,this.navigation=!1,this.pagination=!1,this.draggable=!1,this.slidesPerView=1,this.slidesPerMove=1,this.gap=0,this.index=0,this.slideCount=0,this.isDragging=!1,this.dragOffset=0,this.dragStartX=0,this.dragStartTime=0,this.pointerDown=!1,this.prev=()=>{const r=Math.max(0,this.index-this.perMove);r!==this.index?this.goTo(r):this.loop&&this.goTo(Math.min((this.pageCount-1)*this.perMove,this.maxIndex))},this.next=()=>{const r=Math.min(this.maxIndex,this.index+this.perMove);r!==this.index?this.goTo(r):this.loop&&this.goTo(0)},this.goTo=r=>{r<0||r>this.maxIndex||r===this.index||(this.index=r,this.autoplay&&this.startAutoplay())},this.handleDragStart=r=>{r.preventDefault()},this.handlePointerDown=r=>{r.button===0&&(this.pointerDown=!0,this.dragStartX=r.clientX,this.dragOffset=0,this.dragStartTime=Date.now(),this.autoplay&&this.stopAutoplay())},this.handlePointerMove=r=>{if(!this.pointerDown)return;const o=r.currentTarget,s=(r.clientX-this.dragStartX)/o.offsetWidth*100;!this.isDragging&&Math.abs(r.clientX-this.dragStartX)>5&&(this.isDragging=!0,o.setPointerCapture(r.pointerId)),this.isDragging&&(this.dragOffset=s)},this.handlePointerUp=r=>{if(!this.pointerDown)return;const o=Date.now()-this.dragStartTime,s=Math.abs(this.dragOffset);this.isDragging&&(s>20||s>5&&o<300)&&(this.dragOffset<0?this.next():this.prev(),this.addEventListener("click",this.handleClickCancel,{capture:!0,once:!0})),this.isDragging&&r.currentTarget.releasePointerCapture(r.pointerId),this.pointerDown=!1,this.isDragging=!1,this.dragOffset=0,this.autoplay&&this.startAutoplay()},this.handleClickCancel=r=>{r.preventDefault(),r.stopPropagation()}}static{this.styles=[super.styles,zl]}get perView(){return Math.max(1,this.slidesPerView)}get perMove(){return Math.max(1,this.slidesPerMove)}get maxIndex(){return Math.max(0,this.slideCount-this.perView)}get pageCount(){return this.maxIndex<=0?1:Math.ceil(this.maxIndex/this.perMove)+1}get currentPage(){return this.index>=this.maxIndex?this.pageCount-1:Math.floor(this.index/this.perMove)}connectedCallback(){super.connectedCallback(),this.autoplay&&this.startAutoplay()}disconnectedCallback(){this.stopAutoplay(),super.disconnectedCallback()}willUpdate(e){super.willUpdate(e),e.has("slidesPerView")&&this.style.setProperty("--slides-per-view",String(this.perView)),e.has("gap")&&this.style.setProperty("--slide-gap",`${this.gap}px`)}updated(e){super.updated(e),(e.has("autoplay")||e.has("autoplayInterval"))&&(this.autoplay?this.startAutoplay():this.stopAutoplay())}render(){const e=-(this.index*(100/this.perView))+this.dragOffset,r=this.gap>0?-(this.index*this.gap/this.perView):0,o=`transform: ${r?`translateX(calc(${e}% + ${r}px))`:`translateX(${e}%)`}${this.isDragging?"; transition: none":""}`;return y`
      <div class="slides-wrapper"
        @dragstart=${this.handleDragStart}
        @pointerdown=${this.draggable?this.handlePointerDown:null}
        @pointermove=${this.draggable?this.handlePointerMove:null}
        @pointerup=${this.draggable?this.handlePointerUp:null}
        @pointerleave=${this.draggable?this.handlePointerUp:null}>
        <div part="slides" class="slides" style="${o}">
          <slot @slotchange=${this.handleSlotChange}></slot>
        </div>
      </div>

      <u-button part="prev-button" class="nav-button prev"
        ?hidden=${!this.navigation||!this.loop&&this.index<=0}
        variant="ghost"
        rounded
        @click=${this.prev}>
        <u-icon lib="internal" name="chevron-left"></u-icon>
      </u-button>
      <u-button part="next-button" class="nav-button next"
        ?hidden=${!this.navigation||!this.loop&&this.index>=this.maxIndex}
        variant="ghost"
        rounded
        @click=${this.next}>
        <u-icon lib="internal" name="chevron-right"></u-icon>
      </u-button>

      <div part="indicator" class="indicator"
        ?hidden=${!this.pagination||this.pageCount<=1}>
        ${Array.from({length:this.pageCount},(s,n)=>y`
          <button part="dot" class="dot"
            ?active=${n===this.currentPage}
            @click=${()=>this.goTo(Math.min(n*this.perMove,this.maxIndex))}>
          </button>
        `)}
      </div>
    `}startAutoplay(){this.stopAutoplay(),this.autoplayTimer=window.setInterval(()=>{this.next()},this.autoplayInterval)}stopAutoplay(){this.autoplayTimer&&(clearInterval(this.autoplayTimer),this.autoplayTimer=void 0)}handleSlotChange(e){const r=e.target;this.slideCount=r.assignedElements().length,this.index>=this.slideCount&&(this.index=Math.max(0,this.slideCount-1))}};b([h({type:Boolean,reflect:!0}),v("design:type",Object)],V.prototype,"autoplay",void 0);b([h({type:Number,attribute:"autoplay-interval"}),v("design:type",Object)],V.prototype,"autoplayInterval",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Object)],V.prototype,"loop",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Object)],V.prototype,"navigation",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Object)],V.prototype,"pagination",void 0);b([h({type:Boolean,reflect:!0}),v("design:type",Object)],V.prototype,"draggable",void 0);b([h({type:Number,attribute:"slides-per-view"}),v("design:type",Object)],V.prototype,"slidesPerView",void 0);b([h({type:Number,attribute:"slides-per-move"}),v("design:type",Object)],V.prototype,"slidesPerMove",void 0);b([h({type:Number}),v("design:type",Object)],V.prototype,"gap",void 0);b([h({type:Number}),v("design:type",Object)],V.prototype,"index",void 0);b([z(),v("design:type",Object)],V.prototype,"slideCount",void 0);b([z(),v("design:type",Object)],V.prototype,"isDragging",void 0);b([z(),v("design:type",Object)],V.prototype,"dragOffset",void 0);V=b([E("u-carousel")],V);const Il=S`
  :host {
    display: block;
    width: 100%;
    margin: 0.75em 0;
  }

  .slide {
    position: relative;
    border-radius: 10px;
    overflow: hidden;
    cursor: pointer;
    aspect-ratio: 4 / 3;
  }
  .slide img {
    display: block;
    width: 100%;
    height: 100%;
    object-fit: cover;
    pointer-events: none;
    transition: transform 0.3s ease, filter 0.3s ease;
  }
  .slide:hover img {
    transform: scale(1.05);
    filter: brightness(0.92);
  }

  .caption {
    position: absolute;
    bottom: 0;
    left: 0;
    right: 0;
    padding: 20px 10px 8px;
    color: white;
    font-size: 0.8em;
    line-height: 1.3;
    background: linear-gradient(transparent, rgba(0, 0, 0, 0.55));
  }

  /* ─── 라이트박스 UI ─── */

  /* 루트 오버레이: flex column 전체 화면 */
  .lb-overlay {
    position: fixed;
    inset: 0;
    z-index: 9999;
    display: flex;
    flex-direction: column;
    background: rgba(0, 0, 0, 0.92);
    animation: overlay-fadeIn 0.2s ease;
  }

  /* 상단 헤더: 카운터(중앙) + 닫기(우측) */
  .lb-header {
    height: 56px;
    position: relative;
    flex-shrink: 0;
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .lb-counter {
    color: rgba(255, 255, 255, 0.75);
    font-size: 0.875em;
    font-variant-numeric: tabular-nums;
    background: rgba(0, 0, 0, 0.35);
    padding: 3px 12px;
    border-radius: 20px;
    backdrop-filter: blur(4px);
  }

  .lb-close {
    position: absolute;
    right: 16px;
    top: 50%;
    transform: translateY(-50%);
    width: 36px;
    height: 36px;
    border: none;
    border-radius: 12px;
    background: transparent;
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    transition: background 0.2s;
  }
  .lb-close:hover  { 
    background: rgba(255, 255, 255, 0.18); 
  }
  .lb-close:active { 
    background: rgba(255, 255, 255, 0.28); 
  }

  .lb-close u-icon {
    color: white;
    font-size: 1.25em;
  }

  /* 중앙 본문: 뷰포트 + 이전/다음 버튼(absolute 오버레이) */
  .lb-body {
    position: relative;
    flex: 1;
    min-height: 0;
  }

  .lb-viewport {
    position: relative;
    width: 100%;
    height: 100%;
    overflow: hidden;
  }

  .lb-track {
    position: absolute;
    inset: 0;
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 16px;
    transition: transform 0.45s cubic-bezier(0.25, 0.1, 0.25, 1);
    will-change: transform;
  }

  .lb-slide {
    flex: 0 0 70vw;
    height: 100%;
    display: flex;
    align-items: center;
    justify-content: center;
    opacity: 0.5;
    transform: scale(0.9);
    transition: opacity 0.4s ease, transform 0.4s ease;
  }
  .lb-slide[active] {
    opacity: 1;
    transform: scale(1);
  }
  .lb-slide img {
    max-width: 100%;
    max-height: 100%;
    object-fit: contain;
    border-radius: 6px;
    user-select: none;
    pointer-events: none;
  }

  .lb-nav {
    position: absolute;
    top: 0;
    bottom: 0;
    z-index: 100;
    width: 80px;
    border: none;
    background: transparent;
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    transition: background 0.25s;
  }
  .lb-nav.prev {
    left: 0;
    background: linear-gradient(to right, rgba(0, 0, 0, 0.28), transparent);
  }
  .lb-nav.next {
    right: 0;
    background: linear-gradient(to left, rgba(0, 0, 0, 0.28), transparent);
  }
  .lb-nav.prev:hover {
    background: linear-gradient(to right, rgba(0, 0, 0, 0.5), transparent);
  }
  .lb-nav.next:hover {
    background: linear-gradient(to left, rgba(0, 0, 0, 0.5), transparent);
  }

  .lb-nav u-icon {
    display: flex;
    align-items: center;
    justify-content: center;
    color: white;
    font-size: 1.5em;
    padding: 12px;
    border-radius: 50%;
    background: rgba(255, 255, 255, 0.15);
    transition: background 0.2s, transform 0.15s;
  }
  .lb-nav:hover u-icon {
    background: rgba(255, 255, 255, 0.3);
    transform: scale(1.1);
  }

  /* 하단 푸터: 캡션 */
  .lb-footer {
    position: relative;
    flex-shrink: 0;
    padding: 24px;
    background: linear-gradient(transparent, rgba(0, 0, 0, 0.72));
  }

  .lb-caption {
    margin: 0;
    color: white;
    font-size: 1.05em;
    line-height: 1.5;
    text-align: center;
    text-shadow: 0 1px 4px rgba(0, 0, 0, 0.6);
  }

  @keyframes overlay-fadeIn {
    from { opacity: 0; }
    to   { opacity: 1; }
  }
`;var Ml=Object.defineProperty,Vl=Object.getOwnPropertyDescriptor,Hl=Object.getPrototypeOf,Wl=Reflect.get,Fr=(t,e,r,o)=>{for(var s=o>1?void 0:o?Vl(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&Ml(e,r,s),s},Kl=(t,e,r)=>Wl(Hl(t),r,e);let Ve=class extends j{constructor(){super(...arguments),this.items=[],this.index=null,this.open=t=>{this.index=t},this.close=()=>{this.index=null},this.prev=()=>{this.index!==null&&this.index>0&&this.index--},this.next=()=>{this.index!==null&&this.index<this.items.length-1&&this.index++},this.handlePrevClick=t=>{t.stopPropagation(),this.prev()},this.handleNextClick=t=>{t.stopPropagation(),this.next()},this.handleKeyDown=t=>{this.index!==null&&(t.key==="Escape"?this.close():t.key==="ArrowLeft"?this.prev():t.key==="ArrowRight"&&this.next())}}connectedCallback(){super.connectedCallback(),document.addEventListener("keydown",this.handleKeyDown)}disconnectedCallback(){document.removeEventListener("keydown",this.handleKeyDown),super.disconnectedCallback()}render(){if(!this.items?.length)return M;const t=this.items.length;return y`
      <u-carousel
        .loop=${!1}
        .navigation=${!1}
        .pagination=${!0}
        .draggable=${!0}
        .slidesPerView=${Math.min(t,3)}
        .gap=${8}
      >
        ${Oe(this.items,(e,r)=>y`
          <div class="slide" 
            @click=${()=>this.open(r)}>
            <img 
              src=${e.src} 
              alt=${e.alt||""} 
              loading="lazy" 
            />
            <div class="caption"
              ?hidden=${!e.caption}>
              ${e.caption}
            </div>
          </div>
        `)}
      </u-carousel>

      ${this.renderLightbox()}
    `}renderLightbox(){const t=this.index;if(t===null)return M;if(t<0||t>=this.items.length)return M;const e=this.items[t];if(!e)return M;const r=this.items.length,o=`calc(15vw - ${t} * (70vw + 16px))`;return y`
      <div class="lb-overlay">

        <!-- 상단: 카운터(중앙) + 닫기 버튼(우측) -->
        <header class="lb-header">
          <div class="lb-counter" ?hidden=${r<=1}>
            ${t+1} / ${r}
          </div>
          <button class="lb-close" @click=${this.close}>
            <u-icon lib="internal" name="x-lg"></u-icon>
          </button>
        </header>

        <!-- 중앙: 뷰포트 + 이전/다음 버튼(absolute 오버레이) -->
        <div class="lb-body">
          <div class="lb-viewport">
            <div class="lb-track" style="transform:translateX(${o})">
              ${Oe(this.items,(s,n)=>y`
                <div class="lb-slide" ?active=${n===t}>
                  <img src=${s.src} alt=${s.alt||""} />
                </div>
              `)}
            </div>
          </div>

          <button class="lb-nav prev"
            ?hidden=${t<=0}
            @click=${this.handlePrevClick}>
            <u-icon lib="internal" name="chevron-left"></u-icon>
          </button>

          <button class="lb-nav next"
            ?hidden=${t>=r-1}
            @click=${this.handleNextClick}>
            <u-icon lib="internal" name="chevron-right"></u-icon>
          </button>
        </div>

        <!-- 하단: 캡션 -->
        <footer class="lb-footer" ?hidden=${!e.caption}>
          <p class="lb-caption">${e.caption}</p>
        </footer>

      </div>
    `}};Ve.styles=[Kl(Ve,Ve,"styles"),Il];Fr([h({type:Array})],Ve.prototype,"items",2);Fr([z()],Ve.prototype,"index",2);Ve=Fr([E("u-images-view")],Ve);const Gl=S`
  :host {
    --video-ratio: 16 / 9;
  }

  :host {
    display: block;
    position: relative;
    width: 100%;
    max-width: 800px;
    margin: 0.75em auto;
  }

  video {
    width: 100%;
    aspect-ratio: var(--video-ratio);
    border-radius: 8px;
    background: #000;
  }

  .video-wrapper {
    width: 100%;
    aspect-ratio: var(--video-ratio);
  }

  .video-wrapper iframe {
    width: 100%;
    height: 100%;
    border: none;
    border-radius: 8px;
  }
`;var ql=Object.defineProperty,Jl=Object.getOwnPropertyDescriptor,Xl=Object.getPrototypeOf,Yl=Reflect.get,ur=(t,e,r,o)=>{for(var s=o>1?void 0:o?Jl(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&ql(e,r,s),s},Ql=(t,e,r)=>Yl(Xl(t),r,e);let De=class extends j{constructor(){super(...arguments),this.ratio="16:9"}updated(t){super.updated(t),t.has("ratio")&&this.style.setProperty("--video-ratio",this.ratio.replace(":"," / "))}render(){if(!this.src)return M;if(this.isYouTube(this.src)){const t=this.extractYouTubeId(this.src);if(t)return y`
          <div class="video-wrapper">
            <iframe
              src="https://www.youtube.com/embed/${t}"
              loading="lazy"
              allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture"
              allowfullscreen
            ></iframe>
          </div>
        `}if(this.isVimeo(this.src)){const t=this.extractVimeoId(this.src);if(t)return y`
          <div class="video-wrapper">
            <iframe
              src="https://player.vimeo.com/video/${t}"
              loading="lazy"
              allow="autoplay; fullscreen; picture-in-picture"
              allowfullscreen
            ></iframe>
          </div>
        `}return y`
      <video
        src=${this.src}
        poster=${xe(this.poster)}
        controls
        playsinline
      ></video>
    `}isYouTube(t){return/youtube\.com|youtu\.be/i.test(t)}isVimeo(t){return/vimeo\.com/i.test(t)}extractYouTubeId(t){const e=[/(?:youtube\.com\/watch\?v=|youtu\.be\/)([^&\s]+)/,/youtube\.com\/embed\/([^&\s]+)/];for(const r of e){const o=t.match(r);if(o&&o[1])return o[1]}return null}extractVimeoId(t){const e=t.match(/vimeo\.com\/(\d+)/);return e?e[1]:null}};De.styles=[Ql(De,De,"styles"),Gl];ur([h({type:String})],De.prototype,"src",2);ur([h({type:String})],De.prototype,"poster",2);ur([h({type:String})],De.prototype,"ratio",2);De=ur([E("u-video-view")],De);const Zl=S`
  :host {
    position: relative;
    display: block;
    width: 100%;
    border-radius: 8px;
    overflow: hidden;
    margin: 0.75em 0;
  }

  iframe {
    width: 100%;
    height: 300px;
    display: block;
    border: none;
  }

  .caption {
    text-decoration: none;
    max-width: 60%;
    position: absolute;
    top: 8px;
    right: 8px;
    display: flex;
    flex-direction: column;
    gap: 0.25em;
    padding: 0.25em 0.5em;
    color: #fff;
    background: rgba(0, 0, 0, 0.6);
    border-radius: 6px;
    backdrop-filter: blur(4px);
  }
  .caption:hover {
    background: rgba(0, 0, 0, 0.8);
  }
  .caption strong {
    font-size: 0.8em;
  }
  .caption span {
    font-size: 0.7em;
    opacity: 0.8;
  }
`;var ec=Object.defineProperty,tc=Object.getOwnPropertyDescriptor,rc=Object.getPrototypeOf,oc=Reflect.get,bt=(t,e,r,o)=>{for(var s=o>1?void 0:o?tc(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&ec(e,r,s),s},sc=(t,e,r)=>oc(rc(t),r,e);let be=class extends j{constructor(){super(...arguments),this.zoom=15}render(){if(this.lat==null||this.lng==null)return M;const t=360/Math.pow(2,this.zoom),r=`https://www.openstreetmap.org/export/embed.html?bbox=${`${this.lng-t},${this.lat-t},${this.lng+t},${this.lat+t}`}&layer=mapnik&marker=${this.lat},${this.lng}`,o=`https://www.openstreetmap.org/?mlat=${this.lat}&mlon=${this.lng}#map=${this.zoom}/${this.lat}/${this.lng}`;return y`
      <iframe 
        src="${r}" 
        loading="lazy"
      ></iframe>
      <a class="caption" 
        ?hidden=${!this.label&&!this.description}  
        href="${o}" 
        target="_blank" 
        rel="noopener"
      >
        <strong>${this.label}</strong>
        <span>${this.description}</span>
      </a>
    `}};be.styles=[sc(be,be,"styles"),Zl];bt([h({type:Number})],be.prototype,"lat",2);bt([h({type:Number})],be.prototype,"lng",2);bt([h({type:Number})],be.prototype,"zoom",2);bt([h({type:String})],be.prototype,"label",2);bt([h({type:String})],be.prototype,"description",2);be=bt([E("u-map-view")],be);const nc=S`
  :host {
    margin: 0.75em auto;
    display: block;
    width: 100%;
    background: var(--u-bg-color);
    border: 1px solid var(--u-border-color);
    border-radius: 8px;
    overflow: hidden;
  }

  .toolbar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 6px 12px;
    background-color: var(--u-neutral-100);
    border-bottom: 1px solid var(--u-border-color);
    gap: 8px;
  }

  .toolbar-left {
    flex: 1;
  }

  .toolbar-right {
    display: flex;
    align-items: center;
    gap: 6px;
    flex-shrink: 0;
  }

  .toolbar-right u-button {
    font-size: 12px;
  }
  .toolbar-right u-button[title="Full Screen"] {
    font-size: 15px;
  }

  .viewport {
    position: relative;
    padding: 1em;
  }

  .viewport:fullscreen {
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--u-bg-color);
    padding: 2em;
  }
  .viewport:fullscreen canvas {
    max-width: 90vw;
    max-height: 90vh;
  }

  canvas {
    width: 100% !important;
    min-height: 60px;
  }

  .error-overlay {
    position: absolute;
    bottom: 10px;
    right: 10px;
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 10px;
    background: var(--u-red-0);
    border: 1px solid var(--u-red-200);
    border-radius: 6px;
    color: var(--u-red-800);
    font-size: 12px;
    pointer-events: none;
  }

  .error-overlay u-icon {
    color: var(--u-red-600);
    flex-shrink: 0;
  }
`;var ic=Object.defineProperty,ac=Object.getOwnPropertyDescriptor,lc=Object.getPrototypeOf,cc=Reflect.get,qe=(t,e,r,o)=>{for(var s=o>1?void 0:o?ac(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&ic(e,r,s),s},uc=(t,e,r)=>cc(lc(t),r,e);let br;async function dc(){return br||(br=(await _r(()=>import("./chart.js"),[])).Chart),br}let ae=class extends j{constructor(){super(...arguments),this.error=null,this.chartjs=null}connectedCallback(){super.connectedCallback(),this.observer=new MutationObserver(()=>{this.createChart()}),this.observer.observe(document.documentElement,{attributes:!0,attributeFilter:["theme"]})}disconnectedCallback(){super.disconnectedCallback(),this.observer?.disconnect(),this.destroyChart()}updated(t){super.updated(t),["data","type","options"].some(e=>t.has(e))&&this.createChart()}render(){return this.data?y`
      <div class="toolbar">
        <div class="toolbar-left"></div>
        <div class="toolbar-right">
          <u-button title="PNG Download" @click=${this.handleDownloadPNG}>
            PNG
            <u-icon slot="suffix" lib="bootstrap" name="download"></u-icon>
          </u-button>
          <u-button title="JSON Download" @click=${this.handleDownloadJSON}>
            JSON
            <u-icon slot="suffix" lib="bootstrap" name="download"></u-icon>
          </u-button>
          <u-button title="Full Screen" @click=${this.handleFullscreen}>
            <u-icon lib="bootstrap" name="box-arrow-up-right"></u-icon>
          </u-button>
        </div>
      </div>
      <div class="viewport">
        <canvas></canvas>
        <div class="error-overlay" ?hidden=${!this.error}>
          <u-icon lib="bootstrap" name="exclamation-triangle-fill"></u-icon>
          <span>${this.error}</span>
        </div>
      </div>
    `:M}async createChart(){if(!this.canvas||!this.type||!this.data)return;this.destroyChart(),this.error=null;let t;try{t=await dc()}catch{this.error="chart.js를 로드할 수 없습니다. chart.js 패키지가 설치되어 있는지 확인하세요.";return}const e=this.canvas.getContext("2d");if(!e){this.error="Canvas 2D context를 가져올 수 없습니다.";return}try{this.applyTheme(t),this.chartjs=new t(e,{type:this.type,data:this.data,options:{responsive:!0,maintainAspectRatio:!0,...this.options}})}catch(r){this.error=r instanceof Error?r.message:String(r)}}destroyChart(){this.chartjs&&(this.chartjs.destroy(),this.chartjs=null)}applyTheme(t){const e=i=>getComputedStyle(this).getPropertyValue(i).trim(),r=e("--u-txt-color"),o=e("--u-txt-color-weak"),s=e("--u-border-color"),n=e("--u-bg-color");t.defaults.color=r,t.defaults.borderColor=s,t.defaults.plugins.tooltip.backgroundColor=n,t.defaults.plugins.tooltip.titleColor=r,t.defaults.plugins.tooltip.bodyColor=o,t.defaults.plugins.tooltip.borderColor=s,t.defaults.plugins.tooltip.borderWidth=1,t.defaults.scale.ticks.color=o,t.defaults.scale.grid.color=s}handleDownloadPNG(){if(!this.chartjs)return;const t=this.chartjs.toBase64Image("image/png",1),e=document.createElement("a");e.href=t,e.download=`chart-image-${Date.now()}.png`,e.click()}handleDownloadJSON(){if(!this.data)return;const t=JSON.stringify(this.data,null,2),e=new Blob([t],{type:"application/json;charset=utf-8;"}),r=URL.createObjectURL(e),o=document.createElement("a");o.href=r,o.download=`chart-data-${Date.now()}.json`,o.click(),URL.revokeObjectURL(r)}handleFullscreen(){const t=this.viewport??this;document.fullscreenElement?document.exitFullscreen():t.requestFullscreen?.()}};ae.styles=[uc(ae,ae,"styles"),nc];qe([h({type:String})],ae.prototype,"type",2);qe([h({type:Object})],ae.prototype,"data",2);qe([h({type:Object})],ae.prototype,"options",2);qe([it("canvas")],ae.prototype,"canvas",2);qe([it(".viewport")],ae.prototype,"viewport",2);qe([z()],ae.prototype,"error",2);ae=qe([E("u-chart-view")],ae);function hc(t,e,r){let o,s;try{const a=yo(t,I.__wbindgen_malloc,I.__wbindgen_realloc),l=Xt,c=yo(e,I.__wbindgen_malloc,I.__wbindgen_realloc),u=Xt,d=I.render_json(a,l,c,u,fc(r)?Number.MAX_SAFE_INTEGER:r>>>0);var n=d[0],i=d[1];if(d[3])throw n=0,i=0,gc(d[2]);return o=n,s=i,Xo(n,i)}finally{I.__wbindgen_free(o,s,1)}}function pc(){return{__proto__:null,"./declart_wasm_bg.js":{__proto__:null,__wbg_Error_bce6d499ff0a4aff:function(e,r){return Error(Xo(e,r))},__wbindgen_init_externref_table:function(){const e=I.__wbindgen_externrefs,r=e.grow(4);e.set(0,void 0),e.set(r+0,void 0),e.set(r+1,null),e.set(r+2,!0),e.set(r+3,!1)}}}}function Xo(t,e){return bc(t>>>0,e)}let kt=null;function Nt(){return(kt===null||kt.byteLength===0)&&(kt=new Uint8Array(I.memory.buffer)),kt}function fc(t){return t==null}function yo(t,e,r){if(r===void 0){const a=Ct.encode(t),l=e(a.length,1)>>>0;return Nt().subarray(l,l+a.length).set(a),Xt=a.length,l}let o=t.length,s=e(o,1)>>>0;const n=Nt();let i=0;for(;i<o;i++){const a=t.charCodeAt(i);if(a>127)break;n[s+i]=a}if(i!==o){i!==0&&(t=t.slice(i)),s=r(s,o,o=i+t.length*3,1)>>>0;const a=Nt().subarray(s+i,s+o),l=Ct.encodeInto(t,a);i+=l.written,s=r(s,o,i,1)>>>0}return Xt=i,s}function gc(t){const e=I.__wbindgen_externrefs.get(t);return I.__externref_table_dealloc(t),e}let Ut=new TextDecoder("utf-8",{ignoreBOM:!0,fatal:!0});Ut.decode();const mc=2146435072;let vr=0;function bc(t,e){return vr+=e,vr>=mc&&(Ut=new TextDecoder("utf-8",{ignoreBOM:!0,fatal:!0}),Ut.decode(),vr=e),Ut.decode(Nt().subarray(t,t+e))}const Ct=new TextEncoder;"encodeInto"in Ct||(Ct.encodeInto=function(t,e){const r=Ct.encode(t);return e.set(r),{read:t.length,written:r.length}});let Xt=0,I;function vc(t,e){return I=t.exports,kt=null,I.__wbindgen_start(),I}async function yc(t,e){if(typeof Response=="function"&&t instanceof Response){if(typeof WebAssembly.instantiateStreaming=="function")try{return await WebAssembly.instantiateStreaming(t,e)}catch(s){if(t.ok&&r(t.type)&&t.headers.get("Content-Type")!=="application/wasm")console.warn("`WebAssembly.instantiateStreaming` failed because your server does not serve Wasm with `application/wasm` MIME type. Falling back to `WebAssembly.instantiate` which is slower. Original error:\n",s);else throw s}const o=await t.arrayBuffer();return await WebAssembly.instantiate(o,e)}else{const o=await WebAssembly.instantiate(t,e);return o instanceof WebAssembly.Instance?{instance:o,module:t}:o}function r(o){switch(o){case"basic":case"cors":case"default":return!0}return!1}}async function wc(t){if(I!==void 0)return I;t!==void 0&&(Object.getPrototypeOf(t)===Object.prototype?{module_or_path:t}=t:console.warn("using deprecated parameters for the initialization function; pass a single object instead")),t===void 0&&(t=new URL("/vault-ai-reports/assets/declart_wasm_bg.wasm",import.meta.url));const e=pc();(typeof t=="string"||typeof Request=="function"&&t instanceof Request||typeof URL=="function"&&t instanceof URL)&&(t=fetch(t));const{instance:r,module:o}=await yc(await t,e);return vc(r)}const xc="/vault-ai-reports/assets/declart_wasm_bg.wasm";var kc=Object.defineProperty,$c=Object.getOwnPropertyDescriptor,Je=(t,e,r,o)=>{for(var s=o>1?void 0:o?$c(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&kc(e,r,s),s};function _c(t){const e=globalThis.__VAULT_AI_CONFIG__?.basePath,r=t.indexOf("/assets/");return!e||r<0?t:e.replace(/\/+$/,"")+t.slice(r)}let Cc=null;function Sc(){return Cc??=wc(_c(xc))}const Ec=new Set(["tier","matrix","comparison","timeline"]);function Oc(t){if(!t||typeof t!="object")return t;const e=t;if(typeof e.kind=="string"&&Ec.has(e.kind)&&"view"in e){const{view:r,...o}=e;return o}return t}let _e=class extends $o{constructor(){super(...arguments),this.theme="default",this._svg="",this._error="",this._busy=!1}willUpdate(t){(t.has("declaration")||t.has("theme")||t.has("width"))&&this._render()}async _render(){if(!this.declaration){this._svg="",this._error="";return}this._busy=!0;try{await Sc();const t=JSON.stringify(Oc(this.declaration));this._svg=hc(t,this.theme||"default",this.width??null),this._error=""}catch(t){this._error=t?.message??String(t),this._svg=""}finally{this._busy=!1}}render(){return this._error?Y`<figure class="declart-error">⚠ 다이어그램 렌더 오류: ${this._error}</figure>`:this._svg?Y`<figure class="declart">${es(this._svg)}</figure>`:this._busy?Y`<figure class="declart-loading">다이어그램 준비 중…</figure>`:Zo}};_e.styles=ko`
    :host { display: block; margin: 1rem 0; }
    figure { margin: 0; text-align: center; }
    figure.declart :is(svg) { max-width: 100%; height: auto; }
    .declart-error {
      color: #b91c1c; background: #fef2f2; border: 1px solid #fecaca;
      border-radius: 6px; padding: .5rem .75rem; font-size: .85rem; text-align: left;
    }
    .declart-loading { color: #6b7280; font-size: .85rem; }
  `;Je([Yt({type:Object})],_e.prototype,"declaration",2);Je([Yt({type:String})],_e.prototype,"theme",2);Je([Yt({type:Number})],_e.prototype,"width",2);Je([We()],_e.prototype,"_svg",2);Je([We()],_e.prototype,"_error",2);Je([We()],_e.prototype,"_busy",2);_e=Je([_o("u-declart-view")],_e);var jc=Object.defineProperty,Pc=Object.getOwnPropertyDescriptor,vt=(t,e,r,o)=>{for(var s=o>1?void 0:o?Pc(e,r):e,n=t.length-1,i;n>=0;n--)(i=t[n])&&(s=(o?i(e,r,s):i(s))||s);return o&&s&&jc(e,r,s),s};let Te=class extends $o{constructor(){super(...arguments),this.selectedFile="",this.content="",this.loading=!1,this.errorMsg=""}updated(t){t.has("report")&&this.report?.outputs.length>0&&(this.selectedFile=this.report.outputs[0],this._loadContent())}async _loadContent(){if(!(!this.selectedFile||!this.report)){this.loading=!0,this.errorMsg="";try{this.content=await On(this.report.folder,this.selectedFile)}catch(t){this.errorMsg=`리포트를 불러오지 못했습니다: ${t}`,this.content=""}finally{this.loading=!1}}}_onSelect(t){this.selectedFile=t.target.value,this._loadContent()}render(){return(this.report?.outputs.length??0)>0?Y`
      <div class="article">
        <div class="header">
          <h2>${this.report?.name??""}</h2>
          <select @change=${this._onSelect}>
            ${this.report.outputs.map(e=>Y`
              <option value=${e} ?selected=${e===this.selectedFile}>
                ${e.replace(".md","")}
              </option>
            `)}
          </select>
        </div>
        ${this.loading?Y`<div class="empty">불러오는 중...</div>`:this.errorMsg?Y`<div class="empty error">${this.errorMsg}</div>`:Y`<u-marked-block .value=${this.content}></u-marked-block>`}
      </div>`:Y`
        <div class="article">
          <div class="header"><h2>${this.report?.name??""}</h2></div>
          <div class="empty">아직 생성된 리포트가 없습니다. 다음 스케줄 실행 후 확인하세요.</div>
        </div>
      `}};Te.styles=ko`
    :host {
      display: flex;
      flex-direction: column;
      overflow-y: auto;
      height: 100%;
      box-sizing: border-box;
      /* 스크롤바가 article 바깥에 위치하도록 padding 제거 */
    }
    /* 표준 아티클 너비(800px) — 중앙 정렬, 좌우 padding */
    .article {
      width: 100%;
      max-width: 800px;
      margin: 0 auto;
      padding: 1.5rem 2rem;
      box-sizing: border-box;
      display: flex;
      flex-direction: column;
      gap: 1rem;
      flex: 1;
    }
    .header {
      display: flex;
      align-items: center;
      gap: 1rem;
      border-bottom: 1px solid var(--border-color, #ddd);
      padding-bottom: 1rem;
      flex-shrink: 0;
    }
    h2 { margin: 0; flex: 1; font-size: 1.2rem; }
    select {
      padding: 0.3rem 0.6rem;
      border-radius: 4px;
      border: 1px solid var(--border-color, #ccc);
    }
    u-marked-block {
      display: block;
      min-width: 0;
    }
    .empty {
      padding: 3rem 1rem;
      color: var(--text-muted, #888);
      font-size: 0.95rem;
    }
    .empty.error { color: #c0392b; }
  `;vt([Yt({type:Object})],Te.prototype,"report",2);vt([We()],Te.prototype,"selectedFile",2);vt([We()],Te.prototype,"content",2);vt([We()],Te.prototype,"loading",2);vt([We()],Te.prototype,"errorMsg",2);Te=vt([_o("report-viewer")],Te);const Yo=typeof __VAULT_AI_CONFIG__<"u"?__VAULT_AI_CONFIG__:{},wo=Yo.basePath??"/vault-ai-reports",Rc=Yo.title??"Vault AI 리포트";(async()=>{const t=await En();await vn.load({basepath:wo,iconBasepath:"/icons/",layout:{type:"sidebar",title:Rc,main:t.map(e=>({type:"link",label:e.name,href:`${wo}/${e.folder}`,...e.icon?{icon:e.icon}:{}}))},routes:[...t.map(e=>({path:e.folder,render:()=>Y`<report-viewer .report=${e}></report-viewer>`})),{index:!0,render:()=>t.length>0?Y`<report-viewer .report=${t[0]}></report-viewer>`:Y`<p style="padding:2rem;color:var(--text-muted,#888)">생성된 리포트가 없습니다.</p>`}],theme:{default:"system"}})})();const Ac=Object.freeze(Object.defineProperty({__proto__:null},Symbol.toStringTag,{value:"Module"}));export{j as U,vn as a};
