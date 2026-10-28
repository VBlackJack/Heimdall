/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { CHANGE_MESSAGE_TYPE, createChangeNotifier } from '../src/change-notifier.js';
import { APPENDED_TRANSACTION_META, createUserEditTracker } from '../src/user-edit.js';

const DELAY_MS = 200;

// A hand-driven timer: a pending callback runs only when the test fires it.
function createHarness(isUserEdited) {
  const posted = [];
  let pending = null;
  let nextHandle = 1;
  let activeHandle = 0;

  const notifier = createChangeNotifier({
    post: (message) => posted.push(message),
    isUserEdited,
    delayMs: DELAY_MS,
    setTimer: (callback, delayMs) => {
      assert.equal(delayMs, DELAY_MS);
      pending = callback;
      activeHandle = nextHandle++;
      return activeHandle;
    },
    clearTimer: (handle) => {
      if (handle === activeHandle) {
        pending = null;
      }
    },
  });

  return {
    notifier,
    posted,
    fire() {
      const callback = pending;
      pending = null;
      callback?.();
    },
  };
}

describe('createChangeNotifier', () => {
  it('reports a change the person did not make as not dirty', () => {
    const harness = createHarness(() => false);

    harness.notifier.notify('- item\n\n');
    harness.fire();

    assert.deepEqual(harness.posted, [
      { type: CHANGE_MESSAGE_TYPE, payload: { markdown: '- item\n\n', dirty: false } },
    ]);
  });

  it('reports an edit by the person as dirty', () => {
    const harness = createHarness(() => true);

    harness.notifier.notify('hello\n');
    harness.fire();

    assert.deepEqual(harness.posted, [
      { type: CHANGE_MESSAGE_TYPE, payload: { markdown: 'hello\n', dirty: true } },
    ]);
  });

  it('posts nothing before the timer fires, and only the last markdown after it', () => {
    const harness = createHarness(() => true);

    harness.notifier.notify('a');
    harness.notifier.notify('ab');
    assert.deepEqual(harness.posted, []);

    harness.fire();

    assert.equal(harness.posted.length, 1);
    assert.equal(harness.posted[0].payload.markdown, 'ab');
  });

  it('keeps an edit dirty when the flag is reset before the timer fires', () => {
    let edited = true;
    const harness = createHarness(() => edited);

    harness.notifier.notify('typed\n');
    edited = false;
    harness.fire();

    assert.equal(harness.posted[0].payload.dirty, true);
  });
});

describe('a note opened and left alone', () => {
  it('is reported clean when the editor appends its trailing paragraph', () => {
    const tracker = createUserEditTracker();
    const harness = createHarness(() => tracker.isEdited());

    // The note was loaded: the host's set-content resets the flag.
    tracker.reset();
    // The first transaction is a selection; the trailing plugin appends a paragraph to it.
    tracker.observe({ docChanged: false, getMeta: () => undefined });
    tracker.observe({
      docChanged: true,
      getMeta: (name) => (name === APPENDED_TRANSACTION_META ? {} : undefined),
    });
    harness.notifier.notify('- a\n- b\n\n');
    harness.fire();

    assert.equal(harness.posted[0].payload.dirty, false);
  });

  it('is reported dirty once the person types', () => {
    const tracker = createUserEditTracker();
    const harness = createHarness(() => tracker.isEdited());

    tracker.reset();
    tracker.observe({ docChanged: true, getMeta: () => undefined });
    harness.notifier.notify('- a\n- bc\n');
    harness.fire();

    assert.equal(harness.posted[0].payload.dirty, true);
  });
});
