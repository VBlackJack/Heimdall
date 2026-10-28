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

import {
  ADD_TO_HISTORY_META,
  APPENDED_TRANSACTION_META,
  createUserEditTracker,
  isUserEditTransaction,
} from '../src/user-edit.js';

// A ProseMirror transaction reduced to what the predicate reads.
function transaction({ docChanged = false, meta = {} } = {}) {
  return {
    docChanged,
    getMeta: (name) => meta[name],
  };
}

const typedCharacter = () => transaction({ docChanged: true });
const selectionOnly = () => transaction({ docChanged: false });
const trailingParagraph = () => transaction({
  docChanged: true,
  meta: { [APPENDED_TRANSACTION_META]: selectionOnly() },
});

describe('isUserEditTransaction', () => {
  it('accepts a transaction that changed the document', () => {
    assert.equal(isUserEditTransaction(typedCharacter()), true);
  });

  it('refuses a selection-only transaction', () => {
    assert.equal(isUserEditTransaction(selectionOnly()), false);
  });

  it('refuses a transaction a plugin appended, such as the trailing paragraph', () => {
    assert.equal(isUserEditTransaction(trailingParagraph()), false);
  });

  it('refuses a change flagged as kept out of the history', () => {
    const tr = transaction({ docChanged: true, meta: { [ADD_TO_HISTORY_META]: false } });

    assert.equal(isUserEditTransaction(tr), false);
  });

  it('accepts a change that sets addToHistory to true', () => {
    const tr = transaction({ docChanged: true, meta: { [ADD_TO_HISTORY_META]: true } });

    assert.equal(isUserEditTransaction(tr), true);
  });

  it('refuses a missing transaction', () => {
    assert.equal(isUserEditTransaction(null), false);
  });
});

describe('createUserEditTracker', () => {
  it('starts unedited', () => {
    assert.equal(createUserEditTracker().isEdited(), false);
  });

  it('is edited after a typed character', () => {
    const tracker = createUserEditTracker();

    tracker.observe(typedCharacter());

    assert.equal(tracker.isEdited(), true);
  });

  it('stays unedited when the editor only selects and appends its trailing paragraph', () => {
    const tracker = createUserEditTracker();

    tracker.observe(selectionOnly());
    tracker.observe(trailingParagraph());

    assert.equal(tracker.isEdited(), false);
  });

  it('stays edited once an edit was seen, even through later non-edits', () => {
    const tracker = createUserEditTracker();

    tracker.observe(typedCharacter());
    tracker.observe(selectionOnly());
    tracker.observe(trailingParagraph());

    assert.equal(tracker.isEdited(), true);
  });

  it('is unedited again after a reset, which a note load performs', () => {
    const tracker = createUserEditTracker();
    tracker.observe(typedCharacter());

    tracker.reset();

    assert.equal(tracker.isEdited(), false);
  });
});
