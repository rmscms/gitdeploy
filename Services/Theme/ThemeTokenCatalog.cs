using System.Collections.Generic;

namespace GitDeployPro.Services.Theme
{
    /// <summary>Authoritative Deploy theme token paths and Default/Dark seed values.</summary>
    public static class ThemeTokenCatalog
    {
        public const string SchemaId = "gitdeploypro-theme/v1";

        public static readonly string[] RequiredPaletteKeys =
        {
            "Surface.Base", "Surface.Shell", "Surface.Raised", "Surface.Card", "Surface.Input", "Surface.Overlay",
            "Border.Subtle", "Border.Strong",
            "Text.Primary", "Text.Secondary", "Text.Muted", "Text.Inverse",
            "Accent.Primary", "Accent.PrimaryHover", "Accent.Secondary", "Accent.SecondaryHover",
            "Status.Success", "Status.Warning", "Status.Error", "Status.Info",
            "Status.SuccessSurface", "Status.WarningSurface", "Status.ErrorSurface", "Status.InfoSurface",
            "State.Hover", "State.Pressed", "State.DisabledOverlay",
            "Shadow.Color", "Log.Success"
        };

        public static readonly string[] FileTypeKeys =
        {
            "directory", "placeholder", "defaultFile", "config", "php", "js", "ts", "css", "html",
            "json", "sql", "img", "zip", "md", "txt", "sh"
        };

        public static ThemePack CreateDefaultPack()
        {
            var pack = CreateBasePack("default", "Default", BuildDefaultPalette());
            FillDeployDefaults(pack, isDark: false);
            return pack;
        }

        public static ThemePack CreateDarkPack()
        {
            var pack = CreateBasePack("dark", "Dark", BuildDarkPalette());
            FillDeployDefaults(pack, isDark: true);
            return pack;
        }

        private static ThemePack CreateBasePack(string id, string displayName, Dictionary<string, string> palette)
            => new()
            {
                Schema = SchemaId,
                Id = id,
                DisplayName = displayName,
                Version = "1",
                Author = "GitDeployPro",
                Palette = palette
            };

        public static Dictionary<string, string> BuildDefaultPalette() => new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Surface.Base"] = "#171A1F",
            ["Surface.Shell"] = "#1E232C",
            ["Surface.Raised"] = "#232A35",
            ["Surface.Card"] = "#29313D",
            ["Surface.Input"] = "#202733",
            ["Surface.Overlay"] = "#B5181D25",
            ["Border.Subtle"] = "#36404F",
            ["Border.Strong"] = "#4B5A70",
            ["Text.Primary"] = "#F4F7FF",
            ["Text.Secondary"] = "#D4DEEE",
            ["Text.Muted"] = "#95A3BA",
            ["Text.Inverse"] = "#10131A",
            ["Accent.Primary"] = "#5C8DFF",
            ["Accent.PrimaryHover"] = "#78A0FF",
            ["Accent.Secondary"] = "#7B61FF",
            ["Accent.SecondaryHover"] = "#9683FF",
            ["Status.Success"] = "#4CD27E",
            ["Status.Warning"] = "#FFBF47",
            ["Status.Error"] = "#FF6C7A",
            ["Status.Info"] = "#70C2FF",
            ["Status.SuccessSurface"] = "#20382C",
            ["Status.WarningSurface"] = "#3A301F",
            ["Status.ErrorSurface"] = "#3A2228",
            ["Status.InfoSurface"] = "#1F2F43",
            ["State.Hover"] = "#1FFFFFFF",
            ["State.Pressed"] = "#2FFFFFFF",
            ["State.DisabledOverlay"] = "#AA2D3442",
            ["Shadow.Color"] = "#66000000",
            ["Log.Success"] = "#65FFB2"
        };

        public static Dictionary<string, string> BuildDarkPalette() => new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["Surface.Base"] = "#000000",
            ["Surface.Shell"] = "#0A0A0A",
            ["Surface.Raised"] = "#111111",
            ["Surface.Card"] = "#141414",
            ["Surface.Input"] = "#0D0D0D",
            ["Surface.Overlay"] = "#CC000000",
            ["Border.Subtle"] = "#222222",
            ["Border.Strong"] = "#333333",
            ["Text.Primary"] = "#E8E8E8",
            ["Text.Secondary"] = "#C8C8C8",
            ["Text.Muted"] = "#8A8A8A",
            ["Text.Inverse"] = "#000000",
            ["Accent.Primary"] = "#4C8DFF",
            ["Accent.PrimaryHover"] = "#6AA0FF",
            ["Accent.Secondary"] = "#7B61FF",
            ["Accent.SecondaryHover"] = "#9683FF",
            ["Status.Success"] = "#3DDC84",
            ["Status.Warning"] = "#E6B84D",
            ["Status.Error"] = "#FF5C6A",
            ["Status.Info"] = "#5BB8FF",
            ["Status.SuccessSurface"] = "#0A1A12",
            ["Status.WarningSurface"] = "#1A1408",
            ["Status.ErrorSurface"] = "#1A0A0C",
            ["Status.InfoSurface"] = "#0A121A",
            ["State.Hover"] = "#14FFFFFF",
            ["State.Pressed"] = "#22FFFFFF",
            ["State.DisabledOverlay"] = "#AA0A0A0A",
            ["Shadow.Color"] = "#99000000",
            ["Log.Success"] = "#5CFFB0"
        };

        private static void FillDeployDefaults(ThemePack pack, bool isDark)
        {
            pack.Deploy.Shell = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["pageBackground"] = "@Surface.Base",
                ["railBackground"] = "@Surface.Shell",
                ["dockBackground"] = "@Surface.Card",
                ["splitter"] = "@Border.Strong",
                ["bottomResizeGrip"] = "@Border.Strong",
                ["bottomResizeBorder"] = "@Border.Subtle"
            };

            pack.Deploy.Header = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "@Text.Primary",
                ["subtitle"] = "@Text.Muted"
            };

            pack.Deploy.Branches = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["summaryBackground"] = "@Surface.Input",
                ["summaryForeground"] = "@Text.Secondary",
                ["actionBackground"] = "@Accent.Secondary",
                ["actionForeground"] = "@Text.Primary",
                ["rollbackBackground"] = "@Surface.Raised",
                ["pushBadgeBackground"] = "@Status.WarningSurface",
                ["pushBadgeForeground"] = "@Status.Warning"
            };

            pack.Deploy.ChangedFiles = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["rowBackground"] = "@Surface.Card",
                ["statusAdded"] = "#2E7D32",
                ["statusModified"] = "#FF8F00",
                ["statusDeleted"] = "#C62828",
                ["statusDefault"] = "#808080"
            };

            pack.Deploy.DirectUpload = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["statusCardBackground"] = "@Surface.Input",
                ["treeSelection"] = isDark ? "#222222" : "#2A3A52",
                ["treeHover"] = isDark ? "#1A1A1A" : "#1E2A3C",
                ["folderIcon"] = "@Status.Warning",
                ["fileIcon"] = "@Text.Secondary",
                ["mappedRowBackground"] = "@Status.SuccessSurface",
                ["gitClean"] = "@Status.Success",
                ["gitModified"] = "@Status.Error",
                ["gitUntracked"] = "@Status.Info",
                ["gitIgnored"] = "@Text.Muted",
                ["gitConflicted"] = "@Status.Warning"
            };

            pack.Deploy.Ftp.Chrome = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["treeSelection"] = isDark ? "#1A3A6E" : "#2B65D9",
                ["treeSelectionText"] = "#FFFFFF",
                ["overlay"] = isDark ? "#CC000000" : "#AF151C26",
                ["skeleton1"] = isDark ? "#222222" : "#213D4F66",
                ["skeleton2"] = isDark ? "#1A1A1A" : "#1B3D4F66",
                ["skeleton3"] = isDark ? "#141414" : "#163D4F66"
            };

            pack.Deploy.Ftp.FileTypes = BuildDefaultFileTypes();

            pack.Deploy.Logs = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["background"] = "@Surface.Input",
                ["foreground"] = "@Text.Secondary",
                ["success"] = "@Log.Success",
                ["warning"] = "@Status.Warning",
                ["error"] = "@Status.Error"
            };

            pack.Deploy.Terminal = new ThemePackTerminalSection
            {
                HostBackground = isDark ? "#000000" : "#0C0C0C",
                StatusConnected = "#32CD32",
                StatusDisconnected = "#808080",
                StatusConnecting = "#FF4500",
                StatusError = "#FF0000",
                PresetsDrawerBackground = isDark ? "#0A0A0A" : "@Surface.Card",
                PresetsDrawerBorder = "@Border.Subtle",
                PresetsHeaderForeground = "@Text.Primary",
                PresetsChipBackground = isDark ? "#141414" : "@Surface.Raised",
                SuggestionGhost = isDark ? "#E6D07A" : "#C9A227",
                SuggestionListBackground = isDark ? "#141414" : "@Surface.Raised",
                SuggestionListActive = isDark ? "#2A2618" : "#3A3418",
                SuggestionListBorder = "@Border.Subtle",
                Xterm = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
                {
                    ["background"] = isDark ? "#000000" : "#1e1e1e",
                    ["foreground"] = isDark ? "#E8E8E8" : "#d4d4d4",
                    ["cursor"] = "#00ff00",
                    ["selectionBackground"] = "rgba(128,128,128,0.35)"
                },
                TextPresets = new List<string> { "#D4D4D4", "#FFFFFF", "#00FF00", "#00FFFF", "#FFFF00" }
            };

            pack.Deploy.Editor = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["webviewBackground"] = isDark ? "#000000" : "#FF1E1E1E",
                ["monacoTheme"] = "vs-dark",
                ["fallbackBackground"] = isDark ? "#000000" : "#1E1E1E",
                ["fallbackForeground"] = "@Text.Primary",
                ["completionBackground"] = isDark ? "#0A0A0A" : "#1E1E1E",
                ["completionListBackground"] = isDark ? "#121212" : "#252526",
                ["completionBorder"] = "@Border.Strong",
                ["syntaxComment"] = "#6A9955",
                ["syntaxString"] = "#CE9178",
                ["syntaxKeyword"] = "#569CD6",
                ["syntaxFunction"] = "#DCDCAA",
                ["syntaxNumber"] = "#B5CEA8",
                ["syntaxDataType"] = "#4EC9B0",
                ["tabClose"] = "@Text.Muted",
                ["tabCloseOnAccent"] = "#FFFFFFFF",
                ["actionActiveForeground"] = "@Status.Warning",
                ["actionActiveBackground"] = "@Status.WarningSurface",
                ["actionActiveBorder"] = "@Status.Warning"
            };

            pack.Deploy.Diff = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["contextBg"] = isDark ? "#0A0A0A" : "#101010",
                ["hunkBg"] = isDark ? "#141414" : "#242424",
                ["metaBg"] = isDark ? "#101820" : "#1F2633",
                ["addedBg"] = isDark ? "#0A1A12" : "#0F2A1A",
                ["removedBg"] = isDark ? "#1A0A0C" : "#2A1414",
                ["headerBg"] = isDark ? "#000000" : "#1E1E1E"
            };
        }

        public static Dictionary<string, ThemePackFileTypeVisual> BuildDefaultFileTypes()
            => new(System.StringComparer.OrdinalIgnoreCase)
            {
                ["directory"] = V("#FFE7B95B", "#1F4A391A", "#3A7C5E2A", "#FFE7B95B"),
                ["placeholder"] = V("#FF8B96A6", "#1F2C3440", "#3A3D4C5F", "#FF8B96A6"),
                ["defaultFile"] = V("#FFAAB5C4", "#1F2E3948", "#3A3D5268", "#FFAAB5C4"),
                ["config"] = V("#FF74B6F5", "#1F1E3752", "#3A2A4D73", "#FF74B6F5"),
                ["php"] = V("#FFB0A3FF", "#1F3E3658", "#3A5A4B87", "#FFB0A3FF"),
                ["js"] = V("#FFF7DF5E", "#1F4A4212", "#3A7B6A1D", "#FFF7DF5E"),
                ["ts"] = V("#FF74A9FF", "#1F1A3252", "#3A264A75", "#FF74A9FF"),
                ["css"] = V("#FF64D2FF", "#1F193C50", "#3A2A5C75", "#FF64D2FF"),
                ["html"] = V("#FFFFAE72", "#1F4A2E18", "#3A6F4528", "#FFFFAE72"),
                ["json"] = V("#FF7EE2A8", "#1F1E4030", "#3A2D654A", "#FF7EE2A8"),
                ["sql"] = V("#FFFF9A86", "#1F4C2822", "#3A7B3D35", "#FFFF9A86"),
                ["img"] = V("#FFFFA5D6", "#1F4A203A", "#3A75305C", "#FFFFA5D6"),
                ["zip"] = V("#FFC8A879", "#1F3D2B1F", "#3A5D4330", "#FFC8A879"),
                ["md"] = V("#FF8FC4FF", "#1F1F3755", "#3A305079", "#FF8FC4FF"),
                ["txt"] = V("#FFB7C0CD", "#1F2A3342", "#3A3E4D62", "#FFB7C0CD"),
                ["sh"] = V("#FF8EE39A", "#1F1A3D29", "#3A28613F", "#FF8EE39A")
            };

        private static ThemePackFileTypeVisual V(string icon, string bg, string border, string fg)
            => new() { Icon = icon, BadgeBg = bg, BadgeBorder = border, BadgeFg = fg };

        public static HashSet<string> GetKnownDeployFlatKeys()
        {
            return new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "shell.pageBackground", "shell.railBackground", "shell.dockBackground", "shell.splitter",
                "shell.bottomResizeGrip", "shell.bottomResizeBorder",
                "header.title", "header.subtitle",
                "branches.summaryBackground", "branches.summaryForeground", "branches.actionBackground",
                "branches.actionForeground", "branches.rollbackBackground", "branches.pushBadgeBackground",
                "branches.pushBadgeForeground",
                "changedFiles.rowBackground", "changedFiles.statusAdded", "changedFiles.statusModified",
                "changedFiles.statusDeleted", "changedFiles.statusDefault",
                "directUpload.statusCardBackground", "directUpload.treeSelection", "directUpload.treeHover",
                "directUpload.folderIcon", "directUpload.fileIcon", "directUpload.mappedRowBackground",
                "directUpload.gitClean", "directUpload.gitModified", "directUpload.gitUntracked",
                "directUpload.gitIgnored", "directUpload.gitConflicted",
                "ftp.chrome.treeSelection", "ftp.chrome.treeSelectionText", "ftp.chrome.overlay",
                "ftp.chrome.skeleton1", "ftp.chrome.skeleton2", "ftp.chrome.skeleton3",
                "logs.background", "logs.foreground", "logs.success", "logs.warning", "logs.error",
                "terminal.hostBackground", "terminal.statusConnected", "terminal.statusDisconnected",
                "terminal.statusConnecting", "terminal.statusError",
                "terminal.presetsDrawerBackground", "terminal.presetsDrawerBorder",
                "terminal.presetsHeaderForeground", "terminal.presetsChipBackground",
                "terminal.suggestionGhost", "terminal.suggestionListBackground",
                "terminal.suggestionListActive", "terminal.suggestionListBorder",
                "terminal.xterm.background", "terminal.xterm.foreground", "terminal.xterm.cursor",
                "terminal.xterm.selectionBackground",
                "editor.webviewBackground", "editor.monacoTheme", "editor.fallbackBackground",
                "editor.fallbackForeground",
                "editor.completionBackground", "editor.completionListBackground", "editor.completionBorder",
                "editor.syntaxComment", "editor.syntaxString", "editor.syntaxKeyword",
                "editor.syntaxFunction", "editor.syntaxNumber", "editor.syntaxDataType",
                "editor.tabClose", "editor.tabCloseOnAccent",
                "editor.actionActiveForeground", "editor.actionActiveBackground", "editor.actionActiveBorder",
                "diff.contextBg", "diff.hunkBg", "diff.metaBg", "diff.addedBg", "diff.removedBg", "diff.headerBg"
            };
        }
    }
}
