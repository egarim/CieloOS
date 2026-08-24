import './style.css';
import { initHero } from './scene';

const repoUrl = 'https://github.com/egarim/CieloOS';

document.querySelector<HTMLDivElement>('#app')!.innerHTML = `
  <header class="nav" aria-label="Primary navigation">
    <a class="brand" href="#top" aria-label="CieloOS home"><span class="brand-mark" aria-hidden="true"></span>CieloOS</a>
    <nav>
      <a href="#system">System</a><a href="#agents">Agents</a><a href="#governance">Governance</a>
    </nav>
    <a class="nav-cta" href="${repoUrl}">View on GitHub <span aria-hidden="true">↗</span></a>
  </header>

  <main id="main">
    <section class="hero" id="top" aria-labelledby="hero-title">
      <canvas id="hero-canvas" aria-hidden="true"></canvas>
      <div class="hero-fallback" aria-hidden="true">
        <span class="fallback-line human-line"></span><span class="fallback-line agent-line"></span>
        <span class="fallback-gate"></span><span class="fallback-ray r1"></span><span class="fallback-ray r2"></span><span class="fallback-ray r3"></span>
      </div>
      <div class="hero-copy">
        <p class="eyebrow"><span></span> An operating system built to be operated by AI</p>
        <h1 id="hero-title">One bus.<br><em>Every actor.</em></h1>
        <p class="lede">Humans and AI agents emit the same typed commands. One policy engine decides. One audit trail remembers.</p>
        <div class="hero-actions">
          <a class="button primary" href="#system">See the system <span aria-hidden="true">↓</span></a>
          <a class="button quiet" href="${repoUrl}">Explore the source <span aria-hidden="true">↗</span></a>
        </div>
      </div>
      <div class="hero-legend" aria-label="The CieloOS command flow">
        <span><i class="dot human"></i>Human</span><span><i class="dot agent"></i>Agent</span><span class="legend-rule"></span><strong>Allow · Deny · RequireApproval</strong>
      </div>
    </section>

    <section class="thesis section" id="system">
      <p class="section-number">01 / THE IDEA</p>
      <div class="thesis-grid">
        <h2>The UI is a projection.<br><span>The contract is the OS.</span></h2>
        <div><p>CieloOS does not bolt an agent onto a desktop. It gives people and agents the same governed path to every capability.</p><p class="law">Typed where possible.<br>Pixels where necessary.<br>Policy everywhere.</p></div>
      </div>
    </section>

    <section class="bus-section section" aria-labelledby="bus-title">
      <div class="section-head"><p class="section-number">02 / ONE COMMAND BUS</p><h2 id="bus-title">Different intent in.<br>One accountable action out.</h2></div>
      <div class="bus-diagram" role="img" aria-label="Human and agent requests converge at SubmitAsync, pass ownership, policy and input grants, then reach typed surfaces and the audit log">
        <div class="source-card"><span>Human</span><b>click · console · desktop</b></div>
        <div class="source-card agent-card"><span>Owned agent</span><b>API · loop · inbox</b></div>
        <div class="flow-line"></div>
        <div class="choke"><small>THE SINGLE CHOKE POINT</small><strong>SubmitAsync</strong><span>ownership → policy → input grant</span></div>
        <div class="surface-list"><span>spreadsheet</span><span>session</span><span>console</span><span>desktop</span><span>session-input</span></div>
        <div class="audit-card"><span>AUDIT EVENT</span><strong>joche <i>→</i> joche-agent</strong><small>principal · onBehalfOf</small></div>
      </div>
    </section>

    <section class="agents section" id="agents" aria-labelledby="agents-title">
      <div class="section-head"><p class="section-number">03 / AGENTS AT WORK</p><h2 id="agents-title">Real tools.<br>Governed hands.</h2></div>
      <div class="loop-grid">
        <article class="loop-card"><div class="loop-top"><span>01</span><i class="pulse"></i></div><h3>Console loop</h3><p>The agent observes a tmux pane, decides, then types through <code>console.type</code>. Every keystroke is governed.</p><ol><li><span>Observe</span><b>capture-pane</b></li><li><span>Decide</span><b>model brain</b></li><li><span>Act</span><b>send-keys</b></li></ol></article>
        <article class="loop-card desktop-card"><div class="loop-top"><span>02</span><div class="cursor-glyph">↖</div></div><h3>Desktop loop</h3><p>AT-SPI finds exact interface elements first. Vision is the fallback for what the accessibility tree cannot see.</p><div class="element-demo"><span class="scan"></span><button tabindex="-1">Export file</button><small>button · x 116 · y 82</small></div></article>
      </div>
      <p class="consent-note"><span>Input grants</span> Clicks can run autonomously. Typing and keys require owner consent through a revocable, time-boxed grant.</p>
    </section>

    <section class="governance section" id="governance" aria-labelledby="gov-title">
      <div class="section-head"><p class="section-number">04 / GOVERNANCE</p><h2 id="gov-title">Control is not a layer.<br>It is the route.</h2></div>
      <div class="policy-row"><article><span class="policy-icon allow">✓</span><h3>Allow</h3><p>Known safe actions continue.</p></article><article><span class="policy-icon approval">…</span><h3>Require approval</h3><p>Consent binds to the exact request.</p></article><article><span class="policy-icon deny">×</span><h3>Deny</h3><p>Disallowed actions stop at the gate.</p></article></div>
      <div class="audit-strip"><div><p>DUAL-ACTOR AUDIT</p><strong>Who requested it. Which agent acted. What changed.</strong></div><div class="audit-event"><span>10:42:18</span><b>joche → joche-agent</b><code>desktop.click</code><i>ALLOW</i></div></div>
    </section>

    <section class="sessions section" aria-labelledby="sessions-title">
      <div class="section-head"><p class="section-number">05 / DESKS & SESSIONS</p><h2 id="sessions-title">A durable desk<br>for every owner.</h2><p>One rootless Podman container per session, with a persistent home and a shared owner↔agent volume.</p></div>
      <div class="desk-stage">
        <div class="desk-window"><div class="window-bar"><i></i><i></i><i></i><span>joche-agent / developer desk</span></div><div class="window-body"><aside><b>⌂</b><span>◆</span><span>▦</span></aside><div class="terminal"><p><span>$</span> dotnet new unoapp</p><p class="muted">The template “Uno Platform App” was created.</p><p><span>$</span> <i class="caret"></i></p></div></div></div>
        <div class="desk-labels"><span><b>Office</b> documents + browser</span><span><b>.NET developer</b> SDK + VS Code</span><span><b>Marketing</b> creative workspace</span></div>
      </div>
    </section>

    <section class="models section" aria-labelledby="models-title">
      <div class="models-copy"><p class="section-number">06 / MODELS</p><h2 id="models-title">Bring your own key.<br>Or keep it on-box.</h2><p>CieloOS ships provider-free. A capability registry resolves chat, vision, and embedding through an agent → user → OS cascade.</p><div class="capabilities"><span>chat</span><span>vision</span><span>embedding</span></div></div>
      <div class="model-orbits" aria-hidden="true"><div class="orbit o1"><span>Agent</span></div><div class="orbit o2"><span>User</span></div><div class="orbit o3"><span>OS</span></div><div class="model-core">MODEL<br>REGISTRY</div></div>
    </section>

    <section class="try section" aria-labelledby="try-title"><p class="section-number">07 / TRY CIELOOS</p><h2 id="try-title">Your agent should answer<br>to <em>you.</em></h2><p>Run CieloOS on Ubuntu 24.04+ or Windows through WSL2. Start provider-free, then connect the model you choose.</p><a class="button primary" href="${repoUrl}">Get the source on GitHub <span aria-hidden="true">↗</span></a></section>
  </main>
  <footer><a class="brand" href="#top"><span class="brand-mark" aria-hidden="true"></span>CieloOS</a><p>Built for shared agency.<br>Designed for human ownership.</p><a href="${repoUrl}">GitHub ↗</a></footer>
`;

const reduced = window.matchMedia('(prefers-reduced-motion: reduce)');
const canvas = document.querySelector<HTMLCanvasElement>('#hero-canvas')!;
const fallback = document.querySelector<HTMLElement>('.hero-fallback')!;

const startScene = () => {
  if (reduced.matches) {
    document.documentElement.classList.add('reduced-motion');
    fallback.hidden = false;
    return;
  }
  initHero(canvas).then((cleanup) => {
    if (!cleanup) fallback.hidden = false;
    else fallback.hidden = true;
  });
};

if ('IntersectionObserver' in window) {
  const observer = new IntersectionObserver(([entry]) => {
    if (entry.isIntersecting) { observer.disconnect(); startScene(); }
  });
  observer.observe(document.querySelector('.hero')!);
} else startScene();
