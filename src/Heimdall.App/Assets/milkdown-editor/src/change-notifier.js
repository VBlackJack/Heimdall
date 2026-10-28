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

export const CHANGE_MESSAGE_TYPE = 'change';

/**
 * Debounced posting of the editor's markdown to the host. The message says whether the
 * person edited the note ('dirty'): the host saves only a note that was edited, so a change
 * the editor made by itself (the trailing paragraph it appends on open) is reported but not
 * written to the file.
 *
 * `isUserEdited` is read when the change arrives, not when the timer fires, so a note
 * switch inside the debounce window does not turn an edit into a clean change.
 */
export function createChangeNotifier({ post, isUserEdited, delayMs, setTimer, clearTimer }) {
  let timer = 0;

  return {
    notify(markdown) {
      const dirty = Boolean(isUserEdited());
      clearTimer(timer);
      timer = setTimer(() => {
        post({
          type: CHANGE_MESSAGE_TYPE,
          payload: {
            markdown,
            dirty,
          },
        });
      }, delayMs);
    },
  };
}
