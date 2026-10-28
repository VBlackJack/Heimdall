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

// Transaction meta names. 'appendedTransaction' is set by ProseMirror on a transaction a
// plugin appended (the trailing-paragraph plugin appends one on the first transaction after
// a note is opened, even a selection-only one); 'addToHistory' is set to false by plugins
// for changes that are not the user's.
export const APPENDED_TRANSACTION_META = 'appendedTransaction';
export const ADD_TO_HISTORY_META = 'addToHistory';

/**
 * Whether a ProseMirror transaction is an edit made by the person typing: it changed the
 * document, no plugin appended it, and it was not flagged as kept out of the undo history.
 */
export function isUserEditTransaction(tr) {
  if (!tr || !tr.docChanged) {
    return false;
  }

  if (tr.getMeta(APPENDED_TRANSACTION_META)) {
    return false;
  }

  return tr.getMeta(ADD_TO_HISTORY_META) !== false;
}

/**
 * Remembers whether the loaded note was edited by the person since it was loaded. The flag
 * is raised by the first user edit and kept until the next load resets it.
 */
export function createUserEditTracker() {
  let edited = false;

  return {
    observe(tr) {
      if (isUserEditTransaction(tr)) {
        edited = true;
      }
    },
    reset() {
      edited = false;
    },
    isEdited() {
      return edited;
    },
  };
}
