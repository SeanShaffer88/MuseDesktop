// Execute the actual shipped C# raw-string script. No third-party DOM package.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source = fs.readFileSync('src/MuseDesktop.Core/ApprovalAutoAccept.cs', 'utf8');
const extract = name => {
  const m = source.match(new RegExp(`public const string ${name} = """\\n([\\s\\S]*?)\\n        """;`));
  assert.ok(m, `missing shipped ${name}`);
  return m[1].split('\n').map(l => l.replace(/^        /, '')).join('\n');
};
const script = extract('Script'), reset = extract('ResetPendingScript');
let count = 0;
class Element {
  constructor(tag, text = '', attrs = {}) {
    this.tagName = tag.toUpperCase(); this.text = text; this.attrs = attrs;
    this.children = []; this.parentElement = null; this.disabled = false; this.hidden = false;
    this.style = {}; this.clicks = 0; this.rect = {x: 0, y: 0, width: 900, height: 700};
  }
  add(...items) { for (const e of items) { e.parentElement = this; this.children.push(e); } return this; }
  remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(e => e !== this); this.parentElement = null; }
  get textContent() { return this.text + this.children.map(e => e.textContent).join(' '); }
  get innerText() { return this.textContent; }
  get isConnected() { let p = this; while (p.parentElement) p = p.parentElement; return p.tagName === 'HTML'; }
  getAttribute(k) { return this.attrs[k] ?? null; }
  hasAttribute(k) { return Object.hasOwn(this.attrs, k); }
  contains(e) { return !!e && (e === this || this.children.some(c => c.contains(e))); }
  matches(s) {
    s = s.trim();
    if (s === 'input:not([type="hidden"])') return this.tagName === 'INPUT' && this.attrs.type !== 'hidden';
    const attr = s.match(/^\[([^=\]]+)(?:="([^"]*)")?\]$/);
    if (attr) return attr[2] === undefined ? this.hasAttribute(attr[1]) : this.attrs[attr[1]] === attr[2];
    return this.tagName.toLowerCase() === s;
  }
  closest(selectors) { for (let p = this; p; p = p.parentElement) if (selectors.split(',').some(s => p.matches(s))) return p; return null; }
  descendants() { return this.children.flatMap(e => [e, ...e.descendants()]); }
  querySelectorAll(selectors) { return this.descendants().filter(e => selectors.split(',').some(s => e.matches(s))); }
  querySelector(selectors) { return this.querySelectorAll(selectors)[0] ?? null; }
  getBoundingClientRect() { return this.rect; }
  click() { this.clicks++; this.onClick?.(); }
}
function fixture(url = 'https://muse.ai/chat/test') {
  const root = new Element('html'), body = new Element('body'); root.add(body);
  const context = {
    URL, location: {href: url}, performance: {now: () => context.time}, time: 0,
    window: {innerWidth: 1000, innerHeight: 800},
    getComputedStyle: e => ({display: 'block', visibility: 'visible', opacity: '1', pointerEvents: 'auto', ...e.style}),
    document: {
      body, documentElement: root, visibilityState: 'visible',
      querySelectorAll: s => root.querySelectorAll(s),
      elementFromPoint: (x, y) => [root, ...root.descendants()].reverse().find(e => {
        const r = e.rect;
        return e.isConnected && !e.hidden && x >= r.x && y >= r.y && x < r.x+r.width && y < r.y+r.height;
      }) ?? null,
    },
  };
  vm.createContext(context);
  const run = (advance = 0) => { context.time += advance; return vm.runInContext(script, context, {timeout: 1000}); };
  return {body, context, run, reset: () => vm.runInContext(reset, context)};
}
function card(f, text = 'Allow once', {marker = 'approval-card', title = 'Approval required', y = 10} = {}) {
  const host = new Element('section', '', marker ? {'data-testid': marker} : {});
  host.rect = {x:10,y,width:600,height:180};
  const heading = new Element('h3', title); heading.rect = {x:20,y:y+10,width:400,height:30};
  const yes = new Element('button', text); yes.rect = {x:20,y:y+100,width:120,height:30};
  const no = new Element('button', 'Deny'); no.rect = {x:180,y:y+100,width:120,height:30};
  host.add(heading, yes, no); f.body.add(host);
  return {host, yes, no};
}
function test(name, fn) { fn(); count++; }
test('one-time card settles, clicks once, never clicks deny', () => {
  const f = fixture(), c = card(f);
  assert.equal(f.run(), 'pending'); assert.equal(c.yes.clicks, 0);
  assert.equal(f.run(1000), 'pending'); assert.equal(f.run(600), 'clicked');
  assert.equal(c.yes.clicks, 1); assert.equal(c.no.clicks, 0);
  assert.equal(f.run(2000), 'duplicate');
});
for (const text of ['Allow once','Approve','Approve once','Approve this request','Allow this time','Permitir una vez','Aprobar']) {
  test(`recognized single-use label ${text}`, () => {
    const f=fixture(),c=card(f,text); f.run(); assert.equal(f.run(2000),'clicked'); assert.equal(c.yes.clicks,1);
  });
}
for (const text of ['Always allow','Allow always','Always approve','Permitir siempre','Yes','Allow','Allow all','Approve everything']) {
  test(`ambiguous or persistent label remains manual: ${text}`, () => {
    const f=fixture(),c=card(f,text); f.run(); f.run(3000); assert.equal(c.yes.clicks,0);
  });
}
test('bare Allow only beside a separate permanent choice', () => {
  const f=fixture(),c=card(f,'Allow'),always=new Element('button','Always allow');
  always.rect={x:350,y:110,width:120,height:30}; c.host.add(always);
  f.run(); assert.equal(f.run(2000),'clicked'); assert.equal(always.clicks,0);
});
for (const mutate of [
  (f,c)=>c.yes.disabled=true,
  (f,c)=>c.yes.attrs['aria-disabled']='true',
  (f,c)=>c.host.attrs.inert='',
  (f,c)=>c.host.attrs['aria-hidden']='true',
  (f,c)=>c.host.style.opacity='0',
  (f,c)=>f.body.style.opacity='0',
  (f,c)=>c.host.style.display='none',
  (f,c)=>c.host.style.visibility='hidden',
  (f,c)=>c.yes.style.pointerEvents='none',
  (f,c)=>c.host.add(new Element('input','',{type:'checkbox'})),
  (f,c)=>c.host.add(new Element('input','',{type:'password'})),
  (f,c)=>c.host.add(new Element('textarea')),
  (f,c)=>c.no.remove(),
  (f,c)=>c.host.add(new Element('button','Approve once')),
  (f,c)=>{const quote=new Element('blockquote');c.host.remove();quote.add(c.host);f.body.add(quote);},
  (f,c)=>{c.host.attrs['data-testid']='message';},
  (f,c)=>{c.host.attrs['data-testid']='cookie-dialog';c.host.children[0].text='Cookie preferences';},
  (f,c)=>{f.context.document.visibilityState='hidden';},
  (f,c)=>{c.yes.rect.y=1000;},
  (f,c)=>{const cover=new Element('div');f.body.add(cover);},
]) {
  test('hidden/disabled/form/quoted/ambiguous/covered requests do not click', () => {
    const f=fixture(),c=card(f); mutate(f,c);f.run();f.run(3000);assert.equal(c.yes.clicks,0);
  });
}
for (const url of ['http://muse.ai/chat/test','https://evil.test/chat/test',
  'https://muse.ai:8443/chat/test','https://me:secret@muse.ai/chat/test','https://muse.ai/auth/callback']) {
  test('untrusted origin or non-chat route blocks', () => {
    const f=fixture(url),c=card(f);assert.equal(f.run(),'blocked');assert.equal(c.yes.clicks,0);
  });
}
test('multiple cards are ambiguous, never pick an arbitrary first one', () => {
  const f=fixture(),a=card(f),b=card(f,'Approve once',{y:250});
  assert.equal(f.run(),'ambiguous');f.run(2000);assert.equal(a.yes.clicks+b.yes.clicks,0);
});
test('changed contents restart settling', () => {
  const f=fixture(),c=card(f);f.run();c.host.text='Different request';
  assert.equal(f.run(2000),'pending');assert.equal(c.yes.clicks,0);assert.equal(f.run(2000),'clicked');
});
test('disabling or locking clears a pending attempt, not deduplication', () => {
  const f=fixture(),c=card(f);f.run();f.reset();assert.equal(f.run(5000),'pending');
  assert.equal(c.yes.clicks,0);assert.equal(f.run(2000),'clicked');f.reset();
  assert.equal(f.run(2000),'duplicate');
});
test('disabled/re-enabled and re-rendered cards are not retried', () => {
  const f=fixture(),c=card(f);f.run();f.run(2000);
  c.yes.disabled=true;f.run(2000);c.yes.disabled=false;assert.equal(f.run(2000),'duplicate');
  c.host.remove();const replacement=card(f);assert.equal(f.run(2000),'duplicate');assert.equal(replacement.yes.clicks,0);
});
test('distinct next request clicks; route change requires a fresh settle', () => {
  const f=fixture(),c=card(f);f.run();f.run(2000);c.host.remove();
  const next=card(f,'Approve once');next.host.text='Read another item';
  assert.equal(f.run(2000),'pending');assert.equal(f.run(2000),'clicked');
  f.context.location.href='https://muse.ai/chat/next';
  assert.equal(f.run(2000),'duplicate');assert.equal(next.yes.clicks,1);
});
test('throwing click is marked once and never retried', () => {
  const f=fixture(),c=card(f);c.yes.onClick=()=>{throw Error('provider failed');};
  f.run();assert.equal(f.run(2000),'unrecognised');assert.equal(f.run(2000),'duplicate');
});
console.log(`Auto-approval source-derived DOM fixtures passed: ${count} cases; no dependencies.`);
