import { Crepe } from '@milkdown/crepe';
import '@milkdown/crepe/theme/common/style.css';
import { editorViewCtx, remarkStringifyOptionsCtx } from '@milkdown/kit/core';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';
import { $prose, replaceAll } from '@milkdown/kit/utils';

import { createChangeNotifier } from './change-notifier.js';
import { createUserEditTracker } from './user-edit.js';
import { createWikiLinkPattern, withWikiLinkTextHandler } from './wiki-link-markdown.js';

import './styles.css';

const EDITOR_DEBOUNCE_MS = 200;
const WIKI_LINK_PATTERN = createWikiLinkPattern();

const appElement = document.getElementById('app');
const rootElement = document.createElement('div');
const hostElement = document.createElement('div');

rootElement.className = 'editor-shell';
hostElement.className = 'editor-host';
rootElement.append(hostElement);
appElement.append(rootElement);

let editor = null;
let editorView = null;
let isApplyingRemoteContent = false;
let isReadOnly = false;
let lastMarkdown = '';
let isReady = false;

const pendingHostMessages = [];

// Whether the person edited the loaded note. The editor changes the document by itself too
// (it appends a trailing paragraph on the first transaction after a note is opened), and the
// host must not save a note that was only opened.
const userEditTracker = createUserEditTracker();

const userEditPlugin = $prose(() => {
  return new Plugin({
    key: new PluginKey('HEIMDALL_USER_EDIT'),
    state: {
      init: () => null,
      apply(tr, value) {
        userEditTracker.observe(tr);
        return value;
      },
    },
  });
});

const menuLabels = {
  bold: 'Bold',
  italic: 'Italic',
  strikethrough: 'Strikethrough',
  inlineCode: 'Inline Code',
  codeBlock: 'Code Block',
  link: 'Link',
  image: 'Image',
  noteLink: 'Note Link [[...]]',
  heading1: 'Heading 1',
  heading2: 'Heading 2',
  heading3: 'Heading 3',
  bulletList: 'Bullet List',
  numberedList: 'Numbered List',
  taskList: 'Task List',
  blockquote: 'Blockquote',
  table: 'Table',
  horizontalRule: 'Horizontal Rule',
};

const wikiLinkPlugin = $prose(() => {
  return new Plugin({
    key: new PluginKey('HEIMDALL_WIKI_LINKS'),
    props: {
      decorations(state) {
        const decorations = [];

        state.doc.descendants((node, pos, parent) => {
          if (!node.isText || !node.text || parent?.type.name === 'code_block') {
            return;
          }

          if (node.marks.some((mark) => mark.type.name === 'link' || mark.type.spec.code)) {
            return;
          }

          WIKI_LINK_PATTERN.lastIndex = 0;

          let match;
          while ((match = WIKI_LINK_PATTERN.exec(node.text)) !== null) {
            const rawMatch = match[0];
            const payload = match[1].trim();
            if (!payload) {
              continue;
            }

            decorations.push(
              Decoration.inline(
                pos + match.index,
                pos + match.index + rawMatch.length,
                {
                  class: 'wiki-link',
                  'data-wiki-link': payload,
                  title: payload,
                },
              ),
            );
          }
        });

        if (decorations.length === 0) {
          return DecorationSet.empty;
        }

        return DecorationSet.create(state.doc, decorations);
      },
      handleClick(_view, _position, event) {
        const target = event.target;
        if (!(target instanceof Element)) {
          return false;
        }

        const wikiLink = target.closest('[data-wiki-link]');
        if (!wikiLink) {
          return false;
        }

        const payload = wikiLink.getAttribute('data-wiki-link');
        if (!payload) {
          return false;
        }

        event.preventDefault();
        event.stopPropagation();

        postToHost({
          type: 'open-link',
          payload,
        });

        return true;
      },
    },
  });
});

function postToHost(message) {
  if (window.chrome?.webview?.postMessage) {
    window.chrome.webview.postMessage(message);
  }
}

const changeNotifier = createChangeNotifier({
  post: postToHost,
  isUserEdited: () => userEditTracker.isEdited(),
  delayMs: EDITOR_DEBOUNCE_MS,
  setTimer: (callback, delayMs) => window.setTimeout(callback, delayMs),
  clearTimer: (timer) => window.clearTimeout(timer),
});

function debounceChange(markdown) {
  lastMarkdown = markdown;
  changeNotifier.notify(markdown);
}

function setTheme(theme) {
  const normalizedTheme = theme === 'dark' ? 'dark' : 'light';
  document.documentElement.dataset.theme = normalizedTheme;
}

function setReadOnly(value) {
  isReadOnly = Boolean(value);
  hostElement.classList.toggle('is-readonly', isReadOnly);
  editor?.setReadonly(isReadOnly);
}

async function setContent(markdown) {
  if (!editor?.editor) {
    return;
  }

  isApplyingRemoteContent = true;

  try {
    editor.editor.action(replaceAll(markdown ?? '', true));
    lastMarkdown = markdown ?? '';
    userEditTracker.reset();
  } finally {
    isApplyingRemoteContent = false;
  }
}

function insertText(text) {
  if (!text || !editorView) {
    return;
  }

  const { state } = editorView;
  const { from, to } = state.selection;
  const transaction = state.tr.insertText(text, from, to);
  editorView.dispatch(transaction.scrollIntoView());
  editorView.focus();
}

function focusEditor() {
  editorView?.focus();
}

// ── Context menu ──────────────────────────────────────────────────

let ctxMenuEl = null;

function wrapSelection(prefix, suffix) {
  if (!editorView) {
    return;
  }

  const { state } = editorView;
  const { from, to } = state.selection;
  const selected = state.doc.textBetween(from, to);
  const replacement = prefix + selected + (suffix ?? '');
  const tr = state.tr.insertText(replacement, from, to);
  editorView.dispatch(tr.scrollIntoView());
  editorView.focus();
}

function insertAtLineStart(prefix) {
  if (!editorView) {
    return;
  }

  const { state } = editorView;
  const { from, to } = state.selection;
  const selected = state.doc.textBetween(from, to);

  if (selected) {
    const lines = selected.split('\n').map((line) => prefix + line);
    const tr = state.tr.insertText(lines.join('\n'), from, to);
    editorView.dispatch(tr.scrollIntoView());
  } else {
    const tr = state.tr.insertText(prefix, from, to);
    editorView.dispatch(tr.scrollIntoView());
  }

  editorView.focus();
}

function getMenuItems() {
  return [
    { label: menuLabels.bold, action: () => wrapSelection('**', '**') },
    { label: menuLabels.italic, action: () => wrapSelection('*', '*') },
    { label: menuLabels.strikethrough, action: () => wrapSelection('~~', '~~') },
    { label: menuLabels.inlineCode, action: () => wrapSelection('`', '`') },
    { separator: true },
    { label: menuLabels.codeBlock, action: () => wrapSelection('```\n', '\n```') },
    { label: menuLabels.blockquote, action: () => insertAtLineStart('> ') },
    { separator: true },
    { label: menuLabels.link, action: () => {
      const { state } = editorView;
      const { from, to } = state.selection;
      const selected = state.doc.textBetween(from, to);
      if (selected) {
        wrapSelection('[', '](url)');
      } else {
        insertText('[text](url)');
      }
    }},
    { label: menuLabels.image, action: () => insertText('![alt](url)') },
    { label: menuLabels.noteLink, action: () => wrapSelection('[[', ']]') },
    { separator: true },
    { label: menuLabels.heading1, action: () => insertAtLineStart('# ') },
    { label: menuLabels.heading2, action: () => insertAtLineStart('## ') },
    { label: menuLabels.heading3, action: () => insertAtLineStart('### ') },
    { separator: true },
    { label: menuLabels.bulletList, action: () => insertAtLineStart('- ') },
    { label: menuLabels.numberedList, action: () => insertAtLineStart('1. ') },
    { label: menuLabels.taskList, action: () => insertAtLineStart('- [ ] ') },
    { separator: true },
    { label: menuLabels.table, action: () => insertText('| H1 | H2 | H3 |\n|---|---|---|\n| a | b | c |\n') },
    { label: menuLabels.horizontalRule, action: () => insertText('\n---\n') },
  ];
}

function showContextMenu(x, y) {
  hideContextMenu();

  ctxMenuEl = document.createElement('div');
  ctxMenuEl.className = 'ctx-menu';
  ctxMenuEl.style.left = `${x}px`;
  ctxMenuEl.style.top = `${y}px`;

  for (const item of getMenuItems()) {
    if (item.separator) {
      const sep = document.createElement('div');
      sep.className = 'ctx-menu-sep';
      ctxMenuEl.append(sep);
      continue;
    }

    const btn = document.createElement('div');
    btn.className = 'ctx-menu-item';
    btn.textContent = item.label;
    btn.addEventListener('mousedown', (e) => {
      e.preventDefault();
      e.stopPropagation();
      hideContextMenu();
      item.action();
    });
    ctxMenuEl.append(btn);
  }

  document.body.append(ctxMenuEl);

  // Clamp to viewport
  const rect = ctxMenuEl.getBoundingClientRect();
  if (rect.right > window.innerWidth) {
    ctxMenuEl.style.left = `${Math.max(0, window.innerWidth - rect.width - 4)}px`;
  }
  if (rect.bottom > window.innerHeight) {
    ctxMenuEl.style.top = `${Math.max(0, window.innerHeight - rect.height - 4)}px`;
  }
}

function hideContextMenu() {
  if (ctxMenuEl) {
    ctxMenuEl.remove();
    ctxMenuEl = null;
  }
}

document.addEventListener('mousedown', (e) => {
  if (ctxMenuEl && !ctxMenuEl.contains(e.target)) {
    hideContextMenu();
  }
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    hideContextMenu();
  }
});

hostElement.addEventListener('contextmenu', (e) => {
  if (isReadOnly) {
    return;
  }

  e.preventDefault();
  e.stopPropagation();
  showContextMenu(e.clientX, e.clientY);
});

function handleHostMessage(event) {
  const message = event?.data ?? event;
  if (!message || typeof message !== 'object') {
    return;
  }

  if (!isReady && message.type !== 'set-theme') {
    pendingHostMessages.push(message);
    return;
  }

  switch (message.type) {
    case 'set-content':
      void setContent(String(message.payload ?? ''));
      break;
    case 'set-theme':
      setTheme(message.payload);
      break;
    case 'set-readonly':
      setReadOnly(message.payload);
      break;
    case 'focus':
      focusEditor();
      break;
    case 'insert':
      insertText(String(message.payload ?? ''));
      break;
    case 'set-menu-labels':
      if (message.payload && typeof message.payload === 'object') {
        Object.assign(menuLabels, message.payload);
      }
      break;
    default:
      break;
  }
}

function flushPendingMessages() {
  while (pendingHostMessages.length > 0) {
    handleHostMessage(pendingHostMessages.shift());
  }
}

async function createEditor() {
  if (window.chrome?.webview?.addEventListener) {
    window.chrome.webview.addEventListener('message', handleHostMessage);
  } else {
    window.addEventListener('message', handleHostMessage);
  }

  editor = new Crepe({
    root: hostElement,
    defaultValue: '',
  });

  editor.editor
    .config((ctx) => {
      ctx.update(remarkStringifyOptionsCtx, withWikiLinkTextHandler);
    })
    .use(userEditPlugin)
    .use(wikiLinkPlugin);

  editor.on((listener) => {
    listener.mounted((ctx) => {
      editorView = ctx.get(editorViewCtx);
    });

    listener.markdownUpdated((_ctx, markdown) => {
        if (isApplyingRemoteContent) {
          return;
        }

        debounceChange(markdown);
    });
  });

  await editor.create();

  setTheme('light');
  setReadOnly(false);
  isReady = true;
  flushPendingMessages();

  postToHost({ type: 'ready' });
}

void createEditor();
