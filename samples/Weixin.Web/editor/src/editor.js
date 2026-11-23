import { Editor, editorViewCtx, editorViewOptionsCtx, parserCtx, remarkStringifyOptionsCtx, rootCtx } from '@milkdown/kit/core';
import { history, redoCommand, undoCommand } from '@milkdown/kit/plugin/history';
import {
  commonmark, createCodeBlockCommand, insertHrCommand, insertImageInputRule,
  toggleEmphasisCommand, toggleStrongCommand, turnIntoTextCommand, wrapInHeadingCommand
} from '@milkdown/kit/preset/commonmark';
import { redoDepth, undoDepth } from '@milkdown/kit/prose/history';
import { Slice } from '@milkdown/kit/prose/model';
import { Plugin } from '@milkdown/kit/prose/state';
import { $prose, $remark, callCommand, getMarkdown, replaceAll } from '@milkdown/kit/utils';
import 'prosemirror-view/style/prosemirror.css';

const commands = {
  bold: toggleStrongCommand, italic: toggleEmphasisCommand, rule: insertHrCommand,
  undo: undoCommand, redo: redoCommand
};

function linkUrl(value) {
  try {
    const url = new URL(value);
    return (url.protocol === 'http:' || url.protocol === 'https:') && !url.username && !url.password ? url.href : null;
  } catch {
    return null;
  }
}

const safeMarkdown = $remark('weixinMessage', () => () => {
  function sanitize(node) {
    if (!node.children) return;
    node.children = node.children.flatMap(child => {
      sanitize(child);
      if (child.type === 'link' && !linkUrl(child.url)) return child.children ?? [];
      if (child.type === 'definition' && !linkUrl(child.url)) return [];
      if (child.type === 'image' || child.type === 'imageReference') {
        return { type: 'text', value: child.alt || '图片' };
      }
      if (child.type !== 'html') return child;
      const text = { type: 'text', value: child.value };
      return ['root', 'blockquote', 'listItem'].includes(node.type) ? { type: 'paragraph', children: [text] } : text;
    });
  }
  return sanitize;
});

export async function createEditor(root, onChange) {
  const editor = await Editor.make()
    .config(ctx => {
      ctx.set(rootCtx, root);
      ctx.set(remarkStringifyOptionsCtx, { fences: true, strong: '*', emphasis: '*', resourceLink: true, rule: '-' });
      ctx.update(editorViewOptionsCtx, options => ({
        ...options,
        editable: () => false,
        attributes: { role: 'textbox', 'aria-label': '消息内容', 'aria-multiline': 'true', 'aria-disabled': 'true' },
        handleDOMEvents: {
          paste(view, event) {
            const text = event.clipboardData?.getData('text/plain');
            event.preventDefault();
            if (text) {
              const { selection, tr } = view.state;
              if (selection.$from.parent.type.spec.code || !text.trim()) {
                view.dispatch(tr.insertText(text.replace(/\r\n?/g, '\n')));
              } else {
                const doc = ctx.get(parserCtx)(text);
                if (!doc) return true;
                const paragraph = doc.childCount === 1 && doc.firstChild.type.name === 'paragraph';
                const child = paragraph ? doc.firstChild.firstChild : null;
                if (paragraph && doc.firstChild.childCount === 1 && child.isText && !child.marks.length) {
                  if (!/[\r\n]/.test(text) && child.text === text.trim()) view.dispatch(tr.insertText(text).scrollIntoView());
                  else view.dispatch(tr.replaceSelectionWith(child).scrollIntoView());
                } else {
                  const depth = paragraph ? 1 : 0;
                  view.dispatch(tr.replaceSelection(new Slice(doc.content, depth, depth)).scrollIntoView());
                }
              }
            }
            return true;
          },
          drop(view, event) {
            if (view.dragging) return false;
            event.preventDefault();
            return true;
          },
          click(_view, event) {
            if (event.target.closest?.('a')) event.preventDefault();
            return false;
          }
        }
      }));
    })
    .use(commonmark.filter(plugin => plugin !== insertImageInputRule))
    .use(safeMarkdown)
    .use(history)
    .use($prose(() => new Plugin({
      view: () => ({
        update(view, previous) {
          const { doc, selection, storedMarks } = view.state;
          if (doc !== previous.doc || !selection.eq(previous.selection) || storedMarks !== previous.storedMarks) onChange();
        }
      })
    })))
    .create();
  const view = editor.action(ctx => ctx.get(editorViewCtx));
  let editable = false;

  function selectedLink() {
    const { $from } = view.state.selection;
    const link = view.state.schema.marks.link;
    return link.isInSet($from.marks()) ?? link.isInSet($from.nodeAfter?.marks ?? []);
  }

  return {
    focus: () => view.focus(),
    setEditable(value) {
      if (value === editable) return;
      editable = value;
      view.setProps({
        editable: () => editable,
        attributes: { ...view.props.attributes, 'aria-disabled': String(!editable) }
      });
    },
    getMarkdown: () => editor.action(getMarkdown()),
    clear: () => editor.action(replaceAll('', true)),
    canRun(command) {
      if (command === 'undo') return undoDepth(view.state) > 0;
      if (command === 'redo') return redoDepth(view.state) > 0;
      return true;
    },
    isActive(command) {
      const { doc, schema, selection, storedMarks } = view.state;
      const block = selection.$from.parent.type.name;
      if (command === 'heading') return block === 'heading';
      if (command === 'code') return block === 'code_block';
      if (command !== 'bold' && command !== 'italic') return undefined;
      const mark = schema.marks[command === 'bold' ? 'strong' : 'emphasis'];
      return selection.empty ? !!mark.isInSet(storedMarks ?? selection.$from.marks())
        : doc.rangeHasMark(selection.from, selection.to, mark);
    },
    run(command) {
      if (!editable) return;
      view.focus();
      const block = view.state.selection.$from.parent.type.name;
      if (command === 'heading') return editor.action(callCommand((block === 'heading' ? turnIntoTextCommand : wrapInHeadingCommand).key));
      if (command === 'code') return block === 'code_block'
        ? editor.action(callCommand(turnIntoTextCommand.key))
        : editor.action(callCommand(createCodeBlockCommand.key, 'text'));
      if (Object.hasOwn(commands, command)) return editor.action(callCommand(commands[command].key));
    },
    selectionLink: () => selectedLink()?.attrs.href ?? '',
    setLink(value) {
      const href = linkUrl(value);
      if (!editable || !href) return false;
      const { schema, selection, tr } = view.state;
      const mark = schema.marks.link.create({ href });
      if (selection.empty) {
        const previous = selectedLink();
        const { $from } = selection;
        let from = $from.index();
        if (previous && !previous.isInSet($from.parent.maybeChild(from)?.marks ?? [])) from--;
        if (previous && from >= 0) {
          let to = from + 1;
          while (from > 0 && previous.isInSet($from.parent.child(from - 1).marks)) from--;
          while (to < $from.parent.childCount && previous.isInSet($from.parent.child(to).marks)) to++;
          tr.addMark($from.posAtIndex(from), $from.posAtIndex(to), mark);
        } else tr.replaceSelectionWith(schema.text(href, [mark]), false).removeStoredMark(schema.marks.link);
      } else tr.addMark(selection.from, selection.to, mark);
      view.dispatch(tr.scrollIntoView());
      view.focus();
      return true;
    }
  };
}
