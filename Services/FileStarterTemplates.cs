using System;
using System.Collections.Generic;
using System.IO;

namespace GitDeployPro.Services
{
    /// <summary>
    /// Starter snippets for newly created files based on extension.
    /// </summary>
    public static class FileStarterTemplates
    {
        private static readonly Dictionary<string, string> Templates = new(StringComparer.OrdinalIgnoreCase)
        {
            [".php"] = "<?php\n\n",
            [".phtml"] = "<?php\n\n",
            [".html"] = "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n    <meta charset=\"UTF-8\">\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n    <title>Document</title>\n</head>\n<body>\n\n</body>\n</html>\n",
            [".htm"] = "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n    <meta charset=\"UTF-8\">\n    <title>Document</title>\n</head>\n<body>\n\n</body>\n</html>\n",
            [".css"] = "/* styles */\n\n",
            [".scss"] = "// styles\n\n",
            [".js"] = "'use strict';\n\n",
            [".mjs"] = "'use strict';\n\n",
            [".ts"] = "// TypeScript\n\n",
            [".tsx"] = "import React from 'react';\n\nexport default function Component() {\n  return (\n    <div></div>\n  );\n}\n",
            [".jsx"] = "import React from 'react';\n\nexport default function Component() {\n  return (\n    <div></div>\n  );\n}\n",
            [".cs"] = "using System;\n\nnamespace App;\n\n",
            [".py"] = "#!/usr/bin/env python3\n\n",
            [".sh"] = "#!/usr/bin/env bash\nset -euo pipefail\n\n",
            [".bash"] = "#!/usr/bin/env bash\nset -euo pipefail\n\n",
            [".ps1"] = "# PowerShell\n\n",
            [".sql"] = "-- SQL\n\n",
            [".json"] = "{\n\n}\n",
            [".xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n",
            [".xaml"] = "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n\n</UserControl>\n",
            [".md"] = "# Title\n\n",
            [".yml"] = "# config\n\n",
            [".yaml"] = "# config\n\n",
            [".env"] = "# Environment\n\n",
            [".vue"] = "<template>\n  <div></div>\n</template>\n\n<script setup>\n\n</script>\n\n<style scoped>\n\n</style>\n",
            [".blade.php"] = "@php\n\n@endphp\n",
            [".cshtml"] = "@{\n}\n\n",
            [".razor"] = "@*\n*@\n\n"
        };

        public static string GetStarterContent(string? fileNameOrExtension)
        {
            var ext = NormalizeExtension(fileNameOrExtension);
            if (string.IsNullOrEmpty(ext))
            {
                return string.Empty;
            }

            // Prefer compound extensions like .blade.php when a full file name is given.
            if (!string.IsNullOrWhiteSpace(fileNameOrExtension)
                && fileNameOrExtension.Contains('.')
                && !fileNameOrExtension.StartsWith('.'))
            {
                var lower = fileNameOrExtension.ToLowerInvariant();
                foreach (var key in Templates.Keys)
                {
                    if (key.Contains('.') && lower.EndsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        return Templates[key];
                    }
                }
            }

            return Templates.TryGetValue(ext, out var content) ? content : string.Empty;
        }

        private static string NormalizeExtension(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            if (trimmed.Contains('/') || trimmed.Contains('\\'))
            {
                trimmed = Path.GetFileName(trimmed);
            }

            if (trimmed.StartsWith('.'))
            {
                return trimmed.ToLowerInvariant();
            }

            if (trimmed.Contains('.'))
            {
                return Path.GetExtension(trimmed).ToLowerInvariant();
            }

            return "." + trimmed.ToLowerInvariant();
        }
    }
}
