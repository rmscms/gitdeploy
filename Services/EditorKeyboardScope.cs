using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using Microsoft.Web.WebView2.Wpf;

namespace GitDeployPro.Services
{
    /// <summary>
    /// Routes arrow / home / end keys into the code editor.
    /// WPF directional navigation and the FTP tree otherwise keep those keys.
    /// </summary>
    public static class EditorKeyboardScope
    {
        private static readonly List<Target> Targets = new();

        public static void ArmMonaco(WebView2? view)
        {
            if (view == null)
            {
                return;
            }

            Remove(t => t.WebView == view);
            Targets.Add(new Target { WebView = view, Kind = TargetKind.Monaco });
        }

        public static void ArmAvalon(TextEditor? editor)
        {
            if (editor == null)
            {
                return;
            }

            Remove(t => t.Avalon == editor);
            Targets.Add(new Target { Avalon = editor, Kind = TargetKind.Avalon });
        }

        public static void ArmTextBox(System.Windows.Controls.TextBox? box)
        {
            if (box == null)
            {
                return;
            }

            Remove(t => t.Box == box);
            Targets.Add(new Target { Box = box, Kind = TargetKind.TextBox });
        }

        public static void Disarm()
        {
            Targets.Clear();
        }

        public static void DisarmMonaco(WebView2? view)
        {
            if (view != null)
            {
                Remove(t => t.WebView == view);
            }
        }

        public static bool TryHandlePreviewKey(System.Windows.Input.KeyEventArgs e)
        {
            if (e == null || Targets.Count == 0)
            {
                return false;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (!IsNavigationKey(key))
            {
                return false;
            }

            if (IsPlainTextInput(Keyboard.FocusedElement) || IsPlainTextInput(e.OriginalSource as IInputElement))
            {
                return false;
            }

            var target = FindActiveTarget();
            if (target == null)
            {
                return false;
            }

            if (target.Kind == TargetKind.Avalon && target.Avalon != null && target.Avalon.IsKeyboardFocusWithin)
            {
                return false;
            }

            if (target.Kind == TargetKind.TextBox && target.Box != null && target.Box.IsKeyboardFocusWithin)
            {
                return false;
            }

            e.Handled = true;
            var mods = Keyboard.Modifiers;
            switch (target.Kind)
            {
                case TargetKind.Monaco:
                    MoveMonaco(target.WebView, key, mods);
                    break;
                case TargetKind.Avalon:
                    MoveAvalon(target.Avalon, key, mods);
                    break;
                case TargetKind.TextBox:
                    MoveTextBox(target.Box, key, mods);
                    break;
            }

            return true;
        }

        private static Target? FindActiveTarget()
        {
            for (var i = Targets.Count - 1; i >= 0; i--)
            {
                var target = Targets[i];
                if (target.Kind == TargetKind.Monaco
                    && target.WebView is { IsVisible: true, IsEnabled: true, CoreWebView2: not null })
                {
                    return target;
                }

                if (target.Kind == TargetKind.Avalon
                    && target.Avalon is { IsVisible: true, IsEnabled: true })
                {
                    return target;
                }

                if (target.Kind == TargetKind.TextBox
                    && target.Box is { IsVisible: true, IsEnabled: true })
                {
                    return target;
                }
            }

            return null;
        }

        private static void MoveMonaco(WebView2? view, Key key, ModifierKeys mods)
        {
            if (view?.CoreWebView2 == null)
            {
                return;
            }

            view.Focus();
            var direction = KeyToDirection(key);
            var shift = (mods & ModifierKeys.Shift) == ModifierKeys.Shift ? "true" : "false";
            var ctrl = (mods & ModifierKeys.Control) == ModifierKeys.Control ? "true" : "false";
            _ = view.CoreWebView2.ExecuteScriptAsync(
                $"window.__moveCursor && window.__moveCursor('{direction}',{{shift:{shift},ctrl:{ctrl}}});");
        }

        private static void MoveAvalon(TextEditor? editor, Key key, ModifierKeys mods)
        {
            if (editor?.TextArea == null)
            {
                return;
            }

            editor.TextArea.Focus();
            var command = ResolveEditingCommand(key, mods);
            command?.Execute(null, editor.TextArea);
        }

        private static void MoveTextBox(System.Windows.Controls.TextBox? box, Key key, ModifierKeys mods)
        {
            if (box == null)
            {
                return;
            }

            box.Focus();
            var command = ResolveEditingCommand(key, mods);
            command?.Execute(null, box);
        }

        private static RoutedUICommand? ResolveEditingCommand(Key key, ModifierKeys mods)
        {
            var shift = (mods & ModifierKeys.Shift) == ModifierKeys.Shift;
            var ctrl = (mods & ModifierKeys.Control) == ModifierKeys.Control;
            return key switch
            {
                Key.Left when ctrl && shift => EditingCommands.SelectLeftByWord,
                Key.Right when ctrl && shift => EditingCommands.SelectRightByWord,
                Key.Left when ctrl => EditingCommands.MoveLeftByWord,
                Key.Right when ctrl => EditingCommands.MoveRightByWord,
                Key.Left when shift => EditingCommands.SelectLeftByCharacter,
                Key.Right when shift => EditingCommands.SelectRightByCharacter,
                Key.Left => EditingCommands.MoveLeftByCharacter,
                Key.Right => EditingCommands.MoveRightByCharacter,
                Key.Up when shift => EditingCommands.SelectUpByLine,
                Key.Down when shift => EditingCommands.SelectDownByLine,
                Key.Up => EditingCommands.MoveUpByLine,
                Key.Down => EditingCommands.MoveDownByLine,
                Key.Home when ctrl && shift => EditingCommands.SelectToDocumentStart,
                Key.End when ctrl && shift => EditingCommands.SelectToDocumentEnd,
                Key.Home when ctrl => EditingCommands.MoveToDocumentStart,
                Key.End when ctrl => EditingCommands.MoveToDocumentEnd,
                Key.Home when shift => EditingCommands.SelectToLineStart,
                Key.End when shift => EditingCommands.SelectToLineEnd,
                Key.Home => EditingCommands.MoveToLineStart,
                Key.End => EditingCommands.MoveToLineEnd,
                Key.PageUp when shift => EditingCommands.SelectUpByPage,
                Key.PageDown when shift => EditingCommands.SelectDownByPage,
                Key.PageUp => EditingCommands.MoveUpByPage,
                Key.PageDown => EditingCommands.MoveDownByPage,
                _ => null
            };
        }

        private static string KeyToDirection(Key key) => key switch
        {
            Key.Up => "up",
            Key.Down => "down",
            Key.Left => "left",
            Key.Right => "right",
            Key.Home => "home",
            Key.End => "end",
            Key.PageUp => "pageup",
            Key.PageDown => "pagedown",
            _ => "down"
        };

        private static bool IsNavigationKey(Key key)
            => key is Key.Up or Key.Down or Key.Left or Key.Right
                or Key.Home or Key.End or Key.PageUp or Key.PageDown;

        private static bool IsPlainTextInput(IInputElement? element)
        {
            return element is System.Windows.Controls.TextBox
                or PasswordBox
                or System.Windows.Controls.ComboBox
                or System.Windows.Controls.ComboBoxItem;
        }

        private static void Remove(Predicate<Target> match)
        {
            Targets.RemoveAll(match);
        }

        private enum TargetKind
        {
            Monaco,
            Avalon,
            TextBox
        }

        private sealed class Target
        {
            public TargetKind Kind { get; init; }
            public WebView2? WebView { get; init; }
            public TextEditor? Avalon { get; init; }
            public System.Windows.Controls.TextBox? Box { get; init; }
        }
    }
}
