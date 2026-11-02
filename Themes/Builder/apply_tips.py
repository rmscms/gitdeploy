"""Adds a Persian tooltip ({loc:T ui.tip.<id>}) to every Button/ToggleButton/RepeatButton in the XAML files.

Tooltip texts live in Themes/Builder/tips_fa.json (id = click-handler name without "_Click", or x:Name).
Keys are written to Resources/Localization/fa.json (Persian) and en.json (fallback). Every ".tip." key is rendered
in Persian by LocalizationService, whatever the UI language is.

Usage: python Themes/Builder/apply_tips.py [--dry]
"""
import glob
import io
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
DRY = '--dry' in sys.argv
TIPS = json.load(open(os.path.join(ROOT, 'Themes', 'Builder', 'tips_fa.json'), encoding='utf-8'))

# (xaml file name, handler/name) -> tips_fa.json id, when the generic id would be inaccurate for that window
OVERRIDE = {
    ('BackupArtifactsWindow.xaml', 'RefreshButton_Click'): 'BackupArtifacts.RefreshButton',
    ('ConnectionManagerWindow.xaml', 'TestButton_Click'): 'TestButton_Conn',
    ('ConnectionManagerWindow.xaml', 'BrowseButton_Click'): 'BrowseButton',
    ('RemoteFolderPickerWindow.xaml', 'OkButton_Click'): 'OkButton_Move',
    ('ManageWorkspaceWindow.xaml', 'AddButton_Click'): 'ManageWorkspaceAdd',
    ('ManageWorkspaceWindow.xaml', 'RemoveButton_Click'): 'ManageWorkspaceRemove',
    ('InstallLocationWindow.xaml', 'BrowseButton_Click'): 'BrowseButton_Click',
    ('SshKeySetupWindow.xaml', 'TestButton_Click'): 'TestSshKeyButton',
    ('ProcessMonitorWindow.xaml', 'ConnectButton_Click'): 'ConnectButton',
    ('CodeViewerWindow.xaml', 'SaveButton_Click'): 'SaveButton',
    ('NewConnectionWindow.xaml', 'ConnectButton_Click'): 'ConnectButton',
    ('SavedConnectionsWindow.xaml', 'ConnectButton_Click'): 'ConnectButton',
    ('SavedConnectionsWindow.xaml', 'ManageButton_Click'): 'ManageProfilesButton',
    ('TerminalSuggestionDialog.xaml', 'OkButton_Click'): 'Save',
    ('TelegramTerminalCommandDialog.xaml', 'OkButton_Click'): 'Save',
    ('NewFileDialog.xaml', 'OkButton_Click'): 'OkButton',
    ('SyncPreviewWindow.xaml', 'Confirm_Click'): 'Confirm',
    ('SyncManifestEditorWindow.xaml', 'Save_Click'): 'Save',
    ('HistoryDetailsWindow.xaml', 'Rollback_Click'): 'Rollback',
}

FILES = sorted(glob.glob(os.path.join(ROOT, 'Pages', '*.xaml')) + glob.glob(os.path.join(ROOT, 'Controls', '*.xaml'))
               + glob.glob(os.path.join(ROOT, 'Windows', '*.xaml')) + [os.path.join(ROOT, 'MainWindow.xaml')])

used = {}
left = []
changed_files = 0
rx = re.compile(r'<(Button|ToggleButton|RepeatButton)\b[^>]*?>', re.S)


def human(i):
    return re.sub(r'(?<!^)(?=[A-Z])', ' ', i.replace('_', ' ')).strip()


for path in FILES:
    raw = open(path, 'rb').read()
    bom = raw[:3] == b'\xef\xbb\xbf'
    s = raw.decode('utf-8-sig')
    crlf = '\r\n' in s
    s = s.replace('\r\n', '\n')
    base = os.path.basename(path)
    out = []
    last = 0
    touched = False
    for m in rx.finditer(s):
        tag = m.group(0)
        tt = re.search(r'\s+ToolTip="([^"]*)"', tag)
        if tt and tt.group(1).startswith('{'):
            continue
        click = re.search(r'Click="([^"]+)"', tag)
        name = re.search(r'x:Name="([^"]+)"', tag)
        cands = []
        if click:
            cands.append(OVERRIDE.get((base, click.group(1)), click.group(1).replace('_Click', '')))
        if name:
            cands.append(OVERRIDE.get((base, name.group(1)), name.group(1)))
        if click:
            cands.append(click.group(1))
        ident = next((c for c in cands if c in TIPS), None)
        if ident is None:
            line = s[:m.start()].count('\n') + 1
            left.append(f'{base}:{line} name={name.group(1) if name else None} click={click.group(1) if click else None}')
            continue
        key = f'ui.tip.{ident}'
        used[key] = ident
        new_tag = re.sub(r'\s+ToolTip="[^"]*"', '', tag)
        new_tag = new_tag.replace('<' + m.group(1), '<' + m.group(1) + f' ToolTip="{{loc:T {key}}}"', 1)
        out.append(s[last:m.start()])
        out.append(new_tag)
        last = m.end()
        touched = True
    out.append(s[last:])
    ns = ''.join(out)
    if touched and 'xmlns:loc=' not in ns:
        # add the loc namespace on the root element
        ns = re.sub(r'(<(?:Window|Page|UserControl)\b)', r'\1\n        xmlns:loc="clr-namespace:GitDeployPro.Services.Localization"', ns, count=1)
    if touched and not DRY:
        if crlf:
            ns = ns.replace('\n', '\r\n')
        open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + ns.encode('utf-8'))
        changed_files += 1


def add_keys(lang_file, make_value):
    p = os.path.join(ROOT, 'Resources', 'Localization', lang_file)
    raw = open(p, 'rb').read()
    bom = raw[:3] == b'\xef\xbb\xbf'
    s = raw.decode('utf-8-sig')
    crlf = '\r\n' in s
    s = s.replace('\r\n', '\n')
    existing = json.loads(s)
    lines = []
    for key, ident in sorted(used.items()):
        if key in existing:
            continue
        lines.append('  ' + json.dumps(key) + ': ' + json.dumps(make_value(ident), ensure_ascii=False))
    if not lines:
        return 0
    end = s.rstrip().rfind('}')
    head = s[:end].rstrip()
    if not head.endswith(','):
        head += ','
    s2 = head + '\n' + ',\n'.join(lines) + '\n}\n'
    json.loads(s2)
    if crlf:
        s2 = s2.replace('\n', '\r\n')
    if not DRY:
        open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s2.encode('utf-8'))
    return len(lines)


nfa = add_keys('fa.json', lambda i: TIPS[i])
nen = add_keys('en.json', lambda i: human(i.split('.')[-1]))
print(f'files changed: {changed_files}, tooltip keys added fa={nfa} en={nen}, buttons without tooltip text: {len(left)}')
for l in left:
    print('  LEFT', l)
