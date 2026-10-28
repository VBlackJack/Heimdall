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

// A [[note link]] holds no bracket and no line break: the same class the host's readers and
// the editor's link decorations use.
export const WIKI_LINK_SOURCE = '\\[\\[([^[\\]\\r\\n]+?)\\]\\]';

const WIKI_LINK_OPEN = '[';
const WIKI_LINK_CLOSE = ']';

/** A fresh global pattern, so that no caller shares a lastIndex with another. */
export function createWikiLinkPattern() {
  return new RegExp(WIKI_LINK_SOURCE, 'g');
}

/**
 * Wraps the text handler of mdast-util-to-markdown so that a [[note link]] in a text node is
 * written with its brackets as typed. The default handler escapes every '[' in text, so the
 * link used to be saved as \[\[X]], which no reader of the notes matches.
 *
 * Only the text node's own value is looked at: code spans and code blocks are other node
 * types and never reach this handler. The text around a link, and the name inside it, go
 * through the original handler with the neighbouring character told to it ('[' after, ']'
 * before), so a backslash typed right before a link stays a literal backslash instead of
 * escaping the link's first bracket, and a name holding markup (such as *a*) is still
 * escaped, which keeps the saved note reading back as the same text.
 */
export function wrapTextHandlerForWikiLinks(textHandler) {
  return function wikiLinkAwareText(node, parent, state, info) {
    const value = String(node.value ?? '');
    const links = [...value.matchAll(createWikiLinkPattern())];

    if (links.length === 0) {
      return textHandler(node, parent, state, info);
    }

    const emit = (text, before, after) => {
      if (text.length === 0) {
        return '';
      }

      return textHandler({ ...node, value: text }, parent, state, { ...info, before, after });
    };

    let output = '';
    let cursor = 0;
    let before = info.before;

    for (const link of links) {
      output += emit(value.slice(cursor, link.index), before, WIKI_LINK_OPEN);
      output += WIKI_LINK_OPEN + WIKI_LINK_OPEN + emit(link[1], WIKI_LINK_OPEN, WIKI_LINK_CLOSE) + WIKI_LINK_CLOSE + WIKI_LINK_CLOSE;
      cursor = link.index + link[0].length;
      before = WIKI_LINK_CLOSE;
    }

    output += emit(value.slice(cursor), before, info.after);

    return output;
  };
}

/**
 * Applies wrapTextHandlerForWikiLinks to remark-stringify options (what Milkdown keeps in
 * its remarkStringifyOptions slice), keeping every other option and handler.
 */
export function withWikiLinkTextHandler(options) {
  const current = options ?? {};
  const handlers = current.handlers ?? {};
  const textHandler = handlers.text;

  if (typeof textHandler !== 'function') {
    return current;
  }

  return {
    ...current,
    handlers: {
      ...handlers,
      text: wrapTextHandlerForWikiLinks(textHandler),
    },
  };
}
