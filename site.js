(() => {
  const root = document.documentElement;

  // Theme toggle
  const themeBtn = document.querySelector('button.theme');
  if (themeBtn) {
    themeBtn.addEventListener('click', () => {
      const dark = root.dataset.theme
        ? root.dataset.theme === 'dark'
        : matchMedia('(prefers-color-scheme: dark)').matches;
      const next = dark ? 'light' : 'dark';
      root.dataset.theme = next;
      try { localStorage.setItem('theme', next); } catch (e) {}
    });
  }

  // Language toggle (default: zh)
  const TITLES = {
    zh: '尖塔求解 · 把一场《杀戮尖塔 2》战斗装进 4 KB 连续内存',
    en: 'Spire Solver · Fitting a Slay the Spire 2 Fight into 4 KB of Contiguous Memory',
  };

  function applyLang(lang) {
    const normalized = lang === 'en' ? 'en' : 'zh';
    if (normalized === 'en') {
      root.dataset.lang = 'en';
      root.lang = 'en';
    } else {
      delete root.dataset.lang;
      root.lang = 'zh-CN';
    }
    document.title = TITLES[normalized];
  }

  const langBtn = document.querySelector('button.langbtn');
  if (langBtn) {
    langBtn.addEventListener('click', () => {
      const next = root.dataset.lang === 'en' ? 'zh' : 'en';
      applyLang(next);
      try { localStorage.setItem('lang', next); } catch (e) {}
    });
  }

  // Mobile TOC menu
  const menu = document.querySelector('button.menu');
  const toc = document.getElementById('toc');
  if (menu && toc) {
    const close = () => {
      toc.classList.remove('open');
      menu.setAttribute('aria-expanded', 'false');
    };
    menu.addEventListener('click', () => {
      const open = toc.classList.toggle('open');
      menu.setAttribute('aria-expanded', String(open));
    });
    toc.addEventListener('click', e => {
      if (e.target.closest('a')) close();
    });
    addEventListener('keydown', e => {
      if (e.key === 'Escape') close();
    });
  }

  // Scroll-spy for TOC
  if (toc && 'IntersectionObserver' in window) {
    const links = [...toc.querySelectorAll('a[href^="#"]')];
    const byId = new Map();
    for (const a of links) {
      const id = a.getAttribute('href').slice(1);
      if (!byId.has(id)) byId.set(id, []);
      byId.get(id).push(a);
    }
    const setCurrent = id => {
      for (const a of links) a.removeAttribute('aria-current');
      const matched = byId.get(id);
      if (matched) {
        for (const a of matched) a.setAttribute('aria-current', 'true');
      }
    };
    const visible = new Set();
    const io = new IntersectionObserver(entries => {
      for (const e of entries) {
        if (e.isIntersecting) visible.add(e.target.id);
        else visible.delete(e.target.id);
      }
      for (const id of byId.keys()) {
        if (visible.has(id)) {
          setCurrent(id);
          break;
        }
      }
    }, { rootMargin: '-80px 0px -65% 0px' });
    for (const id of byId.keys()) {
      const el = document.getElementById(id);
      if (el) io.observe(el);
    }
  }
})();
