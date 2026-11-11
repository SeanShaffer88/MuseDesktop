// Execute source-derived helper scripts against controlled DOM fixtures.
// This proves helper behavior, not the live muse.ai DOM contract.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/MuseDesktop.Core/DualWorkbench.cs', import.meta.url), 'utf8');
function constant(name) {
  const match = source.match(new RegExp(`public const string ${name} = """([\\s\\S]*?)""";`));
  assert.ok(match, `Missing script ${name}`);
  return match[1];
}
const insertParts = source.match(/return """([\s\S]*?)""" \+ safe \+ """([\s\S]*?)""";/);
assert.ok(insertParts, 'Missing insertion script');
const insert = text => insertParts[1] + JSON.stringify(text.slice(0, 8000)) + insertParts[2];
const applyParts = source.match(/public static string ApplyScroll\(double ratio\) => """([\s\S]*?)""" \+ ScrollNumber\(ratio\) \+ """([\s\S]*?)""";/);
assert.ok(applyParts, 'Missing scroll application script');
const applyScroll = ratio => applyParts[1] + Math.max(0, Math.min(1, Number.isFinite(ratio) ? ratio : 0)) + applyParts[2];

const run = (script, context) => vm.runInNewContext(script, context, {timeout: 1000});
assert.equal(run(constant('Selection'), {window: {getSelection: () => 'a'.repeat(9000)}}).length, 8000);

class TextArea {
  constructor() { this.text = 'existing draft'; this.events = []; this.tagName = 'TEXTAREA'; }
  get value() { return this.text; }
  set value(v) { this.text = v; }
  getClientRects() { return [1]; }
  focus() { this.focused = true; }
  dispatchEvent(e) { this.events.push(e.type); }
}
const box = new TextArea();
const hostile = '"; globalThis.accidentalSend = true; // <script>\n\\';
const context = {
  document: {querySelectorAll: () => [box]},
  HTMLTextAreaElement: TextArea,
  Event: class { constructor(type) { this.type = type; } },
};
assert.equal(run(insert(hostile), context), true);
assert.equal(box.value, 'existing draft\n' + hostile);
assert.deepEqual(box.events, ['input']);
assert.equal(context.accidentalSend, undefined);
assert.equal(box.focused, true);

const untouched = new TextArea();
assert.equal(run(insert('new text'), {...context, document: {querySelectorAll: () => [untouched, new TextArea()]}}), false);
assert.equal(untouched.value, 'existing draft');
untouched.readOnly = true;
assert.equal(run(insert('new text'), {...context, document: {querySelectorAll: () => [untouched]}}), false);
assert.equal(untouched.value, 'existing draft');
assert.equal(run(insert('new text'), {...context, document: {querySelectorAll: () => []}}), false);

let visible = true;
const streamingContext = {document: {querySelectorAll: () => [{getClientRects: () => visible ? [1] : []}]}};
assert.equal(run(constant('Streaming'), streamingContext), true);
visible = false;
assert.equal(run(constant('Streaming'), streamingContext), false);

let listener;
let clock = 1000;
const messages = [];
const scroller = {tagName: 'DIV', scrollHeight: 500, clientHeight: 100, scrollTop: 100};
const scrollContext = {
  window: {chrome: {webview: {postMessage: message => messages.push(message)}}},
  performance: {now: () => clock},
  document: {
    scrollingElement: scroller,
    querySelectorAll: () => [],
    addEventListener: (kind, fn) => { assert.equal(kind, 'scroll'); listener = fn; },
    removeEventListener: (kind, fn) => { assert.equal(kind, 'scroll'); if (listener === fn) listener = null; },
  },
};
assert.equal(run(constant('StartScroll'), scrollContext), true);
listener({target: scroller});
assert.equal(messages.length, 1);
assert.equal(messages[0].ratio, 0.25);
assert.equal(messages[0].type, 'musedesktop-dual-scroll');
clock += 200;
listener({target: {...scroller, tagName: 'TEXTAREA'}});
assert.equal(messages.length, 1, 'Must not synchronize composer scrolling');
clock += 200;
assert.equal(run(applyScroll(0.75), scrollContext), true);
assert.equal(scroller.scrollTop, 300);
listener({target: scroller});
assert.equal(messages.length, 1, 'Applied scrolling must not create a feedback loop');
clock += 400;
listener({target: scroller});
assert.equal(messages.length, 2);
run('window.__mdDualStopSync?.();', scrollContext);
assert.equal(listener, null);
assert.equal(run(applyScroll(NaN), scrollContext), true);
assert.equal(scroller.scrollTop, 0);

console.log('Dual helper fixtures passed: bounded selection, escaped unsent append, ambiguous/read-only refusal, streaming recognition, scroll bounds and loop suppression.');
