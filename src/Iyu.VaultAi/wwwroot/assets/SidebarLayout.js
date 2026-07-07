import{n,i as v,b as c,t as x,A as _,d as y,o as P,e as S,r as E}from"./lit.js";import{U as B,a as k}from"./index.js";import"./highlight.js";import"./marked.js";import"./katex.js";var j=Object.defineProperty,T=(e,t,o,s)=>{for(var r=void 0,i=e.length-1,a;i>=0;i--)(a=e[i])&&(r=a(t,o,r)||r);return r&&j(t,o,r),r};class b extends B{static{this.styles=super.styles}updated(t){super.updated(t),t.has("styles")&&this.styles&&this.applyToParts(this.styles)}applyToParts(t){const o=Object.entries(t);for(const[s,r]of o)if(r)if(s==="host")this.assignToObject(this,r);else{const i=this.renderRoot?.querySelector(`[part="${String(s)}"]`);if(!i)continue;this.assignToObject(i,r)}}assignToObject(t,o){for(const[s,r]of Object.entries(o)){if(r==null)continue;const i=String(r);if(s.startsWith("--")||s.includes("-")){t.style.setProperty(s,i);continue}try{t.style[s]=i}catch{const a=s.replace(/[A-Z]/g,O=>"-"+O.toLowerCase());t.style.setProperty(a,i)}}}}T([n({type:Object,attribute:!1})],b.prototype,"styles");const C=v`
  :host {
    display: flex;
    flex-direction: column;
    gap: 8px;
    margin: 8px 0;
  }

  .header {
    display: flex;
    flex-direction: column;
    padding: 8px 12px 4px;
  }

  .title {
    color: var(--u-neutral-700);
    font-size: 12px;
    font-weight: 700;
    letter-spacing: 0.5px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  
  .subtitle {
    color: var(--u-neutral-600);
    font-size: 11px;
    font-weight: 300;
  }

  .items {
    display: flex;
    flex-direction: column;
  }
`;var R=Object.defineProperty,z=Object.getOwnPropertyDescriptor,L=Object.getPrototypeOf,D=Reflect.get,w=(e,t,o,s)=>{for(var r=s>1?void 0:s?z(t,o):t,i=e.length-1,a;i>=0;i--)(a=e[i])&&(r=(s?a(t,o,r):a(r))||r);return s&&r&&R(t,o,r),r},M=(e,t,o)=>D(L(e),o,t);let u=class extends b{constructor(){super(...arguments),this.compact=!1}render(){return c`
      <div class="header" part="header" ?hidden=${this.compact}>
        <span class="title" part="title">
          ${this.mainTitle}
        </span>
        <span class="subtitle" part="subtitle" ?hidden=${!this.subTitle}>
          ${this.subTitle}
        </span>
      </div>
      
      <div class="items" part="items">
        <slot></slot>
      </div>
    `}};u.styles=[M(u,u,"styles"),C];w([n({type:Boolean})],u.prototype,"compact",2);w([n({type:String})],u.prototype,"mainTitle",2);w([n({type:String})],u.prototype,"subTitle",2);u=w([x("u-sidebar-section")],u);const A=v`
  :host {
    display: block;
    color: var(--u-neutral-800);
    background-color: transparent;
    border-radius: 8px;
    transition: all 0.2s ease;
    cursor: pointer;
  }
  :host(:hover) {
    color: var(--u-txt-color-hover);
    background-color: var(--u-bg-color-hover);
  }
  :host([selected]) {
    color: var(--u-txt-color-inverse);
    background-color: var(--u-blue-600);
    box-shadow: 0 1px 3px var(--u-shadow-color-weak);
  }
  :host([selected]:hover) {
    color: var(--u-txt-color-inverse);
    background-color: var(--u-blue-700);
    box-shadow: 0 2px 6px var(--u-shadow-color-normal);
  }
  
  .container {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: flex-start;
    gap: 12px;
    padding: 8px 12px;
  }
  .container[compact] {
    justify-content: center;
    gap: 0;
    padding: 8px;
  }

  u-icon {
    flex-shrink: 0;
    color: inherit;
    font-size: 20px;
  }

  span {
    flex: 1;
    font-size: 14px;
    line-height: 20px;
    font-weight: 500;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
`;var G=Object.defineProperty,U=Object.getOwnPropertyDescriptor,H=Object.getPrototypeOf,I=Reflect.get,f=(e,t,o,s)=>{for(var r=s>1?void 0:s?U(t,o):t,i=e.length-1,a;i>=0;i--)(a=e[i])&&(r=(s?a(t,o,r):a(r))||r);return s&&r&&G(t,o,r),r},q=(e,t,o)=>I(H(e),o,t);let l=class extends b{constructor(){super(...arguments),this.selected=!1,this.compact=!1}render(){return c`
      <u-link .href=${this.href||"#"}>
        <div class="container" part="base" ?compact=${this.compact}>
          <u-icon part="icon" ?hidden=${!this.icon}
            .name=${this.icon}
          ></u-icon>
          <span part="label" ?hidden=${this.compact}>
            ${this.label}
          </span>
        </div>
      </u-link>
    `}};l.styles=[q(l,l,"styles"),A];f([n({type:Boolean,reflect:!0})],l.prototype,"selected",2);f([n({type:Boolean})],l.prototype,"compact",2);f([n({type:String})],l.prototype,"icon",2);f([n({type:String})],l.prototype,"label",2);f([n({type:String})],l.prototype,"href",2);f([n({type:String})],l.prototype,"pattern",2);l=f([x("u-sidebar-link")],l);const Y=v`
  :host {
    display: flex;
    flex-direction: column;
    color: var(--u-neutral-800);
  }

  button {
    all: unset;
    width: 100%;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 8px 12px;
    background-color: transparent;
    border: none;
    border-radius: 8px;
    transition: all 0.2s ease;
    cursor: pointer;
  }
  button[compact] {
    justify-content: center;
    gap: 0;
    padding: 8px;
  }
  button[selected] {
    color: var(--u-blue-700);
  }
  button:hover {
    color: var(--u-txt-color-hover);
    background-color: var(--u-bg-color-hover);
  }
  button:active {
    background-color: var(--u-bg-color-active);
  }
  button:focus-visible {
    outline: 2px solid #6666ff;
    outline-offset: 2px;
  }

  .icon {
    flex-shrink: 0;
    color: inherit;
    font-size: 20px;
  }

  .label {
    flex: 1;
    font-size: 14px;
    line-height: 20px;
    font-weight: 600;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .caret {
    color: inherit;
    font-size: 16px !important;
    transition: transform 0.2s ease;
  }
  .caret[collapsed] {
    transform: rotate(-90deg);
  }

  .items {
    display: flex;
    flex-direction: column;
    gap: 4px;
    margin-top: 4px;
    margin-left: 32px;
    border-left: 2px solid var(--u-border-color-weak);
    padding-left: 8px;
    overflow: hidden;
    transition: all 0.3s ease;
  }
  .items[collapsed] {
    margin-top: 0;
    height: 0;
    opacity: 0;
  }
`;var K=Object.defineProperty,N=Object.getOwnPropertyDescriptor,F=Object.getPrototypeOf,W=Reflect.get,g=(e,t,o,s)=>{for(var r=s>1?void 0:s?N(t,o):t,i=e.length-1,a;i>=0;i--)(a=e[i])&&(r=(s?a(t,o,r):a(r))||r);return s&&r&&K(t,o,r),r},V=(e,t,o)=>W(F(e),o,t);let p=class extends b{constructor(){super(...arguments),this.compact=!1,this.selected=!1,this.collapsed=!0,this.handleButtonClick=e=>{this.compact?this.shadowRoot?.querySelector("slot")?.assignedElements({flatten:!0}).find(s=>s instanceof l)?.renderRoot.querySelector("u-link")?.click():this.collapsed=!this.collapsed}}render(){return c`
      <button part="header"
        ?compact=${this.compact}
        ?selected=${this.selected}
        @click=${this.handleButtonClick}>
        <u-icon class="icon" part="icon" ?hidden=${!this.icon}
          .name=${this.icon}
        ></u-icon>
        <span class="label" part="label" ?hidden=${this.compact}>
          ${this.label}
        </span>
        <u-icon class="caret" part="caret" ?hidden=${this.compact}
          ?collapsed=${this.collapsed}
          lib="internal"
          name="chevron-down"
        ></u-icon>
      </button>
      
      <div class="items" part="items" ?hidden=${this.compact}
        ?collapsed=${this.collapsed}>
        <slot></slot>
      </div>
    `}};p.styles=[V(p,p,"styles"),Y];g([n({type:Boolean,reflect:!0})],p.prototype,"compact",2);g([n({type:Boolean,reflect:!0})],p.prototype,"selected",2);g([n({type:Boolean,reflect:!0})],p.prototype,"collapsed",2);g([n({type:String})],p.prototype,"icon",2);g([n({type:String})],p.prototype,"label",2);p=g([x("u-sidebar-group")],p);const X=v`
  :host {
    display: block;
    width: 100%;
    color: var(--u-txt-color);
    background-color: transparent;
    border: none;
    border-radius: 8px;
    transition: all 0.2s ease;
    cursor: pointer;
  }
  :host(:hover) {
    color: var(--u-txt-color-hover);
    background-color: var(--u-bg-color-hover);
  }
  :host(:active) {
    background-color: var(--u-bg-color-active);
  }

  button {
    all: unset;
    width: 100%;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: flex-start;
    gap: 12px;
    padding: 8px 12px;
  }
  button[compact] {
    justify-content: center;
    gap: 0;
    padding: 8px;
  }
  button:focus-visible {
    outline: 2px solid #6666ff;
    outline-offset: 2px;
  }
  
  u-icon {
    flex-shrink: 0;
    color: inherit;
    font-size: 20px;
  }

  span {
    flex: 1;
    font-size: 14px;
    font-weight: 500;
    line-height: 20px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
`;var Z=Object.defineProperty,J=Object.getOwnPropertyDescriptor,Q=Object.getPrototypeOf,ee=Reflect.get,$=(e,t,o,s)=>{for(var r=s>1?void 0:s?J(t,o):t,i=e.length-1,a;i>=0;i--)(a=e[i])&&(r=(s?a(t,o,r):a(r))||r);return s&&r&&Z(t,o,r),r},te=(e,t,o)=>ee(Q(e),o,t);let h=class extends b{constructor(){super(...arguments),this.compact=!1}render(){return c`
      <button part="base" ?compact=${this.compact}>
        <u-icon part="icon" ?hidden=${!this.icon}
          .name=${this.icon}
        ></u-icon>
        <span part="label" ?hidden=${this.compact}>
          ${this.label}
        </span>
      </button>
    `}};h.styles=[te(h,h,"styles"),X];$([n({type:Boolean,reflect:!0})],h.prototype,"compact",2);$([n({type:String})],h.prototype,"icon",2);$([n({type:String})],h.prototype,"label",2);h=$([x("u-sidebar-button")],h);const oe=v`
  :host {
    position: relative;
    display: flex;
    flex-direction: row;
    width: 100%;
    height: 100%;
    font-family: var(--u-font-base);
    overflow: hidden;
  }
  :host([state="mobile"]),
  :host([state="mobile-open"]) {
    flex-direction: column;
  }

  .logo {
    color: var(--u-txt-color);
    font-size: 24px;
    cursor: pointer;
  }
  .logo:hover {
    color: var(--u-txt-color-hover);
  }

  .title {
    flex: 1;
    color: var(--u-txt-color);
    font-size: 18px;
    line-height: 24px;
    font-weight: 700;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .toggler {
    color: inherit;
    font-size: 20px;
    padding: 0px;
    border: none;
    background: transparent;
  }
  .toggler:hover {
    color: var(--u-txt-color-hover);
  }

  /* Sidebar Container */
  .sidebar {
    position: relative;
    z-index: 1000;
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    gap: 8px;
    height: 100%;
    background: var(--u-panel-bg-color);
    border-right: 1px solid var(--u-border-color);
    transition: all 0.3s ease;
  }
  /* Sidebar states */
  .sidebar[state="default"] {
    width: 260px;
  }
  .sidebar[state="slim"] {
    width: 64px;
  }
  .sidebar[state="slim"] .sidebar-header {
    flex-direction: column;
    gap: 16px;
    padding: 16px;
  }
  .sidebar[state="modal"] {
    width: 260px;
    position: absolute;
    top: 0;
    left: 0;
  }
  .sidebar[state="mobile"] {
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    transform: translateY(-100%);
  }
  .sidebar[state="mobile-open"] {
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    transform: translateY(0);
  }

  /* Mobile Header */
  .mobile-header {
    position: relative;
    z-index: 1001;
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 16px;
    background: var(--u-panel-bg-color);
    border-bottom: 1px solid var(--u-border-color-weak);
    user-select: none;
  }

  /* Sidebar Header */
  .sidebar-header {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 16px;
    border-bottom: 1px solid var(--u-border-color-weak);
    user-select: none;
  }

  /* Sidebar Main Menu */
  .sidebar-main {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 8px;
    overflow-x: hidden;
    overflow-y: auto;
  }

  /* Sidebar Footer */
  .sidebar-footer {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 8px;
    border-top: 1px solid var(--u-border-color-weak);
  }
  .sidebar-footer:empty {
    display: none;
  }

  /* Main Content */
  .main {
    position: relative;
    flex: 1;
    background: var(--u-bg-color);
    overflow: auto;
    outline: none;
  }

  .main u-progress-bar {
    --progress-bar-height: 4px;
    --progres-bar-track-color: transparent;
    
    position: absolute;
    z-index: 100;
    top: 0;
    left: 0;
    right: 0;
    opacity: 0;
    transform: translateY(-4px);
    transition: opacity 0.3s ease, transform 0.3s ease;
    pointer-events: none;
  }
  .main u-progress-bar[visible] {
    opacity: 1;
    transform: translateY(0);
  }
  .main u-progress-bar[error] {
    --progress-bar-color: var(--u-red-500);
  }

  /* Backdrop for modal mode */
  .backdrop {
    position: absolute;
    z-index: 100;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background: var(--u-overlay-bg-color);
  }
`;var re=Object.defineProperty,se=Object.getOwnPropertyDescriptor,ie=Object.getPrototypeOf,ae=Reflect.get,m=(e,t,o,s)=>{for(var r=s>1?void 0:s?se(t,o):t,i=e.length-1,a;i>=0;i--)(a=e[i])&&(r=(s?a(t,o,r):a(r))||r);return s&&r&&re(t,o,r),r},ne=(e,t,o)=>ae(ie(e),o,t);function le(e){const t=e.tagName;if(t==="INPUT"||t==="TEXTAREA"||t==="SELECT"||e.isContentEditable)return!0;const o=e.getAttribute("role");return o==="textbox"||o==="searchbox"||o==="combobox"||o==="spinbutton"}let d=class extends b{constructor(){super(...arguments),this.state="default",this.context=null,this.isMatchedLink=e=>!this.context||!e?!1:(e=typeof e=="string"?new URLPattern(e,window.location.origin):e,e.test(this.context.path,window.location.origin)),this.handleBrandLogoClick=()=>{k.navigate("")},this.handleToggleButtonClick=()=>{const e=k.screen??"large";e==="large"?this.state=this.state==="default"?"slim":"default":e==="medium"?this.state=this.state==="slim"?"modal":"slim":e==="small"?this.state=this.state==="mobile"?"mobile-open":"mobile":console.warn("Unknown screen size:",e)},this.handleBackdropClick=()=>{this.state="slim"},this.handleRouteBegin=e=>{this.progressBarEl.setAttribute("visible",""),this.progressBarEl.value=0,this.state==="modal"&&(this.state="slim"),this.state==="mobile-open"&&(this.state="mobile"),this.context=e.context},this.handleRouteProgress=e=>{this.progressBarEl.value=e.progress},this.handleRouteDone=e=>{this.progressBarEl.value=100,setTimeout(()=>{this.progressBarEl.removeAttribute("visible")},300),this.shadowRoot?.querySelector(".main")?.focus({preventScroll:!0})},this.handleRouteError=e=>{this.progressBarEl.setAttribute("error",""),this.progressBarEl.value=100,setTimeout(()=>{this.progressBarEl.removeAttribute("visible"),this.progressBarEl.removeAttribute("error")},300)},this._handleMainKeydown=e=>{const t=e.composedPath()[0];if(t instanceof HTMLElement&&le(t))return;const o=this.shadowRoot?.querySelector(".main");if(!o)return;const s=o.clientHeight,r=80;switch(e.key){case" ":case"PageDown":o.scrollTop+=e.shiftKey?-s:s;break;case"PageUp":o.scrollTop-=s;break;case"End":o.scrollTop=o.scrollHeight;break;case"Home":o.scrollTop=0;break;case"ArrowDown":o.scrollTop+=r;break;case"ArrowUp":o.scrollTop-=r;break;default:return}e.preventDefault()},this.handleScreenResize=e=>{const t=e.detail.size;t==="large"?this.state="default":t==="medium"?this.state="slim":t==="small"?this.state="mobile":console.warn("Unknown screen size:",t)}}connectedCallback(){super.connectedCallback(),window.addEventListener("route-begin",this.handleRouteBegin),window.addEventListener("route-done",this.handleRouteDone),window.addEventListener("route-progress",this.handleRouteProgress),window.addEventListener("route-error",this.handleRouteError),window.addEventListener("screen-resize",this.handleScreenResize)}disconnectedCallback(){window.removeEventListener("route-begin",this.handleRouteBegin),window.removeEventListener("route-done",this.handleRouteDone),window.removeEventListener("route-progress",this.handleRouteProgress),window.removeEventListener("route-error",this.handleRouteError),window.removeEventListener("screen-resize",this.handleScreenResize),super.disconnectedCallback()}willUpdate(e){super.willUpdate(e),e.has("config")&&(this.styles=this.config?.styles)}render(){return this.config?c`
      <!-- Mobile Header -->
      <div class="mobile-header" part="mobile-header" ?hidden="${!this.state.startsWith("mobile")}">
        <u-icon class="logo"
          .name="${this.config.logo}"
          @click=${this.handleBrandLogoClick}
        ></u-icon>
        <span class="title">
          ${this.config.title}
        </span>
        <u-button class="toggler"
          @click=${this.handleToggleButtonClick}>
          <u-icon 
            lib="bootstrap" 
            name=${this.state==="mobile-open"?"x-lg":"list"}
          ></u-icon>
        </u-button>
      </div>

      <!-- Sidebar -->
      <aside class="sidebar" part="sidebar" state="${this.state}">
        <!-- Sidebar Header -->
        <div class="sidebar-header" part="sidebar-header">
          <u-icon class="logo"
            .name="${this.config.logo}"
            @click=${this.handleBrandLogoClick}
          ></u-icon>
          <span class="title" ?hidden=${this.state==="slim"}>
            ${this.config.title}
          </span>
          <u-button class="toggler"
            @click=${this.handleToggleButtonClick}>
            <u-icon 
              lib="bootstrap" 
              name="layout-sidebar"
            ></u-icon>
          </u-button>
        </div>

        <!-- Sidebar Navigation Menu -->
        <nav class="sidebar-main" part="sidebar-main" scrollable>
          ${y(this.config.main??[],(e,t)=>t,e=>this.renderItem(e))}
        </nav>

        <!-- Sidebar Footer -->
        <div class="sidebar-footer" part="sidebar-footer">
          ${y(this.config.footer??[],(e,t)=>t,e=>this.renderItem(e))}
        </div>
      </aside>

      <!-- Main Content -->
      <div class="main" part="main" scrollable tabindex="-1" @keydown=${this._handleMainKeydown}>
        <u-progress-bar part="progress"></u-progress-bar>

        <slot></slot>
      </div>

      <!-- Backdrop for modal state -->
      <div class="backdrop" ?hidden="${this.state!=="modal"}"
        @click="${this.handleBackdropClick}"
      ></div>
    `:_}renderItem(e){if(!e)return _;if(e.type==="html"){const t=e.render(this.state);return typeof t=="string"?P(t):c`${t}`}else{if(e.type==="button")return c`
        <u-sidebar-button
          ?compact=${this.state==="slim"}
          .icon="${e.icon}"
          .label="${e.label}"
          .styles="${e.styles}"
          @click="${e.onClick}"
        ></u-sidebar-button>
      `;if(e.type==="link"){const t=this.isMatchedLink(e.pattern||e.href);return c`
        <u-sidebar-link
          ?compact=${this.state==="slim"}
          ?selected=${t}
          .icon="${e.icon}"
          .label="${e.label}"
          .href="${e.href}"
          .pattern="${e.pattern}"
          .styles="${e.styles}"
        ></u-sidebar-link>
      `}else{if(e.type==="section")return c`
        <u-sidebar-section
          ?compact=${this.state==="slim"}
          .mainTitle="${e.title}"
          .subTitle="${e.subTitle}"
          .styles="${e.styles}">
          ${y(e.items,(t,o)=>o,t=>this.renderItem(t))}
        </u-sidebar-section>
      `;if(e.type==="group"){const t=e.items.some(o=>this.isMatchedLink(o.pattern||o.href));return c`
        <u-sidebar-group
          ?compact=${this.state==="slim"}
          ?selected=${t}
          ?collapsed="${e.collapsed??!1}"
          .icon="${e.icon}"
          .label="${e.label}"
          .styles="${e.styles}">
          ${y(e.items,(o,s)=>s,o=>this.renderItem(o))}
        </u-sidebar-group>
      `}else return _}}}};d.styles=[ne(d,d,"styles"),oe];m([n({type:String,reflect:!0})],d.prototype,"state",2);m([n({type:Object})],d.prototype,"config",2);m([S("u-progress-bar")],d.prototype,"progressBarEl",2);m([E()],d.prototype,"context",2);d=m([x("u-sidebar-layout")],d);export{d as SidebarLayout};
