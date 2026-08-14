using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentFTP.Exceptions;
using GitDeployPro.Services.Localization;
using Renci.SshNet.Common;

namespace GitDeployPro.Services.Remote
{
    public static class RemoteTransferErrorFormatter
    {
        private static readonly string[] GenericPhrases =
        {
            "See InnerException for more info",
            "Error while uploading the file to the server."
        };

        private static readonly string[] ProtectedFileNames =
        {
            ".env", ".htaccess", ".htpasswd", "web.config"
        };

        public static string Format(
            Exception exception,
            string? fileName = null,
            string? remotePath = null,
            string? protocol = null,
            string? profileName = null)
        {
            var rootCause = ResolveRootCause(exception);
            var displayName = string.IsNullOrWhiteSpace(fileName)
                ? Path.GetFileName(remotePath ?? string.Empty)
                : fileName.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = "file";
            }

            var lines = new List<string>
            {
                Loc.T("deploy.upload.failedTitle", displayName),
                Loc.T("deploy.upload.couldNotWrite"),
                "  " + rootCause
            };

            if (!string.IsNullOrWhiteSpace(remotePath))
            {
                lines.Add(Loc.T("deploy.upload.remote", remotePath.Trim()));
            }

            var protocolLabel = BuildProtocolLabel(protocol, profileName);
            if (!string.IsNullOrWhiteSpace(protocolLabel))
            {
                lines.Add(Loc.T("deploy.upload.protocol", protocolLabel));
            }

            if (IsPermissionDenied(rootCause, exception))
            {
                var protectedName = FindProtectedFileName(displayName, remotePath);
                lines.Add(protectedName != null
                    ? Loc.T("deploy.upload.tipProtected", protectedName)
                    : Loc.T("deploy.upload.tipPermission"));
            }

            return string.Join(Environment.NewLine, lines);
        }

        public static string FormatSummary(Exception exception, string? fileName = null)
        {
            var displayName = string.IsNullOrWhiteSpace(fileName) ? "file" : fileName.Trim();
            return $"{Loc.T("deploy.upload.failedTitle", displayName)} — {ResolveRootCause(exception)}";
        }

        public static string ResolveRootCause(Exception exception)
        {
            foreach (var current in EnumerateExceptions(exception))
            {
                if (current is FtpCommandException ftpCommand)
                {
                    var code = string.IsNullOrWhiteSpace(ftpCommand.CompletionCode)
                        ? string.Empty
                        : ftpCommand.CompletionCode.Trim();
                    var message = StripGeneric(ftpCommand.Message);
                    if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(message))
                    {
                        return message.StartsWith(code, StringComparison.Ordinal)
                            ? message
                            : $"{code} {message}";
                    }

                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        return code;
                    }

                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }

                if (current is SftpPermissionDeniedException permission)
                {
                    var message = StripGeneric(permission.Message);
                    return string.IsNullOrWhiteSpace(message)
                        ? "Permission denied"
                        : message;
                }

                var candidate = StripGeneric(current.Message);
                if (!string.IsNullOrWhiteSpace(candidate) && !IsGenericUploadWrapper(candidate))
                {
                    return candidate;
                }
            }

            return StripGeneric(exception?.Message) is { Length: > 0 } fallback
                ? fallback
                : "Unknown upload error";
        }

        private static IEnumerable<Exception> EnumerateExceptions(Exception? exception)
        {
            if (exception == null)
            {
                yield break;
            }

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    foreach (var nested in EnumerateExceptions(inner))
                    {
                        yield return nested;
                    }
                }

                yield break;
            }

            for (var current = exception; current != null; current = current.InnerException)
            {
                yield return current;
            }
        }

        private static string BuildProtocolLabel(string? protocol, string? profileName)
        {
            var proto = string.IsNullOrWhiteSpace(protocol) ? string.Empty : protocol.Trim();
            var profile = string.IsNullOrWhiteSpace(profileName) ? string.Empty : profileName.Trim();
            if (proto.Length == 0)
            {
                return profile;
            }

            return profile.Length == 0 ? proto : $"{proto} ({profile})";
        }

        private static bool IsPermissionDenied(string rootCause, Exception exception)
        {
            if (exception != null && EnumerateExceptions(exception).Any(item => item is SftpPermissionDeniedException))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(rootCause))
            {
                return false;
            }

            return rootCause.Contains("550", StringComparison.OrdinalIgnoreCase)
                   || rootCause.Contains("553", StringComparison.OrdinalIgnoreCase)
                   || rootCause.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                   || rootCause.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                   || rootCause.Contains("not permitted", StringComparison.OrdinalIgnoreCase);
        }

        private static string? FindProtectedFileName(string displayName, string? remotePath)
        {
            foreach (var name in ProtectedFileNames)
            {
                if (displayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }

                if (!string.IsNullOrWhiteSpace(remotePath)
                    && remotePath.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            return null;
        }

        private static string StripGeneric(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            var text = message.Trim();
            var seeInner = text.IndexOf("See InnerException", StringComparison.OrdinalIgnoreCase);
            if (seeInner > 0)
            {
                text = text[..seeInner].Trim().TrimEnd('.', ' ');
            }

            return text;
        }

        private static bool IsGenericUploadWrapper(string message)
        {
            return GenericPhrases.Any(phrase =>
                message.Contains(phrase, StringComparison.OrdinalIgnoreCase));
        }
    }
}
