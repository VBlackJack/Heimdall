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

import { fromMarkdown } from 'mdast-util-from-markdown';
import { defaultHandlers, toMarkdown } from 'mdast-util-to-markdown';

import {
  createWikiLinkPattern,
  withWikiLinkTextHandler,
  wrapTextHandlerForWikiLinks,
} from '../src/wiki-link-markdown.js';

// The serializer Milkdown builds: the default handlers, with the wiki link text handler
// installed the way the editor installs it, through the stringify options.
const wikiAwareOptions = withWikiLinkTextHandler({ handlers: { text: defaultHandlers.text } });
const plainOptions = { handlers: { text: defaultHandlers.text } };

const text = (value) => ({ type: 'text', value });
const paragraph = (...children) => ({ type: 'paragraph', children });
const root = (...children) => ({ type: 'root', children });

const serialize = (value, options = wikiAwareOptions) =>
  toMarkdown(root(paragraph(text(value))), options);

const textOf = (node) => (node.children ? node.children.map(textOf).join('') : node.value ?? '');

// What a person reads back after the note is saved and opened again.
const roundTrip = (value) => textOf(fromMarkdown(serialize(value)));

describe('a note link in text', () => {
  it('is written with its brackets unescaped', () => {
    assert.equal(serialize('[[X]]'), '[[X]]\n');
  });

  it('is written unescaped inside a sentence, with its name unchanged', () => {
    assert.equal(serialize('see [[Procedure reseau]] now'), 'see [[Procedure reseau]] now\n');
  });

  it('is written unescaped for each link of a text node', () => {
    assert.equal(serialize('[[A]] and [[B_c]]'), '[[A]] and [[B_c]]\n');
  });

  it('was escaped by the default handler, which is what the wrapper replaces', () => {
    assert.equal(serialize('[[X]]', plainOptions), '\\[\\[X]]\n');
  });

  it('reads back as the same text', () => {
    for (const value of ['[[X]]', 'a [[X Y]] b', '[[A]] [[B]]', '[[X]]]']) {
      assert.equal(roundTrip(value), value);
    }
  });
});

describe('text that is not a note link', () => {
  it('keeps a single bracket escaped as before', () => {
    assert.equal(serialize('plain [text] [x]'), 'plain \\[text] \\[x]\n');
  });

  it('does not take a closing bracket as the end of a name', () => {
    assert.equal(serialize('[[a]b]]'), '\\[\\[a]b]]\n');
  });

  it('does not take an opening bracket as part of a name', () => {
    assert.equal(serialize('[[a [b]]'), '\\[\\[a \\[b]]\n');
  });

  it('does not cross a line break', () => {
    assert.equal(serialize('[[a\nb]]'), '\\[\\[a\nb]]\n');
  });

  it('keeps a typed backslash and the brackets after it as typed text', () => {
    const literal = '\\[\\[X]]';

    assert.equal(serialize(literal), '\\\\\\[\\\\\\[X]]\n');
    assert.equal(roundTrip(literal), literal);
  });
});

describe('a backslash typed before a note link', () => {
  it('stays a literal backslash and does not escape the link', () => {
    assert.equal(serialize('x\\[[X]]'), 'x\\\\[[X]]\n');
    assert.equal(roundTrip('x\\[[X]]'), 'x\\[[X]]');
  });

  it('stays a literal backslash when a letter follows the text node', () => {
    // The text before the link is told the link follows ('['), not what follows the node.
    const tree = root(paragraph(text('x\\[[X]]'), text('z')));

    assert.equal(toMarkdown(tree, wikiAwareOptions), 'x\\\\[[X]]z\n');
    assert.equal(textOf(fromMarkdown(toMarkdown(tree, wikiAwareOptions))), 'x\\[[X]]z');
  });
});

describe('a note link whose name holds markup', () => {
  it('keeps the markup escaped so the note reads back as the same text', () => {
    assert.equal(serialize('[[a*b*c]]'), '[[a\\*b\\*c]]\n');
    assert.equal(roundTrip('[[a*b*c]]'), '[[a*b*c]]');
  });
});

describe('code', () => {
  it('keeps a note link in an inline code span as it is', () => {
    const tree = root(paragraph({ type: 'inlineCode', value: '[[X]]' }, text(' [[Y]]')));

    assert.equal(toMarkdown(tree, wikiAwareOptions), '`[[X]]` [[Y]]\n');
  });

  it('keeps a note link in a code block as it is', () => {
    const tree = root({ type: 'code', lang: null, value: '[[X]]' });

    assert.equal(toMarkdown(tree, wikiAwareOptions), '```\n[[X]]\n```\n');
  });
});

describe('withWikiLinkTextHandler', () => {
  it('keeps the other options and handlers', () => {
    const strong = () => 'strong';
    const options = withWikiLinkTextHandler({
      encode: [],
      handlers: { text: defaultHandlers.text, strong },
    });

    assert.deepEqual(options.encode, []);
    assert.equal(options.handlers.strong, strong);
    assert.notEqual(options.handlers.text, defaultHandlers.text);
  });

  it('hands the text around a link to the handler it wraps, naming the link as the neighbour', () => {
    const seen = [];
    const recording = (node, _parent, _state, info) => {
      seen.push({ value: node.value, before: info.before, after: info.after });
      return node.value;
    };

    wrapTextHandlerForWikiLinks(recording)(text('a [[X]] b'), null, {}, { before: '^', after: '$' });

    assert.deepEqual(seen, [
      { value: 'a ', before: '^', after: '[' },
      { value: 'X', before: '[', after: ']' },
      { value: ' b', before: ']', after: '$' },
    ]);
  });

  it('leaves the options alone when there is no text handler to wrap', () => {
    const options = { encode: [] };

    assert.equal(withWikiLinkTextHandler(options), options);
  });
});

describe('createWikiLinkPattern', () => {
  it('returns a fresh global pattern each time', () => {
    const first = createWikiLinkPattern();
    const second = createWikiLinkPattern();

    assert.notEqual(first, second);
    assert.equal(first.global, true);
    assert.deepEqual('[[A]] [[B]]'.match(first), ['[[A]]', '[[B]]']);
  });
});
