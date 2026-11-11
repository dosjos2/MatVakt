const $ = (s, r = document) => r.querySelector(s);
const view = $('#view');
let tab = 'food';

async function api(path, method = 'GET', body) {
  const res = await fetch('/api' + path, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
    credentials: 'same-origin'
  });
  if (res.status === 401) { showLogin(); throw new Error('401'); }
  if (!res.ok) { const t = await res.text(); toast(t || 'Noe gikk galt'); throw new Error(t); }
  const ct = res.headers.get('content-type') || '';
  return ct.includes('json') ? res.json() : null;
}

function esc(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
function toast(msg) { const t = $('#toast'); t.textContent = msg; t.hidden = false; clearTimeout(toast.h); toast.h = setTimeout(() => t.hidden = true, 2800); }
function safeUrl(u) { return /^https?:\/\//i.test(u || '') ? u : ''; }

function daysTag(iso) {
  if (!iso) return '<span class="tag">Ingen dato</span>';
  const d = Math.round((new Date(iso + 'T00:00:00') - new Date(new Date().toDateString())) / 864e5);
  const dt = new Date(iso).toLocaleDateString('nb-NO');
  if (d < 0) return `<span class="tag exp">Utløpt ${dt}</span>`;
  if (d === 0) return `<span class="tag exp">Utløper i dag</span>`;
  if (d <= 3) return `<span class="tag soon">${d} dager igjen (${dt})</span>`;
  return `<span class="tag ok">${dt} (${d} d)</span>`;
}

// ---------- Innlogging ----------
async function showLogin() {
  $('#app').hidden = true; $('#login').hidden = false;
  const cfg = await (await fetch('/api/auth/config')).json();
  if (cfg.googleClientId) {
    const init = () => {
      if (!window.google?.accounts) return setTimeout(init, 200);
      google.accounts.id.initialize({
        client_id: cfg.googleClientId,
        callback: async r => { await fetch('/api/auth/google', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ credential: r.credential }) }); boot(); }
      });
      google.accounts.id.renderButton($('#google-btn'), { theme: 'outline', size: 'large', text: 'signin_with', locale: 'no' });
    };
    init();
  } else {
    $('#login-note').textContent = 'Google-innlogging er ikke konfigurert (Google:ClientId). Demo-innlogging er aktiv.';
  }
  $('#demo-btn').hidden = !cfg.demo;
}
$('#demo-btn').onclick = async () => { await fetch('/api/auth/demo', { method: 'POST' }); boot(); };
$('#logout').onclick = async () => { await fetch('/api/auth/logout', { method: 'POST' }); location.reload(); };

async function boot() {
  const r = await fetch('/api/auth/me');
  if (!r.ok) return showLogin();
  const me = await r.json();
  $('#login').hidden = true; $('#app').hidden = false;
  $('#user-name').textContent = me.name;
  render();
}

document.querySelectorAll('nav button').forEach(b => b.onclick = () => {
  tab = b.dataset.tab;
  document.querySelectorAll('nav button').forEach(x => x.classList.toggle('active', x === b));
  render();
});

function render() { ({ food: renderFood, suggest: renderSuggest, recipes: renderRecipes, shopping: renderShopping })[tab](); updateShopBadge(); }
async function updateShopBadge() {
  try { const n = (await api('/shopping')).filter(s => !s.done).length; const b = $('#shop-count'); b.textContent = n; b.hidden = !n; } catch { }
}

// ---------- Matvarer ----------
async function renderFood() {
  const items = await api('/food');
  const groups = {};
  items.forEach(i => (groups[i.name.toLowerCase()] ??= []).push(i));
  view.innerHTML = `
    <div class="card"><form class="row" id="add-food">
      <label>Matvare<input name="name" required placeholder="f.eks. Melk"></label>
      <label>Antall<input name="quantity" type="number" min="0.01" step="any" value="1" style="width:90px"></label>
      <label>Enhet<select name="unit"><option>stk</option><option>l</option><option>dl</option><option>kg</option><option>g</option><option>pk</option><option>boks</option></select></label>
      <label>Utløpsdato<input name="expiry" type="date"></label>
      <button class="primary">Legg til</button>
    </form>
    <p class="muted">Tips: Registrer samme vare flere ganger hvis den har ulike utløpsdatoer. Basisvarer og krydder (salt, mel, olje …) trenger du ikke registrere.</p></div>
    <div class="card" id="food-list">${items.length ? '' : '<p class="muted">Ingen matvarer registrert ennå.</p>'}</div>
    <p><button class="small" id="show-used">Vis oppbrukte</button></p><div id="used"></div>`;
  const list = $('#food-list');
  Object.values(groups).forEach(g => {
    const total = g.reduce((s, i) => s + i.quantity, 0);
    if (g.length > 1) list.insertAdjacentHTML('beforeend', `<div class="muted" style="margin-top:.5rem"><b>${esc(g[0].name)}</b> – totalt ${total} fordelt på ${g.length} utløpsdatoer</div>`);
    g.forEach(i => list.insertAdjacentHTML('beforeend', foodRow(i)));
  });
  $('#add-food').onsubmit = async e => {
    e.preventDefault(); const f = new FormData(e.target);
    await api('/food', 'POST', { name: f.get('name'), quantity: +f.get('quantity'), unit: f.get('unit'), expiryDate: f.get('expiry') || null });
    renderFood();
  };
  $('#show-used').onclick = async () => {
    const all = (await api('/food?includeUsed=true')).filter(i => i.usedUp);
    $('#used').innerHTML = `<div class="card">${all.length ? all.map(foodRow).join('') : '<p class="muted">Ingen oppbrukte varer.</p>'}</div>`;
    bindFood($('#used'));
  };
  bindFood(view);
}
function foodRow(i) {
  return `<div class="item ${i.usedUp ? 'used' : ''}" data-id="${i.id}">
    <span class="name">${esc(i.name)}</span>
    <span>${i.quantity} ${esc(i.unit)}</span>${daysTag(i.expiryDate)}
    ${i.usedUp ? '<button class="small" data-act="restore">Angre</button>' : `
      <button class="small" data-act="dec" title="Bruk én">−1</button>
      <button class="small" data-act="inc" title="Legg til én">+1</button>
      <button class="small" data-act="used">Oppbrukt</button>`}
    <button class="small danger" data-act="del" title="Slett">🗑</button></div>`;
}
function bindFood(root) {
  root.querySelectorAll('.item[data-id] button').forEach(b => b.onclick = async () => {
    const id = b.closest('.item').dataset.id, act = b.dataset.act;
    if (act === 'used') await api(`/food/${id}/use`, 'POST', {});
    else if (act === 'dec') await api(`/food/${id}/use`, 'POST', { amount: 1 });
    else if (act === 'restore') await api(`/food/${id}/restore`, 'POST');
    else if (act === 'del') await api(`/food/${id}`, 'DELETE');
    else if (act === 'inc') {
      const it = (await api('/food?includeUsed=true')).find(x => x.id == id);
      await api(`/food/${id}`, 'PUT', { ...it, quantity: it.quantity + 1 });
    }
    renderFood();
  });
}

// ---------- Oppskrifter ----------
function recipeCard(r) {
  const miss = r.missing ? `<span class="tag miss">Mangler ${r.missing}</span>` : '<span class="tag ok">Alt hjemme ✔</span>';
  const exp = r.expiringUsed ? `<span class="tag soon">Bruker ${r.expiringUsed} som snart utløper</span>` : '';
  const img = safeUrl(r.imageUrl);
  return `<div class="card recipe" data-id="${r.id}">${img ? `<img src="${esc(img)}" alt="" loading="lazy" referrerpolicy="no-referrer">` : ''}
    <h3>${esc(r.title)}</h3><div class="muted">${esc(r.source)} · ${r.servings} porsjoner</div><div>${miss} ${exp}</div></div>`;
}
function bindCards(root) { root.querySelectorAll('.recipe').forEach(c => c.onclick = () => openRecipe(c.dataset.id)); }

async function renderSuggest() {
  const list = await api('/suggestions');
  view.innerHTML = `<h2>Middagsforslag</h2><p class="muted">Rangert etter hvor mange av matvarene dine som snart går ut på dato.</p>` +
    (list.length ? `<div class="grid">${list.map(recipeCard).join('')}</div>` : '<div class="card">Ingen forslag ennå – registrer noen matvarer først.</div>');
  bindCards(view);
}

async function renderRecipes(q = '') {
  const list = await api('/recipes' + (q ? '?q=' + encodeURIComponent(q) : ''));
  view.innerHTML = `<h2>Middagsretter</h2>
    <div class="toolbar"><input id="q" placeholder="Søk …" value="${esc(q)}"><button class="small" id="search">Søk</button>
    <input id="imp-url" placeholder="Importer fra URL (matprat.no, gladkokken.no, trinesmatblogg.no …)" style="flex:1;min-width:240px"><button class="small" id="imp">Importer</button></div>
    <div class="grid">${list.map(recipeCard).join('')}</div>`;
  $('#search').onclick = () => renderRecipes($('#q').value);
  $('#q').onkeydown = e => { if (e.key === 'Enter') renderRecipes($('#q').value); };
  $('#imp').onclick = async () => {
    const url = $('#imp-url').value.trim(); if (!url) return;
    const r = await api('/recipes/import', 'POST', { url }); toast('Importerte: ' + r.title); renderRecipes();
  };
  bindCards(view);
}

async function openRecipe(id) {
  const r = await api('/recipes/' + id);
  const ing = r.ingredients.map(i => `<div class="ing ${i.staple ? 'staple' : i.have ? '' : 'missing'}">
    <span>${i.staple ? '🧂' : i.have ? '✅' : '🛒'}</span><span class="amt">${esc(i.amount)}</span><span class="nm">${esc(i.name)}</span>
    <span class="muted">${i.staple ? 'basisvare/krydder' : i.have ? (i.daysToExpiry != null && i.daysToExpiry <= 3 ? 'utløper snart!' : 'har hjemme') : 'mangler'}</span></div>`).join('');
  const link = safeUrl(r.url);
  $('#modal-content').innerHTML = `<h2>${esc(r.title)}</h2>
    <p class="muted">${esc(r.source)} · ${r.servings} porsjoner ${link ? `· <a href="${esc(link)}" target="_blank" rel="noopener noreferrer">Original</a>` : ''}</p>
    <h3>Ingredienser</h3>${ing}
    ${r.missing ? `<p><button class="primary" id="to-list">🛒 Legg ${r.missing} manglende i handlelisten</button></p>` : ''}
    <h3>Fremgangsmåte</h3><div class="steps">${esc(r.instructions) || '<span class="muted">Se original oppskrift.</span>'}</div>`;
  $('#modal').hidden = false;
  const b = $('#to-list');
  if (b) b.onclick = async () => { const x = await api('/shopping/from-recipe/' + id, 'POST'); toast(`La til ${x.added} varer`); $('#modal').hidden = true; updateShopBadge(); };
}
$('#modal-close').onclick = () => $('#modal').hidden = true;
$('#modal').onclick = e => { if (e.target.id === 'modal') $('#modal').hidden = true; };

// ---------- Handleliste ----------
async function renderShopping() {
  const items = await api('/shopping');
  view.innerHTML = `<h2>Handleliste</h2>
    <div class="card"><form class="row" id="add-shop"><label>Vare<input name="name" required></label><label>Mengde<input name="amount" style="width:110px"></label><button class="primary">Legg til</button></form></div>
    <div class="toolbar"><button class="small" id="to-pantry">Flytt avkryssede til matvarer</button><button class="small" id="clear-done">Fjern avkryssede</button></div>
    <div class="card">${items.length ? items.map(s => `<div class="item ${s.done ? 'used' : ''}" data-id="${s.id}">
      <input type="checkbox" ${s.done ? 'checked' : ''}><span class="name">${esc(s.name)}</span><span class="muted">${esc(s.amount)}</span>
      ${s.recipeTitle ? `<span class="tag">${esc(s.recipeTitle)}</span>` : ''}<button class="small danger">🗑</button></div>`).join('') : '<p class="muted">Handlelisten er tom. Legg til varer fra en oppskrift.</p>'}</div>`;
  $('#add-shop').onsubmit = async e => { e.preventDefault(); const f = new FormData(e.target); await api('/shopping', 'POST', { name: f.get('name'), amount: f.get('amount') || '' }); renderShopping(); updateShopBadge(); };
  view.querySelectorAll('.item').forEach(row => {
    const id = row.dataset.id;
    row.querySelector('input').onchange = async e => { await api('/shopping/' + id, 'PUT', { done: e.target.checked }); renderShopping(); };
    row.querySelector('button').onclick = async () => { await api('/shopping/' + id, 'DELETE'); renderShopping(); updateShopBadge(); };
  });
  $('#clear-done').onclick = async () => { await api('/shopping?doneOnly=true', 'DELETE'); renderShopping(); updateShopBadge(); };
  $('#to-pantry').onclick = async () => { const r = await api('/shopping/to-pantry', 'POST'); toast(`Flyttet ${r.moved} varer`); renderShopping(); updateShopBadge(); };
}

boot();
