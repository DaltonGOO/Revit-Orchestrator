/* Revit Orchestrator site JS
   Animation patterns mirror daltongoo.github.io: hero word-reveal,
   title-underline grow, and a scroll progress bar. Plain DOM/CSS
   transitions — no anime.js dependency required. */

(function () {
  const prefersReduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function splitWords(el) {
    if (!el) return [];
    const text = (el.textContent || '').trim();
    el.textContent = '';
    return text.split(/\s+/).map((w) => {
      const span = document.createElement('span');
      span.className = 'word';
      span.textContent = w + ' ';
      span.style.opacity = '0';
      span.style.transform = 'translateY(12px)';
      span.style.transition = 'opacity .5s ease-out, transform .5s ease-out';
      el.appendChild(span);
      return span;
    });
  }

  function heroReveal() {
    const h = document.querySelector('#hero-title');
    if (!h) return;
    if (prefersReduced) { h.style.opacity = 1; return; }
    const parts = splitWords(h);
    parts.forEach((s, i) => {
      setTimeout(() => {
        s.style.opacity = '1';
        s.style.transform = 'translateY(0)';
      }, i * 45);
    });
  }

  function titleUnderline() {
    const us = document.querySelectorAll('.title-underline');
    us.forEach((u) => {
      if (prefersReduced) { u.style.width = '100%'; return; }
      u.style.transition = 'width .65s cubic-bezier(.2,.7,.2,1)';
      requestAnimationFrame(() => { u.style.width = '100%'; });
    });
  }

  function readingProgress() {
    let bar = document.getElementById('progress-bar');
    if (!bar) {
      bar = document.createElement('div');
      bar.id = 'progress-bar';
      document.body.appendChild(bar);
    }
    if (!prefersReduced) {
      bar.style.transition = 'width .12s linear';
    }
    const update = () => {
      const max = document.documentElement.scrollHeight - window.innerHeight;
      const pct = max > 0 ? (window.scrollY / max) * 100 : 0;
      bar.style.width = pct + '%';
    };
    update();
    window.addEventListener('scroll', update, { passive: true });
    window.addEventListener('resize', update);
  }

  function highlightActiveNav() {
    const path = location.pathname.replace(/\/+$/, '').split('/').pop() || 'index.html';
    document.querySelectorAll('.nav-links a').forEach((a) => {
      const href = (a.getAttribute('href') || '').split('/').pop();
      if (href === path) a.classList.add('active');
    });
  }

  document.addEventListener('DOMContentLoaded', () => {
    heroReveal();
    titleUnderline();
    readingProgress();
    highlightActiveNav();
  });
})();
